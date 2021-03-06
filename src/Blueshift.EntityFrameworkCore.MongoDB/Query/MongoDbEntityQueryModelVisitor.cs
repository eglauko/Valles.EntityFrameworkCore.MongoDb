using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Utilities;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.Expressions;
using Remotion.Linq.Clauses.ResultOperators;
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal;
using Microsoft.EntityFrameworkCore.Query.ResultOperators.Internal;
using Remotion.Linq.Clauses.StreamedData;

namespace Blueshift.EntityFrameworkCore.MongoDB.Query
{
    /// <inheritdoc />
    public class MongoDbEntityQueryModelVisitor : EntityQueryModelVisitor
    {
        private readonly IQueryOptimizer _queryOptimizer;
        private readonly IEntityQueryModelVisitorServiceFactory _entityQueryModelVisitorServiceFactory;
        private readonly ModelExpressionApplyingExpressionVisitor _modelExpressionApplyingExpressionVisitor;

        /// <inheritdoc />
        public MongoDbEntityQueryModelVisitor(
            [NotNull] EntityQueryModelVisitorDependencies entityQueryModelVisitorDependencies,
            [NotNull] QueryCompilationContext queryCompilationContext,
            [NotNull] MongoDbEntityQueryModelVisitorDependencies mongoDbEntityQueryModelVisitorDependencies)
            : base(
                Check.NotNull(entityQueryModelVisitorDependencies, nameof(entityQueryModelVisitorDependencies)),
                Check.NotNull(queryCompilationContext, nameof(queryCompilationContext))
            )
        {
            _queryOptimizer = entityQueryModelVisitorDependencies.QueryOptimizer;

            _entityQueryModelVisitorServiceFactory
                = Check.NotNull(mongoDbEntityQueryModelVisitorDependencies,
                        nameof(mongoDbEntityQueryModelVisitorDependencies))
                    .EntityQueryModelVisitorServiceFactory;
            QueryableMethodProvider =
                mongoDbEntityQueryModelVisitorDependencies.QueryableMethodProvider;

            _modelExpressionApplyingExpressionVisitor
                = _entityQueryModelVisitorServiceFactory.CreateModelExpressionApplyingExpressionVisitor(
                    queryCompilationContext,
                    this);
        }

        /// <summary>
        ///     Gets te <see cref="IQueryableMethodProvider"/> used to reference <see cref="IQueryable"/> methods.
        /// </summary>
        public IQueryableMethodProvider QueryableMethodProvider { get; }

        /// <inheritdoc />
        protected override void OptimizeQueryModel(
            QueryModel queryModel,
            bool asyncQuery)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            ExtractQueryAnnotations(queryModel);

            // First pass of optimizations

            _queryOptimizer.Optimize(QueryCompilationContext, queryModel);

            //new NondeterministicResultCheckingVisitor(QueryCompilationContext.Logger, this).VisitQueryModel(queryModel);

            OnBeforeNavigationRewrite(queryModel);

            // Rewrite includes/navigations

            var includeCompiler = _entityQueryModelVisitorServiceFactory.CreateIncludeCompiler(
                QueryCompilationContext);
            includeCompiler.CompileIncludes(queryModel, TrackResults(queryModel), asyncQuery);

            queryModel.TransformExpressions(new CollectionNavigationSubqueryInjector(this).Visit);
            queryModel.TransformExpressions(new CollectionNavigationSetOperatorSubqueryInjector(this).Visit);

            var navigationRewritingExpressionVisitor = _entityQueryModelVisitorServiceFactory
                .CreateNavigationRewritingExpressionVisitor(this);
            navigationRewritingExpressionVisitor.InjectSubqueryToCollectionsInProjection(queryModel);

            var correlatedCollectionFinder = new CorrelatedCollectionFindingExpressionVisitor(
                this,
                TrackResults(queryModel));

            if (!queryModel.ResultOperators.Any(r => r is GroupResultOperator))
            {
                queryModel.SelectClause.TransformExpressions(correlatedCollectionFinder.Visit);
            }

            navigationRewritingExpressionVisitor.Rewrite(queryModel, parentQueryModel: null);

            includeCompiler.CompileIncludes(queryModel, TrackResults(queryModel), asyncQuery);

            navigationRewritingExpressionVisitor.Rewrite(queryModel, parentQueryModel: null);

            //new EntityQsreToKeyAccessConvertingQueryModelVisitor(QueryCompilationContext).VisitQueryModel(queryModel);

            //includeCompiler.RewriteCollectionQueries();

            //includeCompiler.LogIgnoredIncludes();

            _modelExpressionApplyingExpressionVisitor.ApplyModelExpressions(queryModel);

            // Second pass of optimizations

            ExtractQueryAnnotations(queryModel);

            navigationRewritingExpressionVisitor.Rewrite(queryModel, parentQueryModel: null);

            _queryOptimizer.Optimize(QueryCompilationContext, queryModel);

            // Log results

            QueryCompilationContext.Logger.QueryModelOptimized(queryModel);
        }

        private bool TrackResults(QueryModel queryModel)
        {
            // TODO: Unify with QCC

            var lastTrackingModifier
                = QueryCompilationContext.QueryAnnotations
                    .OfType<TrackingResultOperator>()
                    .LastOrDefault();

            return !_modelExpressionApplyingExpressionVisitor.IsViewTypeQuery
                   && !(queryModel.GetOutputDataInfo() is StreamedScalarValueInfo)
                   && (QueryCompilationContext.TrackQueryResults || lastTrackingModifier != null)
                   && (lastTrackingModifier == null
                       || lastTrackingModifier.IsTracking);
        }

        /// <inheritdoc />
        protected override Func<QueryContext, TResults> CreateExecutorLambda<TResults>()
        {
            Expression = _entityQueryModelVisitorServiceFactory
                .CreateRewritingExpressionVisitor()
                .Visit(Expression);

            return base.CreateExecutorLambda<TResults>();
        }

        private Expression ConvertToRelationshipAssignments(Expression expression)
        {
            if (expression is MethodCallExpression methodCallExpression
                && IncludeCompiler.IsIncludeMethod(methodCallExpression))
            {
                expression = (MethodCallExpression) _entityQueryModelVisitorServiceFactory
                    .CreateDenormalizationCompensatingExpressionVisitor()
                    .Visit(methodCallExpression);
            }
            return expression;
        }

        /// <inheritdoc />
        protected override Expression CallCreateTransparentIdentifier(
            Type transparentIdentifierType,
            Expression outerExpression,
            Expression innerExpression)
        {
            ConstructorInfo constructorInfo = transparentIdentifierType.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[]
                {
                    outerExpression.Type,
                    innerExpression.Type
                },
                Array.Empty<ParameterModifier>());
            return Expression.New(
                constructorInfo,
                outerExpression,
                innerExpression);
        }

        /// <inheritdoc />
        public override void VisitMainFromClause(
            MainFromClause fromClause,
            QueryModel queryModel)
        {
            Check.NotNull(fromClause, nameof(fromClause));
            Check.NotNull(queryModel, nameof(queryModel));

            Expression = CompileMainFromClauseExpression(fromClause, queryModel);

            CurrentParameter
                = Expression.Parameter(
                    Expression.Type.GetSequenceType(),
                    fromClause.ItemName);

            QueryCompilationContext.AddOrUpdateMapping(fromClause, CurrentParameter);
        }

        /// <inheritdoc />
        public override void VisitAdditionalFromClause(
            AdditionalFromClause fromClause,
            QueryModel queryModel,
            int index)
        {
            Check.NotNull(fromClause, nameof(fromClause));
            Check.NotNull(queryModel, nameof(queryModel));

            if (QueryableMethodProvider.IsQueryableExpression(Expression))
            {
                var fromExpression
                    = CompileAdditionalFromClauseExpression(fromClause, queryModel);

                var innerItemParameter
                    = Expression.Parameter(
                        fromExpression.Type.GetSequenceType(), fromClause.ItemName);

                var transparentIdentifierType
                    = CreateTransparentIdentifierType(
                        CurrentParameter.Type,
                        innerItemParameter.Type);

                Expression
                    = Expression.Call(
                        QueryableMethodProvider.SelectMany
                            .MakeGenericMethod(
                                CurrentParameter.Type,
                                innerItemParameter.Type,
                                transparentIdentifierType),
                        Expression,
                        QueryableMethodProvider.CreateLambdaExpression(
                            fromExpression,
                            CurrentParameter),
                        QueryableMethodProvider.CreateLambdaExpression(
                            CallCreateTransparentIdentifier(
                                transparentIdentifierType, CurrentParameter, innerItemParameter),
                            CurrentParameter,
                            innerItemParameter));

                IntroduceTransparentScope(fromClause, queryModel, index, transparentIdentifierType);
            }
            else
            {
                base.VisitAdditionalFromClause(fromClause, queryModel, index);
            }
        }

        /// <inheritdoc />
        public override void VisitGroupJoinClause(
            GroupJoinClause groupJoinClause,
            QueryModel queryModel,
            int index)
        {
            Check.NotNull(groupJoinClause, nameof(groupJoinClause));
            Check.NotNull(queryModel, nameof(queryModel));

            if (QueryableMethodProvider.IsQueryableExpression(Expression))
            {
                var outerKeySelectorExpression
                    = ReplaceClauseReferences(groupJoinClause.JoinClause.OuterKeySelector, groupJoinClause);

                var innerSequenceExpression
                    = CompileGroupJoinInnerSequenceExpression(groupJoinClause, queryModel);

                var innerItemParameter
                    = Expression.Parameter(
                        innerSequenceExpression.Type.GetSequenceType(),
                        groupJoinClause.JoinClause.ItemName);

                QueryCompilationContext.AddOrUpdateMapping(groupJoinClause.JoinClause, innerItemParameter);

                var innerKeySelectorExpression
                    = ReplaceClauseReferences(groupJoinClause.JoinClause.InnerKeySelector, groupJoinClause);

                var innerItemsParameter
                    = Expression.Parameter(
                        LinqOperatorProvider.MakeSequenceType(innerItemParameter.Type),
                        groupJoinClause.ItemName);

                var transparentIdentifierType
                    = CreateTransparentIdentifierType(
                        CurrentParameter.Type,
                        innerItemsParameter.Type);

                Expression
                    = Expression.Call(
                        QueryableMethodProvider.GroupJoin
                            .MakeGenericMethod(
                                CurrentParameter.Type,
                                innerItemParameter.Type,
                                outerKeySelectorExpression.Type,
                                transparentIdentifierType),
                        Expression,
                        innerSequenceExpression,
                        QueryableMethodProvider.CreateLambdaExpression(
                            outerKeySelectorExpression,
                            CurrentParameter),
                        QueryableMethodProvider.CreateLambdaExpression(
                            innerKeySelectorExpression,
                            innerItemParameter),
                        QueryableMethodProvider.CreateLambdaExpression(
                            CallCreateTransparentIdentifier(
                                transparentIdentifierType,
                                CurrentParameter,
                                innerItemsParameter),
                            CurrentParameter,
                            innerItemsParameter));

                IntroduceTransparentScope(groupJoinClause, queryModel, index, transparentIdentifierType);
            }
            else
            {
                VisitGroupJoinClause(groupJoinClause, queryModel, index);
            }
        }

        /// <inheritdoc />
        public override void VisitJoinClause(
            JoinClause joinClause,
            QueryModel queryModel,
            int index)
        {
            Check.NotNull(joinClause, nameof(joinClause));
            Check.NotNull(queryModel, nameof(queryModel));

            if (QueryableMethodProvider.IsQueryableExpression(Expression))
            {
                var outerKeySelectorExpression
                    = ReplaceClauseReferences(joinClause.OuterKeySelector, joinClause);

                var innerSequenceExpression
                    = CompileJoinClauseInnerSequenceExpression(joinClause, queryModel);

                var innerItemParameter
                    = Expression.Parameter(
                        innerSequenceExpression.Type.GetSequenceType(), joinClause.ItemName);

                QueryCompilationContext.AddOrUpdateMapping(joinClause, innerItemParameter);

                var innerKeySelectorExpression
                    = ReplaceClauseReferences(joinClause.InnerKeySelector, joinClause);

                var transparentIdentifierType
                    = CreateTransparentIdentifierType(
                        CurrentParameter.Type,
                        innerItemParameter.Type);

                Expression
                    = Expression.Call(
                        QueryableMethodProvider.Join
                            .MakeGenericMethod(
                                CurrentParameter.Type,
                                innerItemParameter.Type,
                                outerKeySelectorExpression.Type,
                                transparentIdentifierType),
                        Expression,
                        innerSequenceExpression,
                        QueryableMethodProvider.CreateLambdaExpression(
                            outerKeySelectorExpression,
                            CurrentParameter),
                        QueryableMethodProvider.CreateLambdaExpression(
                            innerKeySelectorExpression,
                            innerItemParameter),
                        QueryableMethodProvider.CreateLambdaExpression(
                            CallCreateTransparentIdentifier(
                                transparentIdentifierType,
                                CurrentParameter,
                                innerItemParameter),
                            CurrentParameter,
                            innerItemParameter));

                IntroduceTransparentScope(joinClause, queryModel, index, transparentIdentifierType);
            }
            else
            {
                VisitJoinClause(joinClause, queryModel, index);
            }
        }

        /// <inheritdoc />
        public override void VisitOrdering(
            Ordering ordering,
            QueryModel queryModel,
            OrderByClause orderByClause,
            int index)
        {
            Check.NotNull(ordering, nameof(ordering));
            Check.NotNull(queryModel, nameof(queryModel));
            Check.NotNull(orderByClause, nameof(orderByClause));

            if (QueryableMethodProvider.IsQueryableExpression(Expression))
            {
                var expression = ReplaceClauseReferences(ordering.Expression);

                MethodInfo orderingMethodInfo = index == 0
                    ? ordering.OrderingDirection == OrderingDirection.Asc
                        ? QueryableMethodProvider.OrderBy
                        : QueryableMethodProvider.OrderByDescending
                    : ordering.OrderingDirection == OrderingDirection.Asc
                        ? QueryableMethodProvider.ThenBy
                        : QueryableMethodProvider.ThenByDescending;

                Expression
                    = Expression.Call(
                        orderingMethodInfo.MakeGenericMethod(CurrentParameter.Type, expression.Type),
                        Expression,
                        QueryableMethodProvider.CreateLambdaExpression(expression, CurrentParameter));
            }
            else
            {
                base.VisitOrdering(ordering, queryModel, orderByClause, index);
            }
        }

        /// <inheritdoc />
        public override void VisitSelectClause(
            SelectClause selectClause,
            QueryModel queryModel)
        {
            Check.NotNull(selectClause, nameof(selectClause));
            Check.NotNull(queryModel, nameof(queryModel));

            if (selectClause.Selector.Type == Expression.Type.GetSequenceType()
                && selectClause.Selector is QuerySourceReferenceExpression)
            {
                return;
            }

            Expression selector = ReplaceClauseReferences(
                _entityQueryModelVisitorServiceFactory
                    .CreateProjectionExpressionVisitor(
                        this,
                        queryModel.MainFromClause)
                    .Visit(selectClause.Selector),
                inProjection: true);

            if ((Expression.Type.TryGetSequenceType() != null
                 || !(selectClause.Selector is QuerySourceReferenceExpression))
                && !queryModel.ResultOperators
                    .Select(ro => ro.GetType())
                    .Any(
                        t => t == typeof(GroupResultOperator)
                             || t == typeof(AllResultOperator)))
            {
                MethodInfo selectMethodInfo = QueryableMethodProvider.IsQueryableExpression(Expression)
                                              && !(selector is MethodCallExpression methodCallExpression)
                    ? QueryableMethodProvider.Select
                    : LinqOperatorProvider.Select;

                Expression = Expression.Call(
                    selectMethodInfo
                        .MakeGenericMethod(
                            CurrentParameter.Type,
                            selector.Type),
                    Expression,
                    QueryableMethodProvider.CreateLambdaExpression(
                        ConvertToRelationshipAssignments(selector),
                        CurrentParameter));
            }
        }

        /// <inheritdoc />
        public override void VisitWhereClause(
            WhereClause whereClause,
            QueryModel queryModel,
            int index)
        {
            Check.NotNull(whereClause, nameof(whereClause));
            Check.NotNull(queryModel, nameof(queryModel));

            if (QueryableMethodProvider.IsQueryableExpression(Expression))
            {
                Expression
                    = Expression.Call(
                        QueryableMethodProvider.Where.MakeGenericMethod(CurrentParameter.Type),
                        Expression,
                        QueryableMethodProvider.CreateLambdaExpression(
                            ReplaceClauseReferences(whereClause.Predicate),
                            CurrentParameter));
            }
            else
            {
                base.VisitWhereClause(whereClause, queryModel, index);
            }
        }
    }
}
