// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Extensions.Internal;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Query.Expressions.Internal;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.ResultOperators.Internal;
using Microsoft.EntityFrameworkCore.Utilities;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.Expressions;
using Remotion.Linq.Clauses.ExpressionVisitors;
using Remotion.Linq.Clauses.ResultOperators;
using Remotion.Linq.Parsing;

namespace Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal
{
    /// <summary>
    ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class NavigationRewritingExpressionVisitor : RelinqExpressionVisitor
    {
        private readonly EntityQueryModelVisitor _queryModelVisitor;
        private readonly NavigationJoins _navigationJoins = new NavigationJoins();
        private readonly NavigationRewritingQueryModelVisitor _navigationRewritingQueryModelVisitor;
        private QueryModel _queryModel;
        private QueryModel _parentQueryModel;

        private bool _insideInnerKeySelector;
        private bool _insideOrderBy;
        private bool _insideMaterializeCollectionNavigation;

        private class NavigationJoins : IEnumerable<NavigationJoin>
        {
            private readonly Dictionary<NavigationJoin, int> _navigationJoins = new Dictionary<NavigationJoin, int>();

            public void Add(NavigationJoin navigationJoin)
            {
                _navigationJoins.TryGetValue(navigationJoin, out var count);
                _navigationJoins[navigationJoin] = ++count;
            }

            public bool Remove(NavigationJoin navigationJoin)
            {
                if (_navigationJoins.TryGetValue(navigationJoin, out var count))
                {
                    if (count > 1)
                    {
                        _navigationJoins[navigationJoin] = --count;
                    }
                    else
                    {
                        _navigationJoins.Remove(navigationJoin);
                    }

                    return true;
                }

                return false;
            }

            public IEnumerator<NavigationJoin> GetEnumerator() => _navigationJoins.Keys.GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => _navigationJoins.Keys.GetEnumerator();
        }

        private class NavigationJoin
        {
            public static void RemoveNavigationJoin(
                NavigationJoins navigationJoins, NavigationJoin navigationJoin)
            {
                if (!navigationJoins.Remove(navigationJoin))
                {
                    foreach (var nj in navigationJoins)
                    {
                        nj.Children.Remove(navigationJoin);
                    }
                }
            }

            public NavigationJoin(
                IQuerySource querySource,
                INavigation navigation,
                JoinClause joinClause,
                IEnumerable<IBodyClause> additionalBodyClauses,
                bool dependentToPrincipal,
                QuerySourceReferenceExpression querySourceReferenceExpression)
                : this(
                    querySource,
                    navigation,
                    joinClause,
                    null,
                    additionalBodyClauses,
                    dependentToPrincipal,
                    querySourceReferenceExpression)
            {
            }

            public NavigationJoin(
                IQuerySource querySource,
                INavigation navigation,
                GroupJoinClause groupJoinClause,
                IEnumerable<IBodyClause> additionalBodyClauses,
                bool dependentToPrincipal,
                QuerySourceReferenceExpression querySourceReferenceExpression)
                : this(
                    querySource,
                    navigation,
                    null,
                    groupJoinClause,
                    additionalBodyClauses,
                    dependentToPrincipal,
                    querySourceReferenceExpression)
            {
            }

            private NavigationJoin(
                IQuerySource querySource,
                INavigation navigation,
                JoinClause joinClause,
                GroupJoinClause groupJoinClause,
                IEnumerable<IBodyClause> additionalBodyClauses,
                bool dependentToPrincipal,
                QuerySourceReferenceExpression querySourceReferenceExpression)
            {
                QuerySource = querySource;
                Navigation = navigation;
                JoinClause = joinClause;
                GroupJoinClause = groupJoinClause;
                AdditionalBodyClauses = additionalBodyClauses;
                DependentToPrincipal = dependentToPrincipal;
                QuerySourceReferenceExpression = querySourceReferenceExpression;
            }

            public IQuerySource QuerySource { get; }
            public INavigation Navigation { get; }
            public JoinClause JoinClause { get; }
            public GroupJoinClause GroupJoinClause { get; }
            public bool DependentToPrincipal { get; }
            public QuerySourceReferenceExpression QuerySourceReferenceExpression { get; }
            public readonly NavigationJoins Children = new NavigationJoins();

            private IEnumerable<IBodyClause> AdditionalBodyClauses { get; }

            private bool IsInserted { get; set; }

            public IEnumerable<NavigationJoin> Iterate()
            {
                yield return this;

                foreach (var navigationJoin in Children.SelectMany(nj => nj.Iterate()))
                {
                    yield return navigationJoin;
                }
            }

            public void Insert(QueryModel queryModel)
            {
                var insertionIndex = 0;

                if (QuerySource is IBodyClause bodyClause)
                {
                    insertionIndex = queryModel.BodyClauses.IndexOf(bodyClause) + 1;
                }

                if (queryModel.MainFromClause == QuerySource
                    || insertionIndex > 0)
                {
                    foreach (var nj in Iterate())
                    {
                        nj.Insert(queryModel, ref insertionIndex);
                    }
                }
            }

            private void Insert(QueryModel queryModel, ref int insertionIndex)
            {
                if (IsInserted)
                {
                    return;
                }

                queryModel.BodyClauses.Insert(insertionIndex++, JoinClause ?? (IBodyClause)GroupJoinClause);

                foreach (var additionalBodyClause in AdditionalBodyClauses)
                {
                    queryModel.BodyClauses.Insert(insertionIndex++, additionalBodyClause);
                }

                IsInserted = true;
            }
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public NavigationRewritingExpressionVisitor([NotNull] EntityQueryModelVisitor queryModelVisitor)
            : this(queryModelVisitor, navigationExpansionSubquery: false)
        {
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public NavigationRewritingExpressionVisitor([NotNull] EntityQueryModelVisitor queryModelVisitor, bool navigationExpansionSubquery)
        {
            Check.NotNull(queryModelVisitor, nameof(queryModelVisitor));

            _queryModelVisitor = queryModelVisitor;
            _navigationRewritingQueryModelVisitor = new NavigationRewritingQueryModelVisitor(this, _queryModelVisitor, navigationExpansionSubquery);
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual void Rewrite([NotNull] QueryModel queryModel, [CanBeNull] QueryModel parentQueryModel)
        {
            Check.NotNull(queryModel, nameof(queryModel));

            _queryModel = queryModel;
            _parentQueryModel = parentQueryModel;

            _navigationRewritingQueryModelVisitor.VisitQueryModel(_queryModel);

            foreach (var navigationJoin in _navigationJoins)
            {
                navigationJoin.Insert(_queryModel);
            }

            if (parentQueryModel != null)
            {
                _queryModel = parentQueryModel;
            }
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected override Expression VisitUnary(UnaryExpression node)
        {
            var newOperand = Visit(node.Operand);

            return node.NodeType == ExpressionType.Convert && newOperand.Type == node.Type
                ? newOperand
                : node.Update(newOperand);
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected override Expression VisitSubQuery(SubQueryExpression expression)
        {
            var oldInsideInnerKeySelector = _insideInnerKeySelector;
            _insideInnerKeySelector = false;

            Rewrite(expression.QueryModel, _queryModel);

            _insideInnerKeySelector = oldInsideInnerKeySelector;

            return expression;
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected override Expression VisitBinary(BinaryExpression node)
        {
            var newLeft = Visit(node.Left);
            var newRight = Visit(node.Right);

            if (newLeft == node.Left
                && newRight == node.Right)
            {
                return node;
            }

            var leftNavigationJoin
                = _navigationJoins
                    .SelectMany(nj => nj.Iterate())
                    .FirstOrDefault(nj => ReferenceEquals(nj.QuerySourceReferenceExpression, newLeft));

            var rightNavigationJoin
                = _navigationJoins
                    .SelectMany(nj => nj.Iterate())
                    .FirstOrDefault(nj => ReferenceEquals(nj.QuerySourceReferenceExpression, newRight));

            var leftJoin = leftNavigationJoin?.JoinClause ?? leftNavigationJoin?.GroupJoinClause?.JoinClause;
            var rightJoin = rightNavigationJoin?.JoinClause ?? rightNavigationJoin?.GroupJoinClause?.JoinClause;

            if (leftNavigationJoin != null)
            {
                if (newRight.IsNullConstantExpression())
                {
                    if (leftNavigationJoin.DependentToPrincipal)
                    {
                        newLeft = leftJoin?.OuterKeySelector;

                        NavigationJoin.RemoveNavigationJoin(_navigationJoins, leftNavigationJoin);

                        if (newLeft != null
                            && IsCompositeKey(newLeft.Type))
                        {
                            newRight = CreateNullCompositeKey(newLeft);
                        }
                    }
                }
                else
                {
                    newLeft = leftJoin?.InnerKeySelector;
                }
            }

            if (rightNavigationJoin != null)
            {
                if (newLeft.IsNullConstantExpression())
                {
                    if (rightNavigationJoin.DependentToPrincipal)
                    {
                        newRight = rightJoin?.OuterKeySelector;

                        NavigationJoin.RemoveNavigationJoin(_navigationJoins, rightNavigationJoin);

                        if (newRight != null
                            && IsCompositeKey(newRight.Type))
                        {
                            newLeft = CreateNullCompositeKey(newRight);
                        }
                    }
                }
                else
                {
                    newRight = rightJoin?.InnerKeySelector;
                }
            }

            if (node.NodeType != ExpressionType.ArrayIndex
                && node.NodeType != ExpressionType.Coalesce
                && newLeft != null
                && newRight != null
                && newLeft.Type != newRight.Type)
            {
                if (newLeft.Type.IsNullableType()
                    && !newRight.Type.IsNullableType())
                {
                    newRight = Expression.Convert(newRight, newLeft.Type);
                }
                else if (!newLeft.Type.IsNullableType()
                         && newRight.Type.IsNullableType())
                {
                    newLeft = Expression.Convert(newLeft, newRight.Type);
                }
            }

            return Expression.MakeBinary(node.NodeType, newLeft, newRight, node.IsLiftedToNull, node.Method);
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected override Expression VisitConditional(ConditionalExpression node)
        {
            var test = Visit(node.Test);
            if (test.Type == typeof(bool?))
            {
                test = Expression.Equal(test, Expression.Constant(true, typeof(bool?)));
            }

            var ifTrue = Visit(node.IfTrue);
            var ifFalse = Visit(node.IfFalse);

            if (ifTrue.Type.IsNullableType()
                && !ifFalse.Type.IsNullableType())
            {
                ifFalse = Expression.Convert(ifFalse, ifTrue.Type);
            }

            if (ifFalse.Type.IsNullableType()
                && !ifTrue.Type.IsNullableType())
            {
                ifTrue = Expression.Convert(ifTrue, ifFalse.Type);
            }

            return test != node.Test || ifTrue != node.IfTrue || ifFalse != node.IfFalse
                ? Expression.Condition(test, ifTrue, ifFalse)
                : node;
        }

        private static NewExpression CreateNullCompositeKey(Expression otherExpression)
            => Expression.New(
                AnonymousObject.AnonymousObjectCtor,
                Expression.NewArrayInit(
                    typeof(object),
                    Enumerable.Repeat(
                        Expression.Constant(null),
                        ((NewArrayExpression)((NewExpression)otherExpression).Arguments.Single()).Expressions.Count)));

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected override Expression VisitMember(MemberExpression node)
        {
            Check.NotNull(node, nameof(node));

            var result = _queryModelVisitor.BindNavigationPathPropertyExpression(
                node,
                (ps, qs) =>
                    {
                        if (qs != null)
                        {
                            return RewriteNavigationProperties(
                                ps,
                                qs,
                                node,
                                node.Expression,
                                node.Member.Name,
                                node.Type,
                                e => e.MakeMemberAccess(node.Member),
                                e => new NullConditionalExpression(e, e.MakeMemberAccess(node.Member)));
                        }

                        return null;
                    });

            if (result != null)
            {
                return result;
            }

            var newExpression = Visit(node.Expression);

            var newMemberExpression = newExpression != node.Expression
                ? newExpression.MakeMemberAccess(node.Member)
                : node;

            result = NeedsNullCompensation(newExpression)
                ? (Expression)new NullConditionalExpression(
                    newExpression,
                    newMemberExpression)
                : newMemberExpression;

            return result.Type == typeof(bool?) && node.Type == typeof(bool)
                ? Expression.Equal(result, Expression.Constant(true, typeof(bool?)))
                : result;
        }

        private readonly Dictionary<QuerySourceReferenceExpression, bool> _nullCompensationNecessityMap
            = new Dictionary<QuerySourceReferenceExpression, bool>();

        private bool NeedsNullCompensation(Expression expression)
        {
            if (expression is QuerySourceReferenceExpression qsre)
            {
                if (_nullCompensationNecessityMap.TryGetValue(qsre, out var result))
                {
                    return result;
                }

                var subQuery = (qsre.ReferencedQuerySource as FromClauseBase)?.FromExpression as SubQueryExpression
                               ?? (qsre.ReferencedQuerySource as JoinClause)?.InnerSequence as SubQueryExpression;

                // if qsre is pointing to a subquery, look for DefaulIfEmpty result operators inside
                // if such operator is found then we need to add null-compensation logic
                if (subQuery != null)
                {
                    var containsDefaultIfEmptyChecker = new ContainsDefaultIfEmptyCheckingVisitor();
                    containsDefaultIfEmptyChecker.VisitQueryModel(subQuery.QueryModel);
                    if (!containsDefaultIfEmptyChecker.ContainsDefaultIfEmpty)
                    {
                        subQuery.QueryModel.TransformExpressions(
                            e => new TransformingQueryModelExpressionVisitor<ContainsDefaultIfEmptyCheckingVisitor>(containsDefaultIfEmptyChecker).Visit(e));
                    }

                    _nullCompensationNecessityMap[qsre] = containsDefaultIfEmptyChecker.ContainsDefaultIfEmpty;

                    return containsDefaultIfEmptyChecker.ContainsDefaultIfEmpty;
                }

                _nullCompensationNecessityMap[qsre] = false;
            }

            return false;
        }

        private class ContainsDefaultIfEmptyCheckingVisitor : QueryModelVisitorBase
        {
            public bool ContainsDefaultIfEmpty { get; private set; }

            public override void VisitResultOperator(ResultOperatorBase resultOperator, QueryModel queryModel, int index)
            {
                if (resultOperator is DefaultIfEmptyResultOperator)
                {
                    ContainsDefaultIfEmpty = true;
                }
            }
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected override MemberAssignment VisitMemberAssignment(MemberAssignment node)
        {
            var newExpression = CompensateForNullabilityDifference(
                Visit(node.Expression),
                node.Expression.Type);

            return node.Update(newExpression);
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected override ElementInit VisitElementInit(ElementInit node)
        {
            var originalArgumentTypes = node.Arguments.Select(a => a.Type).ToList();
            var newArguments = node.Arguments.Select(Visit).ToList();

            for (var i = 0; i < newArguments.Count; i++)
            {
                newArguments[i] = CompensateForNullabilityDifference(newArguments[i], originalArgumentTypes[i]);
            }

            return node.Update(newArguments);
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected override Expression VisitNewArray(NewArrayExpression node)
        {
            var originalExpressionTypes = node.Expressions.Select(e => e.Type).ToList();
            var newExpressions = node.Expressions.Select(Visit).ToList();

            for (var i = 0; i < newExpressions.Count; i++)
            {
                newExpressions[i] = CompensateForNullabilityDifference(newExpressions[i], originalExpressionTypes[i]);
            }

            return node.Update(newExpressions);
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            Check.NotNull(node, nameof(node));

            if (node.Method.MethodIsClosedFormOf(
                CollectionNavigationIncludeExpressionRewriter.ProjectCollectionNavigationMethodInfo))
            {
                var newArgument = Visit(node.Arguments[0]);

                return newArgument != node.Arguments[0]
                    ? node.Update(node.Object, new[] { newArgument, node.Arguments[1] })
                    : node;
            }

            //var oldInsideMaterializeCorrelatedCollection = _insideMaterializeCorrelatedCollection;
            //if (node.Method.Name == "MaterializeCorrelatedSubquery" || node.Method.Name.StartsWith("Include"))
            //{
            //    _insideMaterializeCorrelatedCollection = true;
            //}

            if (node.Method.IsEFPropertyMethod())
            {
                var result = _queryModelVisitor.BindNavigationPathPropertyExpression(
                    node,
                    (ps, qs) =>
                        {
                            if (qs != null)
                            {
                                return RewriteNavigationProperties(
                                    ps,
                                    qs,
                                    node,
                                    node.Arguments[0],
                                    (string)((ConstantExpression)node.Arguments[1]).Value,
                                    node.Type,
                                    e => node.Arguments[0].Type != e.Type
                                        ? Expression.Call(node.Method, Expression.Convert(e, node.Arguments[0].Type), node.Arguments[1])
                                        : Expression.Call(node.Method, e, node.Arguments[1]),
                                    e => node.Arguments[0].Type != e.Type
                                        ? new NullConditionalExpression(
                                            Expression.Convert(e, node.Arguments[0].Type),
                                            Expression.Call(node.Method, Expression.Convert(e, node.Arguments[0].Type), node.Arguments[1]))
                                        : new NullConditionalExpression(e, Expression.Call(node.Method, e, node.Arguments[1])));
                            }

                            //if (node.Method.Name == "MaterializeCorrelatedSubquery" || node.Method.Name.StartsWith("Include"))
                            //{
                            //    _insideMaterializeCorrelatedCollection = oldInsideMaterializeCorrelatedCollection;
                            //}

                            return null;
                        });

                if (result != null)
                {
                    //if (node.Method.Name == "MaterializeCorrelatedSubquery" || node.Method.Name.StartsWith("Include"))
                    //{
                    //    _insideMaterializeCorrelatedCollection = oldInsideMaterializeCorrelatedCollection;
                    //}

                    return result;
                }

                var propertyArguments = node.Arguments.Select(Visit).ToList();

                var newPropertyExpression = propertyArguments[0] != node.Arguments[0] || propertyArguments[1] != node.Arguments[1]
                    ? Expression.Call(node.Method, propertyArguments[0], node.Arguments[1])
                    : node;

                result = NeedsNullCompensation(propertyArguments[0])
                    ? (Expression)new NullConditionalExpression(propertyArguments[0], newPropertyExpression)
                    : newPropertyExpression;

                //if (node.Method.Name == "MaterializeCorrelatedSubquery" || node.Method.Name.StartsWith("Include"))
                //{
                //    _insideMaterializeCorrelatedCollection = oldInsideMaterializeCorrelatedCollection;
                //}

                return result.Type == typeof(bool?) && node.Type == typeof(bool)
                    ? Expression.Equal(result, Expression.Constant(true, typeof(bool?)))
                    : result;
            }

            var insideMaterializeCollectionNavigation = _insideMaterializeCollectionNavigation;
            if (node.Method.MethodIsClosedFormOf(CollectionNavigationSubqueryInjector.MaterializeCollectionNavigationMethodInfo))
            {
                _insideMaterializeCollectionNavigation = true;
            }

            var newObject = Visit(node.Object);
            var newArguments = node.Arguments.Select(Visit);

            if (newObject != node.Object)
            {
                if (newObject is NullConditionalExpression nullConditionalExpression)
                {
                    var newMethodCallExpression = node.Update(nullConditionalExpression.AccessOperation, newArguments);

                    return new NullConditionalExpression(newObject, newMethodCallExpression);
                }
            }

            var newExpression = node.Update(newObject, newArguments);

            if (node.Method.MethodIsClosedFormOf(CollectionNavigationSubqueryInjector.MaterializeCollectionNavigationMethodInfo))
            {
                _insideMaterializeCollectionNavigation = insideMaterializeCollectionNavigation;
            }

            return newExpression;
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected override Expression VisitExtension(Expression node)
        {
            if (node is CorrelatedCollectionMarkingExpression correlatedCollectionMarkingExpression)
            {
                var oldInsideCorrelatedCollection = _insideCorrelatedCollection;
                _insideCorrelatedCollection = true;

                try
                {
                    var newOperand = (SubQueryExpression)Visit(correlatedCollectionMarkingExpression.Operand);

                    return newOperand != correlatedCollectionMarkingExpression.Operand
                        ? new CorrelatedCollectionMarkingExpression(
                            newOperand,
                            correlatedCollectionMarkingExpression.OriginQuerySource,
                            correlatedCollectionMarkingExpression.FirstNavigation,
                            correlatedCollectionMarkingExpression.LastNavigation)
                        : correlatedCollectionMarkingExpression;
                }
                finally
                {
                    _insideCorrelatedCollection = oldInsideCorrelatedCollection;
                }
            }

            return base.VisitExtension(node);
        }

        private Expression RewriteNavigationProperties(
            IReadOnlyList<IPropertyBase> properties,
            IQuerySource querySource,
            Expression expression,
            Expression declaringExpression,
            string propertyName,
            Type propertyType,
            Func<Expression, Expression> propertyCreator,
            Func<Expression, Expression> conditionalAccessPropertyCreator)
        {
            var navigations = properties.OfType<INavigation>().ToList();

            if (navigations.Count > 0)
            {
                var outerQuerySourceReferenceExpression = new QuerySourceReferenceExpression(querySource);

                var additionalFromClauseBeingProcessed = _navigationRewritingQueryModelVisitor.AdditionalFromClauseBeingProcessed;
                if (additionalFromClauseBeingProcessed != null
                    && navigations.Last().IsCollection()
                    && !_insideMaterializeCollectionNavigation)
                {
                    if (additionalFromClauseBeingProcessed.FromExpression is SubQueryExpression fromSubqueryExpression)
                    {
                        return RewriteSelectManyInsideSubqueryIntoJoins(
                            fromSubqueryExpression,
                            outerQuerySourceReferenceExpression,
                            navigations,
                            additionalFromClauseBeingProcessed);
                    }

                    return RewriteSelectManyNavigationsIntoJoins(
                        outerQuerySourceReferenceExpression,
                        navigations,
                        additionalFromClauseBeingProcessed);
                }

                if (navigations.Count == 1
                    && navigations[0].IsDependentToPrincipal())
                {
                    var foreignKeyMemberAccess = TryCreateForeignKeyMemberAccess(propertyName, declaringExpression, navigations[0]);
                    if (foreignKeyMemberAccess != null)
                    {
                        return foreignKeyMemberAccess;
                    }
                }

                if (_insideInnerKeySelector && !_insideMaterializeCollectionNavigation)
                {
                    var translated = CreateSubqueryForNavigations(
                        outerQuerySourceReferenceExpression,
                        navigations,
                        propertyCreator);

                    return translated;
                }

                var navigationResultExpression = RewriteNavigationsIntoJoins(
                    outerQuerySourceReferenceExpression,
                    navigations,
                    properties.Count == navigations.Count ? null : propertyType,
                    propertyCreator,
                    conditionalAccessPropertyCreator);

                if (navigationResultExpression is QuerySourceReferenceExpression resultQsre)
                {
                    foreach (var includeResultOperator in _queryModelVisitor.QueryCompilationContext.QueryAnnotations
                        .OfType<IncludeResultOperator>()
                        .Where(o => o.PathFromQuerySource == expression))
                    {
                        includeResultOperator.PathFromQuerySource = resultQsre;
                        includeResultOperator.QuerySource = resultQsre.ReferencedQuerySource;
                    }
                }

                return navigationResultExpression;
            }

            return default;
        }

        private class QsreWithNavigationFindingExpressionVisitor : ExpressionVisitorBase
        {
            private readonly QuerySourceReferenceExpression _searchedQsre;
            private readonly INavigation _navigation;
            private bool _navigationFound;

            public QsreWithNavigationFindingExpressionVisitor([NotNull] QuerySourceReferenceExpression searchedQsre, [NotNull] INavigation navigation)
            {
                _searchedQsre = searchedQsre;
                _navigation = navigation;
                _navigationFound = false;
                SearchedQsreFound = false;
            }

            public bool SearchedQsreFound { get; private set; }

            protected override Expression VisitMember(MemberExpression node)
            {
                if (!_navigationFound
                    && node.Member.Name == _navigation.Name)
                {
                    _navigationFound = true;

                    return base.VisitMember(node);
                }

                _navigationFound = false;

                return node;
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Method.IsEFPropertyMethod()
                    && !_navigationFound
                    && (string)((ConstantExpression)node.Arguments[1]).Value == _navigation.Name)
                {
                    _navigationFound = true;

                    return base.VisitMethodCall(node);
                }

                _navigationFound = false;

                return node;
            }

            protected override Expression VisitQuerySourceReference(QuerySourceReferenceExpression expression)
            {
                if (_navigationFound && expression.ReferencedQuerySource == _searchedQsre.ReferencedQuerySource)
                {
                    SearchedQsreFound = true;
                }
                else
                {
                    _navigationFound = false;
                }

                return expression;
            }
        }

        private Expression TryCreateForeignKeyMemberAccess(string propertyName, Expression declaringExpression, INavigation navigation)
        {
            var canPerformOptimization = true;
            if (_insideOrderBy)
            {
                var qsre = (declaringExpression as MemberExpression)?.Expression as QuerySourceReferenceExpression;
                if (qsre == null)
                {
                    if (declaringExpression is MethodCallExpression methodCallExpression
                        && methodCallExpression.Method.IsEFPropertyMethod())
                    {
                        qsre = methodCallExpression.Arguments[0] as QuerySourceReferenceExpression;
                    }
                }

                if (qsre != null)
                {
                    var qsreFindingVisitor = new QsreWithNavigationFindingExpressionVisitor(qsre, navigation);
                    qsreFindingVisitor.Visit(_queryModel.SelectClause.Selector);

                    if (qsreFindingVisitor.SearchedQsreFound)
                    {
                        canPerformOptimization = false;
                    }
                }
            }

            if (canPerformOptimization)
            {
                var foreignKeyMemberAccess = CreateForeignKeyMemberAccess(propertyName, declaringExpression, navigation);
                if (foreignKeyMemberAccess != null)
                {
                    return foreignKeyMemberAccess;
                }
            }

            return null;
        }

        private static Expression CreateForeignKeyMemberAccess(string propertyName, Expression declaringExpression, INavigation navigation)
        {
            var principalKey = navigation.ForeignKey.PrincipalKey;
            if (principalKey.Properties.Count == 1)
            {
                Debug.Assert(navigation.ForeignKey.Properties.Count == 1);

                var principalKeyProperty = principalKey.Properties[0];
                if (principalKeyProperty.Name == propertyName
                    && principalKeyProperty.ClrType == navigation.ForeignKey.Properties[0].ClrType.UnwrapNullableType())
                {
                    var declaringMethodCallExpression = declaringExpression as MethodCallExpression;
                    var parentDeclaringExpression = declaringMethodCallExpression != null
                                                    && declaringMethodCallExpression.Method.IsEFPropertyMethod()
                        ? declaringMethodCallExpression.Arguments[0]
                        : (declaringExpression as MemberExpression)?.Expression;

                    if (parentDeclaringExpression != null)
                    {
                        var foreignKeyPropertyExpression = CreateKeyAccessExpression(parentDeclaringExpression, navigation.ForeignKey.Properties);

                        return foreignKeyPropertyExpression;
                    }
                }
            }

            return null;
        }

        private Expression CreateSubqueryForNavigations(
            Expression outerQuerySourceReferenceExpression,
            ICollection<INavigation> navigations,
            Func<Expression, Expression> propertyCreator)
        {
            var firstNavigation = navigations.First();
            var targetEntityType = firstNavigation.GetTargetType();

            var mainFromClause
                = new MainFromClause(
                    "subQuery",
                    targetEntityType.ClrType,
                    NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(targetEntityType.ClrType));

            _queryModelVisitor.QueryCompilationContext.AddOrUpdateMapping(mainFromClause, targetEntityType);
            var querySourceReference = new QuerySourceReferenceExpression(mainFromClause);
            var subQueryModel = new QueryModel(mainFromClause, new SelectClause(querySourceReference));

            var leftKeyAccess = CreateKeyAccessExpression(
                querySourceReference,
                firstNavigation.IsDependentToPrincipal()
                    ? firstNavigation.ForeignKey.PrincipalKey.Properties
                    : firstNavigation.ForeignKey.Properties);

            var rightKeyAccess = CreateKeyAccessExpression(
                outerQuerySourceReferenceExpression,
                firstNavigation.IsDependentToPrincipal()
                    ? firstNavigation.ForeignKey.Properties
                    : firstNavigation.ForeignKey.PrincipalKey.Properties);

            subQueryModel.BodyClauses.Add(
                new WhereClause(
                    CreateKeyComparisonExpressionForCollectionNavigationSubquery(
                        leftKeyAccess,
                        rightKeyAccess,
                        querySourceReference)));

            subQueryModel.ResultOperators.Add(new FirstResultOperator(returnDefaultWhenEmpty: true));

            var selectClauseExpression = (Expression)querySourceReference;

            selectClauseExpression
                = navigations
                    .Skip(1)
                    .Aggregate(
                        selectClauseExpression,
                        (current, navigation) => Expression.Property(current, navigation.Name));

            subQueryModel.SelectClause = new SelectClause(propertyCreator(selectClauseExpression));

            if (navigations.Count > 1)
            {
                var subQueryVisitor = CreateVisitorForSubQuery(navigationExpansionSubquery: true);
                subQueryVisitor.Rewrite(subQueryModel, parentQueryModel: null);
            }

            return new SubQueryExpression(subQueryModel);
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual NavigationRewritingExpressionVisitor CreateVisitorForSubQuery(bool navigationExpansionSubquery)
            => new NavigationRewritingExpressionVisitor(
                _queryModelVisitor,
                navigationExpansionSubquery);

        private static Expression CreateKeyComparisonExpressionForCollectionNavigationSubquery(
            Expression leftExpression,
            Expression rightExpression,
            QuerySourceReferenceExpression leftQsre)
        {
            if (leftExpression.Type != rightExpression.Type)
            {
                if (leftExpression.Type.IsNullableType())
                {
                    Debug.Assert(leftExpression.Type.UnwrapNullableType() == rightExpression.Type);

                    rightExpression = Expression.Convert(rightExpression, leftExpression.Type);
                }
                else
                {
                    Debug.Assert(rightExpression.Type.IsNullableType());
                    Debug.Assert(rightExpression.Type.UnwrapNullableType() == leftExpression.Type);

                    leftExpression = Expression.Convert(leftExpression, rightExpression.Type);
                }
            }

            var outerNullProtection
                = Expression.NotEqual(
                    leftQsre,
                    Expression.Constant(null, leftQsre.Type));

            return new NullConditionalEqualExpression(outerNullProtection, leftExpression, rightExpression);
        }

        private bool _insideCorrelatedCollection = false;

        private Expression RewriteNavigationsIntoJoins(
            QuerySourceReferenceExpression outerQuerySourceReferenceExpression,
            IEnumerable<INavigation> navigations,
            Type propertyType,
            Func<Expression, Expression> propertyCreator,
            Func<Expression, Expression> conditionalAccessPropertyCreator)
        {
            var querySourceReferenceExpression = outerQuerySourceReferenceExpression;
            var navigationJoins = _navigationJoins;

            var optionalNavigationInChain
                = NeedsNullCompensation(outerQuerySourceReferenceExpression);

            foreach (var navigation in navigations)
            {
                var addNullCheckToOuterKeySelector = optionalNavigationInChain;

                if (!navigation.ForeignKey.IsRequired
                    || !navigation.IsDependentToPrincipal())
                {
                    optionalNavigationInChain = true;
                }

                var targetEntityType = navigation.GetTargetType();

                if (navigation.IsCollection())
                {
                    // 


                    if (_insideCorrelatedCollection)
                    {
                        return propertyCreator(querySourceReferenceExpression);
                    }
                    else
                    {
                        _queryModel.MainFromClause.FromExpression
                            = NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(targetEntityType.ClrType);

                        var innerQuerySourceReferenceExpression
                            = new QuerySourceReferenceExpression(_queryModel.MainFromClause);

                        var leftKeyAccess = CreateKeyAccessExpression(
                            querySourceReferenceExpression,
                            navigation.IsDependentToPrincipal()
                                ? navigation.ForeignKey.Properties
                                : navigation.ForeignKey.PrincipalKey.Properties);

                        var rightKeyAccess = CreateKeyAccessExpression(
                            innerQuerySourceReferenceExpression,
                            navigation.IsDependentToPrincipal()
                                ? navigation.ForeignKey.PrincipalKey.Properties
                                : navigation.ForeignKey.Properties);

                        _queryModel.BodyClauses.Add(
                            new WhereClause(
                                CreateKeyComparisonExpressionForCollectionNavigationSubquery(
                                    leftKeyAccess,
                                    rightKeyAccess,
                                    querySourceReferenceExpression)));

                        return _queryModel.MainFromClause.FromExpression;
                    }
                }

                var navigationJoin
                    = navigationJoins
                        .FirstOrDefault(
                            nj =>
                                nj.QuerySource == querySourceReferenceExpression.ReferencedQuerySource
                                && nj.Navigation == navigation);

                if (navigationJoin == null)
                {
                    QuerySourceReferenceExpression innerQuerySourceReferenceExpression;
                    var joinClause = BuildJoinFromNavigation(
                        querySourceReferenceExpression,
                        navigation,
                        targetEntityType,
                        addNullCheckToOuterKeySelector,
                        out innerQuerySourceReferenceExpression);

                    if (optionalNavigationInChain)
                    {
                        RewriteNavigationIntoGroupJoin(
                            joinClause,
                            navigation,
                            targetEntityType,
                            querySourceReferenceExpression,
                            null,
                            new List<IBodyClause>(),
                            new List<ResultOperatorBase> { new DefaultIfEmptyResultOperator(null) },
                            out navigationJoin);
                    }
                    else
                    {
                        navigationJoin
                            = new NavigationJoin(
                                querySourceReferenceExpression.ReferencedQuerySource,
                                navigation,
                                joinClause,
                                new List<IBodyClause>(),
                                navigation.IsDependentToPrincipal(),
                                innerQuerySourceReferenceExpression);
                    }
                }

                navigationJoins.Add(navigationJoin);

                querySourceReferenceExpression = navigationJoin.QuerySourceReferenceExpression;
                navigationJoins = navigationJoin.Children;
            }

            if (propertyType == null)
            {
                return querySourceReferenceExpression;
            }

            return optionalNavigationInChain
                ? conditionalAccessPropertyCreator(querySourceReferenceExpression)
                : propertyCreator(querySourceReferenceExpression);
        }

        private void RewriteNavigationIntoGroupJoin(
            JoinClause joinClause,
            INavigation navigation,
            IEntityType targetEntityType,
            QuerySourceReferenceExpression querySourceReferenceExpression,
            MainFromClause groupJoinSubqueryMainFromClause,
            ICollection<IBodyClause> groupJoinSubqueryBodyClauses,
            ICollection<ResultOperatorBase> groupJoinSubqueryResultOperators,
            out NavigationJoin navigationJoin)
        {
            var groupJoinClause
                = new GroupJoinClause(
                    joinClause.ItemName + "_group",
                    typeof(IEnumerable<>).MakeGenericType(targetEntityType.ClrType),
                    joinClause);

            var groupReferenceExpression = new QuerySourceReferenceExpression(groupJoinClause);

            var groupJoinSubqueryModelMainFromClause = new MainFromClause(joinClause.ItemName + "_groupItem", joinClause.ItemType, groupReferenceExpression);
            var newQuerySourceReferenceExpression = new QuerySourceReferenceExpression(groupJoinSubqueryModelMainFromClause);

            var groupJoinSubqueryModel = new QueryModel(
                groupJoinSubqueryModelMainFromClause,
                new SelectClause(newQuerySourceReferenceExpression));

            foreach (var groupJoinSubqueryBodyClause in groupJoinSubqueryBodyClauses)
            {
                groupJoinSubqueryModel.BodyClauses.Add(groupJoinSubqueryBodyClause);
            }

            foreach (var groupJoinSubqueryResultOperator in groupJoinSubqueryResultOperators)
            {
                groupJoinSubqueryModel.ResultOperators.Add(groupJoinSubqueryResultOperator);
            }

            if (groupJoinSubqueryMainFromClause != null
                && (groupJoinSubqueryBodyClauses.Any() || groupJoinSubqueryResultOperators.Any()))
            {
                var querySourceMapping = new QuerySourceMapping();
                querySourceMapping.AddMapping(groupJoinSubqueryMainFromClause, newQuerySourceReferenceExpression);

                groupJoinSubqueryModel.TransformExpressions(
                    e =>
                        ReferenceReplacingExpressionVisitor
                            .ReplaceClauseReferences(e, querySourceMapping, throwOnUnmappedReferences: false));
            }

            var defaultIfEmptySubquery = new SubQueryExpression(groupJoinSubqueryModel);
            var defaultIfEmptyAdditionalFromClause = new AdditionalFromClause(joinClause.ItemName, joinClause.ItemType, defaultIfEmptySubquery);
            navigationJoin = new NavigationJoin(
                querySourceReferenceExpression.ReferencedQuerySource,
                navigation,
                groupJoinClause,
                new[] { defaultIfEmptyAdditionalFromClause },
                navigation.IsDependentToPrincipal(),
                new QuerySourceReferenceExpression(defaultIfEmptyAdditionalFromClause));

            _queryModelVisitor.QueryCompilationContext.AddOrUpdateMapping(defaultIfEmptyAdditionalFromClause, targetEntityType);
        }

        private Expression RewriteSelectManyNavigationsIntoJoins(
            QuerySourceReferenceExpression outerQuerySourceReferenceExpression,
            IEnumerable<INavigation> navigations,
            AdditionalFromClause additionalFromClauseBeingProcessed)
        {
            var querySourceReferenceExpression = outerQuerySourceReferenceExpression;
            var additionalJoinIndex = _queryModel.BodyClauses.IndexOf(additionalFromClauseBeingProcessed);
            var joinClauses = new List<JoinClause>();

            foreach (var navigation in navigations)
            {
                var targetEntityType = navigation.GetTargetType();

                var joinClause = BuildJoinFromNavigation(
                    querySourceReferenceExpression,
                    navigation,
                    targetEntityType,
                    false,
                    out var innerQuerySourceReferenceExpression);

                joinClauses.Add(joinClause);

                querySourceReferenceExpression = innerQuerySourceReferenceExpression;
            }

            _queryModel.BodyClauses.RemoveAt(additionalJoinIndex);

            for (var i = 0; i < joinClauses.Count; i++)
            {
                _queryModel.BodyClauses.Insert(additionalJoinIndex + i, joinClauses[i]);
            }

            var querySourceMapping = new QuerySourceMapping();
            querySourceMapping.AddMapping(additionalFromClauseBeingProcessed, querySourceReferenceExpression);

            _queryModel.TransformExpressions(
                e =>
                    ReferenceReplacingExpressionVisitor
                        .ReplaceClauseReferences(e, querySourceMapping, throwOnUnmappedReferences: false));

            foreach (var includeResultOperator in _queryModelVisitor.QueryCompilationContext.QueryAnnotations.OfType<IncludeResultOperator>())
            {
                if (includeResultOperator.PathFromQuerySource.TryGetReferencedQuerySource()
                    == additionalFromClauseBeingProcessed)
                {
                    includeResultOperator.PathFromQuerySource = querySourceReferenceExpression;
                    includeResultOperator.QuerySource = querySourceReferenceExpression.ReferencedQuerySource;
                }
            }

            return querySourceReferenceExpression;
        }

        private Expression RewriteSelectManyInsideSubqueryIntoJoins(
            SubQueryExpression fromSubqueryExpression,
            QuerySourceReferenceExpression outerQuerySourceReferenceExpression,
            ICollection<INavigation> navigations,
            AdditionalFromClause additionalFromClauseBeingProcessed)
        {
            var collectionNavigation = navigations.Last();
            var adddedJoinClauses = new List<IBodyClause>();

            foreach (var navigation in navigations)
            {
                var targetEntityType = navigation.GetTargetType();

                QuerySourceReferenceExpression innerQuerySourceReferenceExpression;
                var joinClause = BuildJoinFromNavigation(
                    outerQuerySourceReferenceExpression,
                    navigation,
                    targetEntityType,
                    false,
                    out innerQuerySourceReferenceExpression);

                if (navigation == collectionNavigation)
                {
                    NavigationJoin navigationJoin;
                    RewriteNavigationIntoGroupJoin(
                        joinClause,
                        navigations.Last(),
                        targetEntityType,
                        outerQuerySourceReferenceExpression,
                        fromSubqueryExpression.QueryModel.MainFromClause,
                        fromSubqueryExpression.QueryModel.BodyClauses,
                        fromSubqueryExpression.QueryModel.ResultOperators,
                        out navigationJoin);

                    _navigationJoins.Add(navigationJoin);

                    var additionalFromClauseIndex = _parentQueryModel.BodyClauses.IndexOf(additionalFromClauseBeingProcessed);
                    _parentQueryModel.BodyClauses.Remove(additionalFromClauseBeingProcessed);

                    var i = additionalFromClauseIndex;
                    foreach (var addedJoinClause in adddedJoinClauses)
                    {
                        _parentQueryModel.BodyClauses.Insert(i++, addedJoinClause);
                    }

                    var querySourceMapping = new QuerySourceMapping();
                    querySourceMapping.AddMapping(additionalFromClauseBeingProcessed, navigationJoin.QuerySourceReferenceExpression);

                    _parentQueryModel.TransformExpressions(
                        e =>
                            ReferenceReplacingExpressionVisitor
                                .ReplaceClauseReferences(e, querySourceMapping, throwOnUnmappedReferences: false));

                    foreach (var includeResultOperator in _queryModelVisitor.QueryCompilationContext.QueryAnnotations.OfType<IncludeResultOperator>())
                    {
                        if (includeResultOperator.PathFromQuerySource.TryGetReferencedQuerySource()
                            == additionalFromClauseBeingProcessed)
                        {
                            includeResultOperator.PathFromQuerySource = navigationJoin.QuerySourceReferenceExpression;
                            includeResultOperator.QuerySource = navigationJoin.QuerySourceReferenceExpression.ReferencedQuerySource;
                        }
                    }

                    return navigationJoin.QuerySourceReferenceExpression;
                }

                adddedJoinClauses.Add(joinClause);

                outerQuerySourceReferenceExpression = innerQuerySourceReferenceExpression;
            }

            return outerQuerySourceReferenceExpression;
        }

        private JoinClause BuildJoinFromNavigation(
            QuerySourceReferenceExpression querySourceReferenceExpression,
            INavigation navigation,
            IEntityType targetEntityType,
            bool addNullCheckToOuterKeySelector,
            out QuerySourceReferenceExpression innerQuerySourceReferenceExpression)
        {
            var outerKeySelector =
                CreateKeyAccessExpression(
                    querySourceReferenceExpression,
                    navigation.IsDependentToPrincipal()
                        ? navigation.ForeignKey.Properties
                        : navigation.ForeignKey.PrincipalKey.Properties,
                    addNullCheckToOuterKeySelector);

            var itemName
                = querySourceReferenceExpression.ReferencedQuerySource.HasGeneratedItemName()
                    ? navigation.DeclaringEntityType.DisplayName()[0].ToString().ToLowerInvariant()
                    : querySourceReferenceExpression.ReferencedQuerySource.ItemName;

            var joinClause
                = new JoinClause(
                    $"{itemName}.{navigation.Name}",
                    targetEntityType.ClrType,
                    NullAsyncQueryProvider.Instance.CreateEntityQueryableExpression(targetEntityType.ClrType),
                    outerKeySelector,
                    Expression.Constant(null));

            innerQuerySourceReferenceExpression = new QuerySourceReferenceExpression(joinClause);
            _queryModelVisitor.QueryCompilationContext.AddOrUpdateMapping(joinClause, targetEntityType);

            var innerKeySelector
                = CreateKeyAccessExpression(
                    innerQuerySourceReferenceExpression,
                    navigation.IsDependentToPrincipal()
                        ? navigation.ForeignKey.PrincipalKey.Properties
                        : navigation.ForeignKey.Properties);

            if (innerKeySelector.Type != joinClause.OuterKeySelector.Type)
            {
                if (innerKeySelector.Type.IsNullableType())
                {
                    joinClause.OuterKeySelector
                        = Expression.Convert(
                            joinClause.OuterKeySelector,
                            innerKeySelector.Type);
                }
                else
                {
                    innerKeySelector
                        = Expression.Convert(
                            innerKeySelector,
                            joinClause.OuterKeySelector.Type);
                }
            }

            joinClause.InnerKeySelector = innerKeySelector;

            return joinClause;
        }

        private static Expression CreateKeyAccessExpression(
            Expression target, IReadOnlyList<IProperty> properties, bool addNullCheck = false)
            => properties.Count == 1
                ? CreatePropertyExpression(target, properties[0], addNullCheck)
                : Expression.New(
                    AnonymousObject.AnonymousObjectCtor,
                    Expression.NewArrayInit(
                        typeof(object),
                        properties
                            .Select(p => Expression.Convert(CreatePropertyExpression(target, p, addNullCheck), typeof(object)))
                            .Cast<Expression>()
                            .ToArray()));

        private static Expression CreatePropertyExpression(Expression target, IProperty property, bool addNullCheck)
        {
            var propertyExpression = target.CreateEFPropertyExpression(property, makeNullable: false);

            var propertyDeclaringType = property.DeclaringType.ClrType;
            if (propertyDeclaringType != target.Type
                && target.Type.GetTypeInfo().IsAssignableFrom(propertyDeclaringType.GetTypeInfo()))
            {
                if (!propertyExpression.Type.IsNullableType())
                {
                    propertyExpression = Expression.Convert(propertyExpression, propertyExpression.Type.MakeNullable());
                }

                return Expression.Condition(
                    Expression.TypeIs(target, propertyDeclaringType),
                    propertyExpression,
                    Expression.Constant(null, propertyExpression.Type));
            }

            return addNullCheck
                ? new NullConditionalExpression(target, propertyExpression)
                : propertyExpression;
        }

        private static bool IsCompositeKey([NotNull] Type type)
        {
            Check.NotNull(type, nameof(type));

            return type == typeof(AnonymousObject);
        }

        private static Expression CompensateForNullabilityDifference(Expression expression, Type originalType)
        {
            var newType = expression.Type;

            var needsTypeCompensation
                = originalType != newType
                  && !originalType.IsNullableType()
                  && newType.IsNullableType()
                  && originalType == newType.UnwrapNullableType();

            return needsTypeCompensation
                ? Expression.Convert(expression, originalType)
                : expression;
        }

        private class NavigationRewritingQueryModelVisitor : ExpressionTransformingQueryModelVisitor<NavigationRewritingExpressionVisitor>
        {
            private readonly CollectionNavigationSubqueryInjector _subqueryInjector;
            private readonly bool _navigationExpansionSubquery;
            private readonly QueryCompilationContext _queryCompilationContext;

            public AdditionalFromClause AdditionalFromClauseBeingProcessed { get; private set; }

            public NavigationRewritingQueryModelVisitor(
                NavigationRewritingExpressionVisitor transformingVisitor,
                EntityQueryModelVisitor queryModelVisitor,
                bool navigationExpansionSubquery)
                : base(transformingVisitor)
            {
                _subqueryInjector = new CollectionNavigationSubqueryInjector(queryModelVisitor, shouldInject: true);
                _navigationExpansionSubquery = navigationExpansionSubquery;
                _queryCompilationContext = queryModelVisitor.QueryCompilationContext;
            }

            public override void VisitAdditionalFromClause(AdditionalFromClause fromClause, QueryModel queryModel, int index)
            {
                // ReSharper disable once PatternAlwaysOfType
                if (fromClause.TryGetFlattenedGroupJoinClause()?.JoinClause is JoinClause joinClause
                    // ReSharper disable once PatternAlwaysOfType
                    && _queryCompilationContext.FindEntityType(joinClause) is IEntityType entityType)
                {
                    _queryCompilationContext.AddOrUpdateMapping(fromClause, entityType);
                }

                var oldAdditionalFromClause = AdditionalFromClauseBeingProcessed;
                AdditionalFromClauseBeingProcessed = fromClause;
                fromClause.TransformExpressions(TransformingVisitor.Visit);
                AdditionalFromClauseBeingProcessed = oldAdditionalFromClause;
            }

            public override void VisitWhereClause(WhereClause whereClause, QueryModel queryModel, int index)
            {
                base.VisitWhereClause(whereClause, queryModel, index);

                if (whereClause.Predicate.Type == typeof(bool?))
                {
                    whereClause.Predicate = Expression.Equal(whereClause.Predicate, Expression.Constant(true, typeof(bool?)));
                }
            }

            public override void VisitOrderByClause(OrderByClause orderByClause, QueryModel queryModel, int index)
            {
                var originalTypes = orderByClause.Orderings.Select(o => o.Expression.Type).ToList();

                var oldInsideOrderBy = TransformingVisitor._insideOrderBy;
                TransformingVisitor._insideOrderBy = true;

                base.VisitOrderByClause(orderByClause, queryModel, index);

                TransformingVisitor._insideOrderBy = oldInsideOrderBy;

                for (var i = 0; i < orderByClause.Orderings.Count; i++)
                {
                    orderByClause.Orderings[i].Expression = CompensateForNullabilityDifference(
                        orderByClause.Orderings[i].Expression,
                        originalTypes[i]);
                }
            }

            public override void VisitJoinClause(JoinClause joinClause, QueryModel queryModel, int index)
                => VisitJoinClauseInternal(joinClause);

            public override void VisitJoinClause(JoinClause joinClause, QueryModel queryModel, GroupJoinClause groupJoinClause)
                => VisitJoinClauseInternal(joinClause);

            private void VisitJoinClauseInternal(JoinClause joinClause)
            {
                joinClause.InnerSequence = TransformingVisitor.Visit(joinClause.InnerSequence);

                var queryCompilationContext = TransformingVisitor._queryModelVisitor.QueryCompilationContext;
                if (queryCompilationContext.FindEntityType(joinClause) == null
                    && joinClause.InnerSequence is SubQueryExpression subQuery)
                {
                    IEntityType entityType = null;
                    var properties = MemberAccessBindingExpressionVisitor.GetPropertyPath(
                        subQuery.QueryModel.SelectClause.Selector, queryCompilationContext, out var qsre);
                    if (properties.Count > 0)
                    {
                        if (properties[properties.Count - 1] is INavigation navigation)
                        {
                            entityType = navigation.GetTargetType();
                        }
                    }
                    else if (qsre != null)
                    {
                        entityType = queryCompilationContext.FindEntityType(qsre.ReferencedQuerySource);
                    }

                    if (entityType != null)
                    {
                        queryCompilationContext.AddOrUpdateMapping(joinClause, entityType);
                    }
                }

                joinClause.OuterKeySelector = TransformingVisitor.Visit(joinClause.OuterKeySelector);

                var oldInsideInnerKeySelector = TransformingVisitor._insideInnerKeySelector;
                TransformingVisitor._insideInnerKeySelector = true;
                joinClause.InnerKeySelector = TransformingVisitor.Visit(joinClause.InnerKeySelector);

                if (joinClause.OuterKeySelector.Type.IsNullableType()
                    && !joinClause.InnerKeySelector.Type.IsNullableType())
                {
                    joinClause.InnerKeySelector = Expression.Convert(joinClause.InnerKeySelector, joinClause.InnerKeySelector.Type.MakeNullable());
                }

                if (joinClause.InnerKeySelector.Type.IsNullableType()
                    && !joinClause.OuterKeySelector.Type.IsNullableType())
                {
                    joinClause.OuterKeySelector = Expression.Convert(joinClause.OuterKeySelector, joinClause.OuterKeySelector.Type.MakeNullable());
                }

                TransformingVisitor._insideInnerKeySelector = oldInsideInnerKeySelector;
            }

            public override void VisitSelectClause(SelectClause selectClause, QueryModel queryModel)
            {
                selectClause.Selector = _subqueryInjector.Visit(selectClause.Selector);

                //var subqueryCorrelatingExpressionVisitor = new SubqueryCorrelatingExpressionVisitor(_queryCompilationContext.CreateQueryModelVisitor(), queryModel);
                //selectClause.Selector = subqueryCorrelatingExpressionVisitor.Visit(selectClause.Selector);

                if (_navigationExpansionSubquery)
                {
                    base.VisitSelectClause(selectClause, queryModel);
                    return;
                }

                var originalType = selectClause.Selector.Type;

                base.VisitSelectClause(selectClause, queryModel);

                //var subqueryCorrelatingExpressionVisitor = new SubqueryCorrelatingExpressionVisitor(_queryCompilationContext.CreateQueryModelVisitor(), queryModel);
                //selectClause.Selector = subqueryCorrelatingExpressionVisitor.Visit(selectClause.Selector);

                selectClause.Selector = CompensateForNullabilityDifference(selectClause.Selector, originalType);
            }

            public override void VisitResultOperator(ResultOperatorBase resultOperator, QueryModel queryModel, int index)
            {
                if (resultOperator is AllResultOperator allResultOperator)
                {
                    Func<AllResultOperator, Expression> expressionExtractor = o => o.Predicate;
                    Action<AllResultOperator, Expression> adjuster = (o, e) => o.Predicate = e;
                    VisitAndAdjustResultOperatorType(allResultOperator, expressionExtractor, adjuster);

                    return;
                }

                if (resultOperator is ContainsResultOperator containsResultOperator)
                {
                    Func<ContainsResultOperator, Expression> expressionExtractor = o => o.Item;
                    Action<ContainsResultOperator, Expression> adjuster = (o, e) => o.Item = e;
                    VisitAndAdjustResultOperatorType(containsResultOperator, expressionExtractor, adjuster);

                    return;
                }

                if (resultOperator is SkipResultOperator skipResultOperator)
                {
                    Func<SkipResultOperator, Expression> expressionExtractor = o => o.Count;
                    Action<SkipResultOperator, Expression> adjuster = (o, e) => o.Count = e;
                    VisitAndAdjustResultOperatorType(skipResultOperator, expressionExtractor, adjuster);

                    return;
                }

                if (resultOperator is TakeResultOperator takeResultOperator)
                {
                    Func<TakeResultOperator, Expression> expressionExtractor = o => o.Count;
                    Action<TakeResultOperator, Expression> adjuster = (o, e) => o.Count = e;
                    VisitAndAdjustResultOperatorType(takeResultOperator, expressionExtractor, adjuster);

                    return;
                }

                if (resultOperator is GroupResultOperator groupResultOperator)
                {
                    groupResultOperator.ElementSelector
                        = _subqueryInjector.Visit(groupResultOperator.ElementSelector);

                    var originalKeySelectorType = groupResultOperator.KeySelector.Type;
                    var originalElementSelectorType = groupResultOperator.ElementSelector.Type;

                    base.VisitResultOperator(resultOperator, queryModel, index);

                    groupResultOperator.KeySelector = CompensateForNullabilityDifference(
                        groupResultOperator.KeySelector,
                        originalKeySelectorType);

                    groupResultOperator.ElementSelector = CompensateForNullabilityDifference(
                        groupResultOperator.ElementSelector,
                        originalElementSelectorType);

                    return;
                }

                base.VisitResultOperator(resultOperator, queryModel, index);
            }

            private void VisitAndAdjustResultOperatorType<TResultOperator>(
                TResultOperator resultOperator,
                Func<TResultOperator, Expression> expressionExtractor,
                Action<TResultOperator, Expression> adjuster)
                where TResultOperator : ResultOperatorBase
            {
                var originalExpression = expressionExtractor(resultOperator);
                var originalType = originalExpression.Type;

                var translatedExpression = CompensateForNullabilityDifference(
                    TransformingVisitor.Visit(originalExpression),
                    originalType);

                adjuster(resultOperator, translatedExpression);
            }

            //public void CorrelateSubqueries(QueryModel queryModel)
            //{

            //    var subqueryCorrelatingExpressionVisitor = new SubqueryCorrelatingExpressionVisitor(_queryCompilationContext.CreateQueryModelVisitor(), queryModel);
            //    queryModel.TransformExpressions(subqueryCorrelatingExpressionVisitor.Visit);
            //}

            //            private class SubqueryCorrelatingExpressionVisitor : RelinqExpressionVisitor
            //            {
            //                private readonly EntityQueryModelVisitor _queryModelVisitor;
            //                private QueryModel _queryModel;

            //                public SubqueryCorrelatingExpressionVisitor(EntityQueryModelVisitor queryModelVisitor, QueryModel queryModel)
            //                {
            //                    _queryModelVisitor = queryModelVisitor;
            //                    _queryModel = queryModel;
            //                }

            //                // todo: dry
            //                private class QuerySourceReferenceFindingExpressionTreeVisitor : RelinqExpressionVisitor
            //                {
            //                    public QuerySourceReferenceExpression QuerySourceReferenceExpression { get; private set; }

            //                    protected override Expression VisitQuerySourceReference(QuerySourceReferenceExpression querySourceReferenceExpression)
            //                    {
            //                        if (QuerySourceReferenceExpression == null)
            //                        {
            //                            QuerySourceReferenceExpression = querySourceReferenceExpression;
            //                        }

            //                        return querySourceReferenceExpression;
            //                    }
            //                }




            //                protected override Expression VisitMethodCall(MethodCallExpression node)
            //                {
            //                    //if (node.Method.MethodIsClosedFormOf(_materializeCorrelatedSubqueryMethodInfo))
            //                    //{
            //                    //    return node;
            //                    //}

            //                    if (node.Method.Name.StartsWith("IncludeCollection"))
            //                    {
            //                        return node;
            //                    }

            //                    return base.VisitMethodCall(node);
            //                }



            //                protected override Expression VisitExtension(Expression node)
            //                {
            //                    if (node is CorrelatedCollectionMarkingExpression correlatedCollectionMarkingExpression)
            //                    {
            //                        return _queryModelVisitor.BindNavigationPathPropertyExpression(
            //                            correlatedCollectionMarkingExpression.Operand.QueryModel.MainFromClause.FromExpression,
            //                            (properties, querySource) =>
            //                            {
            //                                var collectionNavigation = properties.OfType<INavigation>().SingleOrDefault(n => n.IsCollection());

            //                                return CorrelateSubquery2(
            //                                    new QuerySourceReferenceExpression(correlatedCollectionMarkingExpression.OriginQuerySource),
            //                                    new QuerySourceReferenceExpression(querySource),
            //                                    correlatedCollectionMarkingExpression.FirstNavigation,
            //                                    correlatedCollectionMarkingExpression.LastNavigation,
            //                                    correlatedCollectionMarkingExpression.Operand);

            //                                //return properties.Count == 1 && collectionNavigation != null
            //                                //    ? CorrelateSubquery2(new QuerySourceReferenceExpression(querySource), collectionNavigation, expression)
            //                                //    : default;
            //                            });





            //                    }

            //                    return base.VisitExtension(node);
            //                }

            //                //protected override Expression VisitSubQuery(SubQueryExpression expression)
            //                //{
            //                //    var subQueryModel = expression.QueryModel;
            //                //    if (subQueryModel.ResultOperators.Count == 0
            //                //        && subQueryModel.SelectClause.Selector is QuerySourceReferenceExpression selectorQsre
            //                //        && selectorQsre.ReferencedQuerySource == subQueryModel.MainFromClause
            //                //        && subQueryModel.MainFromClause.FromExpression is MemberExpression fromMemberExpression)
            //                //    {
            //                //        var newMemberExpression = _queryModelVisitor.BindNavigationPathPropertyExpression(
            //                //            fromMemberExpression,
            //                //            (properties, querySource) =>
            //                //            {
            //                //                var collectionNavigation = properties.OfType<INavigation>().SingleOrDefault(n => n.IsCollection());

            //                //                return properties.Count == 1 && collectionNavigation != null
            //                //                    ? CorrelateSubquery2(new QuerySourceReferenceExpression(querySource), collectionNavigation, expression)
            //                //                    : default;
            //                //            });

            //                //        if (newMemberExpression != null)
            //                //        {
            //                //            return newMemberExpression;
            //                //        }
            //                //    }

            //                //    if (subQueryModel.ResultOperators.Count == 0)
            //                //        //&& (subQueryModel.MainFromClause.FromExpression is MemberExpression 
            //                //        //    || (subQueryModel.MainFromClause.FromExpression as MethodCallExpression)?.IsEFProperty()))
            //                //    {
            //                //        var querySourceReferenceFindingExpressionTreeVisitor
            //                //            = new QuerySourceReferenceFindingExpressionTreeVisitor();

            //                //        querySourceReferenceFindingExpressionTreeVisitor.Visit(subQueryModel.SelectClause.Selector);
            //                //        if (querySourceReferenceFindingExpressionTreeVisitor.QuerySourceReferenceExpression.ReferencedQuerySource == subQueryModel.MainFromClause)
            //                //        {
            //                //            var newMemberExpression = _queryModelVisitor.BindNavigationPathPropertyExpression(
            //                //            subQueryModel.MainFromClause.FromExpression,
            //                //            (properties, querySource) =>
            //                //            {
            //                //                var collectionNavigation = properties.OfType<INavigation>().SingleOrDefault(n => n.IsCollection());

            //                //                return properties.Count == 1 && collectionNavigation != null // TODO: no navigation chaining for now
            //                //                    ? CorrelateSubquery2(new QuerySourceReferenceExpression(querySource), collectionNavigation, expression)
            //                //                    : default;
            //                //            });

            //                //            if (newMemberExpression != null)
            //                //            {
            //                //                return newMemberExpression;
            //                //            }
            //                //        }
            //                //    }

            //                //    return base.VisitSubQuery(expression);
            //                //}


            //                private static MethodInfo _correlateSubqueryMethodInfo = typeof(IQueryBuffer).GetMethod(nameof(IQueryBuffer.CorrelateSubquery));



            //                private Expression CorrelateSubquery2(
            //                    QuerySourceReferenceExpression originQuerySourceExpression,
            //                    QuerySourceReferenceExpression outerExpression,
            //                    INavigation firstNavigation,
            //                    INavigation collectionNavigation, 
            //                    SubQueryExpression subQueryExpression)
            //                {
            //                    var subQueryModel = subQueryExpression.QueryModel;

            //                    var outerKey = BuildKeyAccess(collectionNavigation.ForeignKey.PrincipalKey.Properties, outerExpression);
            //                    var innerKey = BuildKeyAccess(collectionNavigation.ForeignKey.Properties, new QuerySourceReferenceExpression(subQueryModel.MainFromClause));
            //                    var correlationnPredicate = CreateCorrelationPredicate(collectionNavigation);

            //                    var subQueryResultElementType = subQueryModel.SelectClause.Selector.Type;

            //                    var kvp = typeof(KeyValuePair<,>).MakeGenericType(subQueryResultElementType, typeof(AnonymousObject2));
            //                    var kvpCtor = kvp.GetTypeInfo().DeclaredConstructors.FirstOrDefault();

            //                    var kvp2 = typeof(KeyValuePair<,>).MakeGenericType(kvp, typeof(AnonymousObject2));
            //                    var kvp2Ctor = kvp2.GetTypeInfo().DeclaredConstructors.FirstOrDefault();

            //                    var originEntityType = _queryModelVisitor.QueryCompilationContext.Model.FindEntityType(originQuerySourceExpression.Type);
            //                    var originKey = BuildKeyAccess(originEntityType.FindPrimaryKey().Properties, originQuerySourceExpression);



            //                    subQueryModel.SelectClause.Selector = Expression.New(
            //                        kvp2Ctor,
            //                        Expression.New(kvpCtor, subQueryModel.SelectClause.Selector, innerKey),
            //                        originKey);

            //                    //subQueryModel.SelectClause.Selector = Expression.New(kvpCtor, subQueryModel.SelectClause.Selector, innerKey);
            //                    subQueryModel.ResultTypeOverride = typeof(IEnumerable<>).MakeGenericType(subQueryModel.SelectClause.Selector.Type);


            //                    var arguments = new List<Expression>
            //                    {
            //                        Expression.Constant(1), // TODO: fix this!
            //                        originQuerySourceExpression,
            //                        Expression.Constant(collectionNavigation),
            //                        Expression.Constant(firstNavigation),
            //                        outerKey,
            //                        Expression.Lambda(new SubQueryExpression(subQueryModel)), 
            //                        correlationnPredicate
            //                    };

            //                    var generic = _correlateSubqueryMethodInfo.MakeGenericMethod(subQueryResultElementType);

            //                    var result = Expression.Call(
            //                        Expression.Property(
            //                            EntityQueryModelVisitor.QueryContextParameter,
            //                            nameof(QueryContext.QueryBuffer)),
            //                        generic,
            //                        arguments);

            //                    return result;

            //                    //// TODO: needed?
            //                    //var resultCollectionType = collectionNavigation.GetCollectionAccessor().CollectionType;

            //                    //return resultCollectionType.GetTypeInfo().IsGenericType && resultCollectionType.GetGenericTypeDefinition() == typeof(ICollection<>)
            //                    //    ? (Expression)result
            //                    //    : Expression.Convert(result, resultCollectionType);
            //                }

            //            //    private Expression CorrelateSubquery(QuerySourceReferenceExpression outerExpression, INavigation collectionNavigation, Expression subqueryExpression)
            //            //    {
            //            //        var outerKey = BuildOuterKey(collectionNavigation, outerExpression);

            //            //        var correlationnPredicate = TryCreateCorrelationPredicate(collectionNavigation.DeclaringEntityType.ClrType, collectionNavigation);

            //            //        if (correlationnPredicate is DefaultExpression)
            //            //        {
            //            //            return null;
            //            //        }

            //            //        var arguments = new List<Expression>
            //            //        {
            //            //            Expression.Constant(1),
            //            //            Expression.Constant(collectionNavigation/*.GetCollectionAccessor()*/),
            //            //            //Expression.Constant(collectionNavigation.GetCollectionAccessor()),
            //            //            outerKey,
            //            //            Expression.Lambda(subqueryExpression),
            //            //            correlationnPredicate
            //            //        };

            //            //        var targetType = collectionNavigation.GetTargetType().ClrType;

            //            //        var generic = _materializeCorrelatedSubqueryMethodInfo.MakeGenericMethod(targetType);
            //            //        //var generic = _materializeCorrelatedSubqueryMethodInfo.MakeGenericMethod(collectionNavigation.GetTargetType().ClrType);

            //            //        var result = Expression.Call(
            //            //            Expression.Property(
            //            //                EntityQueryModelVisitor.QueryContextParameter,
            //            //                nameof(QueryContext.QueryBuffer)),
            //            //            generic,
            //            //            arguments);

            //            //        /*
            //            //         * 
            //            //         * 
            //            //         * 
            //            //         *             int childId,
            //            //IClrCollectionAccessor clrCollectionAccessor,
            //            //TOuter outer,
            //            //Func<IEnumerable<TInner>> relatedEntitiesFactory,
            //            //Func<TOuter, TInner, bool> joinPredicate)
            //            //         * 
            //            //         * 
            //            //         * 
            //            //         */

            //            //        var resultCollectionType = collectionNavigation.GetCollectionAccessor().CollectionType;

            //            //        //TODO: not needed?
            //            //        result = Expression.Call(
            //            //             CollectionNavigationSubqueryInjector.MaterializeCollectionNavigationMethodInfo.MakeGenericMethod(targetType),
            //            //            Expression.Constant(collectionNavigation), /*subqueryExpression*/result);

            //            //        return resultCollectionType.GetTypeInfo().IsGenericType && resultCollectionType.GetGenericTypeDefinition() == typeof(ICollection<>)
            //            //            ? (Expression)result
            //            //            : Expression.Convert(result, resultCollectionType);




            //            //        //return result;
            //            //        //var targetType = collectionNavigation.GetTargetType().ClrType;
            //            //        //var mainFromClause = new MainFromClause(targetType.Name.Substring(0, 1).ToLowerInvariant(), targetType, expression);
            //            //        //var selector = new QuerySourceReferenceExpression(mainFromClause);

            //            //        //var subqueryModel = new QueryModel(mainFromClause, new SelectClause(selector));
            //            //        //var subqueryExpression = new SubQueryExpression(subqueryModel);

            //            //        //var resultCollectionType = collectionNavigation.GetCollectionAccessor().CollectionType;

            //            //        //var result = Expression.Call(
            //            //        //    MaterializeCollectionNavigationMethodInfo.MakeGenericMethod(targetType),
            //            //        //    Expression.Constant(collectionNavigation), subqueryExpression);

            //            //        //return resultCollectionType.GetTypeInfo().IsGenericType && resultCollectionType.GetGenericTypeDefinition() == typeof(ICollection<>)
            //            //        //    ? (Expression)result
            //            //        //    : Expression.Convert(result, resultCollectionType);
            //            //    }



            //                private static Expression BuildKeyAccess(IEnumerable<IProperty> keyProperties, Expression qsre)
            //                {
            //                    var keyAccessExpressions = keyProperties.Select(p => qsre.CreateEFPropertyExpression(p)).ToArray();

            ////                    var keyAccessExpressions = keyProperties.Select(p => Expression.MakeMemberAccess(qsre, p.GetMemberInfo(forConstruction: false, forSet: false))).ToArray();

            //                    return Expression.New(
            //                        AnonymousObject2.AnonymousObjectCtor,
            //                        Expression.NewArrayInit(
            //                            typeof(object),
            //                            keyAccessExpressions.Select(k => Expression.Convert(k, typeof(object)))));
            //                }

            //                private static Expression BuildOuterKey(INavigation navigation, Expression outerQsre)
            //                {
            //                    var foreignKey = navigation.ForeignKey;
            //                    var primaryKeyProperties = foreignKey.PrincipalKey.Properties;

            //                    var pks = primaryKeyProperties.Select(p => Expression.MakeMemberAccess(outerQsre, p.GetMemberInfo(forConstruction: false, forSet: false))).ToArray();

            //                    return Expression.New(
            //                        AnonymousObject.AnonymousObjectCtor,
            //                        Expression.NewArrayInit(
            //                            typeof(object),
            //                            pks.Select(k => Expression.Convert(k, typeof(object)))));
            //                }

            //                private static Expression CreateCorrelationPredicate(INavigation navigation)
            //                {
            //                    var foreignKey = navigation.ForeignKey;
            //                    var primaryKeyProperties = foreignKey.PrincipalKey.Properties;
            //                    var foreignKeyProperties = foreignKey.Properties;

            //                    var outerKeyParameter = Expression.Parameter(typeof(AnonymousObject2), "o");
            //                    var innerKeyParameter = Expression.Parameter(typeof(AnonymousObject2), "i");

            //                    return Expression.Lambda(
            //                        primaryKeyProperties
            //                            .Select((pk, i) => new { pk, i })
            //                            .Zip(
            //                                foreignKeyProperties,
            //                                (outer, inner) =>
            //                                {
            //                                    //Expression outerKeyAccess =
            //                                    //    Expression.Convert(
            //                                    //        Expression.Call(
            //                                    //            outerKeyParameter,
            //                                    //            AnonymousObject.GetValueMethodInfo,
            //                                    //            Expression.Constant(outer.i)),
            //                                    //        primaryKeyProperties[outer.i].ClrType);

            //                                    var outerKeyAccess =
            //                                        Expression.Call(
            //                                            outerKeyParameter,
            //                                            AnonymousObject2.GetValueMethodInfo,
            //                                            Expression.Constant(outer.i));

            //                                    var typedOuterKeyAccess = 
            //                                        Expression.Convert(
            //                                            outerKeyAccess,
            //                                            primaryKeyProperties[outer.i].ClrType);

            //                                    //Expression innerKeyAccess =
            //                                    //    Expression.Convert(
            //                                    //        Expression.Call(
            //                                    //            innerKeyParameter,
            //                                    //            AnonymousObject.GetValueMethodInfo,
            //                                    //            Expression.Constant(outer.i)),
            //                                    //        foreignKeyProperties[outer.i].ClrType);

            //                                    var innerKeyAccess =
            //                                        Expression.Call(
            //                                            innerKeyParameter,
            //                                            AnonymousObject2.GetValueMethodInfo,
            //                                            Expression.Constant(outer.i));

            //                                    var typedInnerKeyAccess =
            //                                        Expression.Convert(
            //                                            innerKeyAccess,
            //                                            foreignKeyProperties[outer.i].ClrType);







            //                                    //Expression equalityExpression;
            //                                    //if (outerKeyAccess.Type != innerKeyAccess.Type)
            //                                    //{
            //                                    //    if (outerKeyAccess.Type.IsNullableType())
            //                                    //    {
            //                                    //        innerKeyAccess = Expression.Convert(innerKeyAccess, outerKeyAccess.Type);
            //                                    //    }
            //                                    //    else
            //                                    //    {
            //                                    //        outerKeyAccess = Expression.Convert(outerKeyAccess, innerKeyAccess.Type);
            //                                    //    }
            //                                    //}





            //                                    Expression equalityExpression;
            //                                    if (typedOuterKeyAccess.Type != typedInnerKeyAccess.Type)
            //                                    {
            //                                        if (typedOuterKeyAccess.Type.IsNullableType())
            //                                        {
            //                                            typedInnerKeyAccess = Expression.Convert(typedInnerKeyAccess, typedOuterKeyAccess.Type);
            //                                        }
            //                                        else
            //                                        {
            //                                            typedOuterKeyAccess = Expression.Convert(typedOuterKeyAccess, typedInnerKeyAccess.Type);
            //                                        }
            //                                    }













            //                                    //if (typeof(IStructuralEquatable).GetTypeInfo()
            //                                    //    .IsAssignableFrom(pkMemberAccess.Type.GetTypeInfo()))
            //                                    //{
            //                                    //    equalityExpression
            //                                    //        = Expression.Call(_structuralEqualsMethod, pkMemberAccess, fkMemberAccess);
            //                                    //}
            //                                    //else
            //                                    {
            //                                        //equalityExpression = Expression.Equal(outerKeyAccess, innerKeyAccess);
            //                                        equalityExpression = Expression.Equal(typedOuterKeyAccess, typedInnerKeyAccess);
            //                                    }

            //                                    return inner.ClrType.IsNullableType()
            //                                        ? Expression.Condition(
            //                                            Expression.OrElse(
            //                                                Expression.Equal(innerKeyAccess, Expression.Default(innerKeyAccess.Type)),
            //                                                Expression.Equal(outerKeyAccess, Expression.Default(outerKeyAccess.Type)))
            //                                                ,
            //                                            //Expression.Equal(innerKeyAccess, Expression.Constant(null)),
            //                                            Expression.Constant(false),
            //                                            equalityExpression)
            //                                        : equalityExpression;
            //                                })
            //                            .Aggregate((e1, e2) => Expression.AndAlso(e1, e2)),
            //                        outerKeyParameter,
            //                        innerKeyParameter);
            //                }

            //                private static Expression TryCreateCorrelationPredicate(Type targetType, INavigation navigation)
            //                {
            //                    var foreignKey = navigation.ForeignKey;
            //                    var primaryKeyProperties = foreignKey.PrincipalKey.Properties;
            //                    var foreignKeyProperties = foreignKey.Properties;
            //                    var relatedType = navigation.GetTargetType().ClrType;

            //                    if (primaryKeyProperties.Any(p => p.IsShadowProperty)
            //                        || foreignKeyProperties.Any(p => p.IsShadowProperty))
            //                    {
            //                        return
            //                            Expression.Default(typeof(Func<,,>)
            //                                .MakeGenericType(targetType, relatedType, typeof(bool)));
            //                    }

            //                    var targetEntityParameter = Expression.Parameter(typeof(AnonymousObject) /* targetType*/, "p");
            //                    var relatedEntityParameter = Expression.Parameter(relatedType, "d");

            //                    return Expression.Lambda(
            //                        primaryKeyProperties.Zip(foreignKeyProperties,
            //                                (pk, fk) =>
            //                                {
            //                                    Expression pkMemberAccess =
            //                                        Expression.Call(
            //                                            targetEntityParameter,
            //                                            AnonymousObject.GetValueMethodInfo,
            //                                            Expression.Constant(0)); // TODO: hack






            //                                    //Expression pkMemberAccess
            //                                    //    = Expression.MakeMemberAccess(
            //                                    //        targetEntityParameter,
            //                                    //        pk.GetMemberInfo(forConstruction: false, forSet: false));

            //                                    Expression fkMemberAccess
            //                                        = Expression.MakeMemberAccess(
            //                                            relatedEntityParameter,
            //                                            fk.GetMemberInfo(forConstruction: false, forSet: false));

            //                                    if (pkMemberAccess.Type != fkMemberAccess.Type)
            //                                    {
            //                                        // PK is always object because it comes from AnonymousObject - we need to type it correctly to avoid reference comparison
            //                                        pkMemberAccess = Expression.Convert(pkMemberAccess, fkMemberAccess.Type);


            //                                        //if (pkMemberAccess.Type.IsNullableType())
            //                                        //{
            //                                        //    fkMemberAccess = Expression.Convert(fkMemberAccess, pkMemberAccess.Type);
            //                                        //}
            //                                        //else
            //                                        //{
            //                                        //    pkMemberAccess = Expression.Convert(pkMemberAccess, fkMemberAccess.Type);
            //                                        //}
            //                                    }

            //                                    Expression equalityExpression;

            //                                    //if (typeof(IStructuralEquatable).GetTypeInfo()
            //                                    //    .IsAssignableFrom(pkMemberAccess.Type.GetTypeInfo()))
            //                                    //{
            //                                    //    equalityExpression
            //                                    //        = Expression.Call(_structuralEqualsMethod, pkMemberAccess, fkMemberAccess);
            //                                    //}
            //                                    //else
            //                                    {
            //                                        equalityExpression = Expression.Equal(pkMemberAccess, fkMemberAccess);
            //                                    }

            //                                    return fk.ClrType.IsNullableType()
            //                                        ? Expression.Condition(
            //                                            Expression.Equal(fkMemberAccess, Expression.Default(fk.ClrType)),
            //                                            Expression.Constant(false),
            //                                            equalityExpression)
            //                                        : equalityExpression;
            //                                })
            //                            .Aggregate((e1, e2) => Expression.AndAlso(e1, e2)),
            //                        targetEntityParameter,
            //                        relatedEntityParameter);
            //                }



            //            }
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public void InjectSubqueryToCollectionsInProjection(QueryModel queryModel)
        {
            var visitor = new ProjectionSubqueryInjectingQueryModelVisitor(_queryModelVisitor);
            visitor.VisitQueryModel(queryModel);
        }

        private class ProjectionSubqueryInjectingQueryModelVisitor : QueryModelVisitorBase
        {
            private readonly CollectionNavigationSubqueryInjector _subqueryInjector;

            public ProjectionSubqueryInjectingQueryModelVisitor(EntityQueryModelVisitor queryModelVisitor)
            {
                _subqueryInjector = new CollectionNavigationSubqueryInjector(queryModelVisitor, shouldInject: true);
            }

            public override void VisitSelectClause(SelectClause selectClause, QueryModel queryModel)
            {
                selectClause.Selector = _subqueryInjector.Visit(selectClause.Selector);

                base.VisitSelectClause(selectClause, queryModel);
            }
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public void MarkCorrelatedCollections(QueryModel queryModel)
        {
            var correlatedCollectionMarker = new CorrelatedCollectionMarkingExpressionVisitor(_queryModelVisitor);

            queryModel.SelectClause.TransformExpressions(correlatedCollectionMarker.Visit);

            //queryModel.TransformExpressions(correlatedCollectionMarker.Visit);
        }

        //private class CorrelatedCollectionMarkingQueryModelVisitor : QueryModelVisitorBase
        //{
        //    private CorrelatedCollectionMarkingExpressionVisitor _correlatedCollectionMarkingExpressionVisitor;

        //    public CorrelatedCollectionMarkingQueryModelVisitor(EntityQueryModelVisitor queryModelVisitor)
        //    {
        //        _correlatedCollectionMarkingExpressionVisitor = new CorrelatedCollectionMarkingExpressionVisitor(queryModelVisitor);
        //    }

        //    public override void VisitMainFromClause(MainFromClause fromClause, QueryModel queryModel)
        //    {
        //    }

        //    public override void VisitAdditionalFromClause(AdditionalFromClause fromClause, QueryModel queryModel, int index)
        //    {
        //    }

        //    protected override void VisitBodyClauses(ObservableCollection<IBodyClause> bodyClauses, QueryModel queryModel)
        //    {
        //    }

        //    public override void VisitResultOperator(ResultOperatorBase resultOperator, QueryModel queryModel, int index)
        //    {
        //    }

        //    public override void VisitSelectClause(SelectClause selectClause, QueryModel queryModel)
        //    {
        //        _correlatedCollectionMarkingExpressionVisitor.Visit(selectClause.Selector);
        //    }
        //}

        private class CorrelatedCollectionMarkingExpressionVisitor : RelinqExpressionVisitor
        {
            private EntityQueryModelVisitor _queryModelVisitor;

            public CorrelatedCollectionMarkingExpressionVisitor(EntityQueryModelVisitor queryModelVisitor)
            {
                _queryModelVisitor = queryModelVisitor;
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                if (node.Method.Name.StartsWith("IncludeCollection"))
                {
                    return node;
                }

                return base.VisitMethodCall(node);
            }

            // todo: dry
            private class QuerySourceReferenceFindingExpressionTreeVisitor : RelinqExpressionVisitor
            {
                public QuerySourceReferenceExpression QuerySourceReferenceExpression { get; private set; }

                protected override Expression VisitQuerySourceReference(QuerySourceReferenceExpression querySourceReferenceExpression)
                {
                    if (QuerySourceReferenceExpression == null)
                    {
                        QuerySourceReferenceExpression = querySourceReferenceExpression;
                    }

                    return querySourceReferenceExpression;
                }
            }


            private class IsTranslatableExpressionVerifier : RelinqExpressionVisitor
            {
                private MainFromClause _mainFromClause;

                public IsTranslatableExpressionVerifier(MainFromClause mainFromClause)
                {
                    _mainFromClause = mainFromClause;
                }

                public bool IsTranslatable { get; private set; } = true;

                public override Expression Visit(Expression node)
                {
                    if (node is QuerySourceReferenceExpression
                        || node is MemberExpression
                        || node is MethodCallExpression
                        || node is NewExpression)
                    {
                        return base.Visit(node);
                    }
                    else
                    {
                        IsTranslatable = false;

                        return node;
                    }
                }

                protected override Expression VisitQuerySourceReference(QuerySourceReferenceExpression querySourceReferenceExpression)
                {
                    if (IsTranslatable && querySourceReferenceExpression.ReferencedQuerySource != _mainFromClause)
                    {
                        IsTranslatable = false;
                    }

                    return querySourceReferenceExpression;
                }

                protected override Expression VisitMember(MemberExpression node)
                {
                    if (node.Expression is QuerySourceReferenceExpression)
                    {
                        Visit(node.Expression);
                    }
                    else
                    {
                        IsTranslatable = false;
                    }

                    // TODO: assume all members are translatable? or maybe only do it for entity types (i.e. check the property in metadata)

                    return node;
                }

                protected override Expression VisitMethodCall(MethodCallExpression node)
                {
                    if (node.IsEFProperty())
                    {
                        Visit(node.Arguments[0]);
                    }
                    else
                    {
                        IsTranslatable = false;
                    }

                    // TODO: assume all members are translatable? or maybe only do it for entity types (i.e. check the property in metadata)

                    return node;
                }

                protected override Expression VisitNew(NewExpression expression)
                {
                    foreach (var argument in expression.Arguments)
                    {
                        Visit(argument);
                    }

                    return expression;
                }
            }

            protected override Expression VisitSubQuery(SubQueryExpression expression)
            {
                var subQueryModel = expression.QueryModel;
                if (subQueryModel.ResultOperators.Count == 0 // TODO: Distinct?
                    && subQueryModel.SelectClause.Selector is QuerySourceReferenceExpression selectorQsre
                    && selectorQsre.ReferencedQuerySource == subQueryModel.MainFromClause)
                {
                    var newExpression = _queryModelVisitor.BindNavigationPathPropertyExpression(
                        subQueryModel.MainFromClause.FromExpression,
                        (properties, querySource) =>
                        {
                            var collectionNavigation = properties.OfType<INavigation>().SingleOrDefault(n => n.IsCollection());

                            return collectionNavigation != null
                                ? new CorrelatedCollectionMarkingExpression(expression, querySource, properties.OfType<INavigation>().First(), collectionNavigation)
                                : default;
                        });

                    if (newExpression != null)
                    {
                        return newExpression;
                    }
                }

                if (subQueryModel.ResultOperators.Count == 0)
                {

                    // TODO is it still needed or can we translate everything as long as its in projection only?
                    //var isTranslatableSelector = new IsTranslatableExpressionVerifier(subQueryModel.MainFromClause);
                    //isTranslatableSelector.Visit(subQueryModel.SelectClause.Selector);


                    //if (isTranslatableSelector.IsTranslatable)

                    var querySourceReferenceFindingExpressionTreeVisitor
                        = new QuerySourceReferenceFindingExpressionTreeVisitor();

                    querySourceReferenceFindingExpressionTreeVisitor.Visit(subQueryModel.SelectClause.Selector);
                    if (querySourceReferenceFindingExpressionTreeVisitor.QuerySourceReferenceExpression?.ReferencedQuerySource == subQueryModel.MainFromClause)
                    {
                        var newExpression = _queryModelVisitor.BindNavigationPathPropertyExpression(
                            subQueryModel.MainFromClause.FromExpression,
                            (properties, querySource) =>
                            {
                                var collectionNavigation = properties.OfType<INavigation>().SingleOrDefault(n => n.IsCollection());

                                return collectionNavigation != null
                                    ? new CorrelatedCollectionMarkingExpression(expression, querySource, properties.OfType<INavigation>().First(), collectionNavigation)
                                    : default;
                            });

                        if (newExpression != null)
                        {
                            return newExpression;
                        }
                    }
                }

                return base.VisitSubQuery(expression);
            }
        }

        private class CorrelatedCollectionMarkingExpression : Expression
        {
            private readonly Type _type;

            public CorrelatedCollectionMarkingExpression(
                SubQueryExpression operand, 
                IQuerySource originQuerySource,
                INavigation firstNavigation, 
                INavigation lastNavigation)
            {
                Operand = operand;
                OriginQuerySource = originQuerySource;
                FirstNavigation = firstNavigation;
                LastNavigation = lastNavigation;
                _type = operand.Type;
            }

            public virtual SubQueryExpression Operand { get; }

            public virtual IQuerySource OriginQuerySource { get; set; }

            public virtual INavigation FirstNavigation { get; }

            public virtual INavigation LastNavigation { get; }

            public override bool CanReduce => true;
            public override Type Type => _type;
            public override ExpressionType NodeType => ExpressionType.Extension;

            public override Expression Reduce()
                => Operand;

            protected override Expression VisitChildren(ExpressionVisitor visitor)
                => this;

            public override string ToString()
                => $"{nameof(CorrelatedCollectionMarkingExpression)}({Operand})";
        }



        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public void CorrelateSubqueries(QueryModel queryModel)
        {

            var subqueryCorrelatingExpressionVisitor = new SubqueryCorrelatingExpressionVisitor(_queryModelVisitor, queryModel);
            queryModel.TransformExpressions(subqueryCorrelatingExpressionVisitor.Visit);
        }



        private class SubqueryCorrelatingExpressionVisitor : RelinqExpressionVisitor
        {
            private readonly EntityQueryModelVisitor _queryModelVisitor;
            private QueryModel _queryModel;
            private int _correlatedCollectionCount = 0;

            public SubqueryCorrelatingExpressionVisitor(EntityQueryModelVisitor queryModelVisitor, QueryModel queryModel)
            {
                _queryModelVisitor = queryModelVisitor;
                _queryModel = queryModel;
            }

            // todo: dry
            private class QuerySourceReferenceFindingExpressionTreeVisitor : RelinqExpressionVisitor
            {
                public QuerySourceReferenceExpression QuerySourceReferenceExpression { get; private set; }

                protected override Expression VisitQuerySourceReference(QuerySourceReferenceExpression querySourceReferenceExpression)
                {
                    if (QuerySourceReferenceExpression == null)
                    {
                        QuerySourceReferenceExpression = querySourceReferenceExpression;
                    }

                    return querySourceReferenceExpression;
                }
            }




            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                //if (node.Method.MethodIsClosedFormOf(_materializeCorrelatedSubqueryMethodInfo))
                //{
                //    return node;
                //}

                if (node.Method.Name.StartsWith("IncludeCollection"))
                {
                    return node;
                }

                return base.VisitMethodCall(node);
            }

            //protected override Expression VisitMember(MemberExpression node)
            //{
            //    var newExpression = Visit(node.Expression);


            //    return base.VisitMember(node);
            //}




            //internal virtual Expression VisitMemberInit(MemberInitExpression init)
            //{
            //    NewExpression n = this.VisitNew(init.NewExpression);
            //    IEnumerable<MemberBinding> bindings = this.VisitBindingList(init.Bindings);
            //    if (n != init.NewExpression || bindings != init.Bindings)
            //    {
            //        return Expression.MemberInit(n, bindings);
            //    }
            //    return init;
            //}



            //protected override Expression VisitMemberInit(MemberInitExpression node)
            //{
            //    var newNew = (NewExpression)Visit(node.NewExpression);

            //    var newBindings = new List<MemberBinding>();

            //    foreach (var binding in node.Bindings)
            //    {
            //        var assignment = (binding as MemberAssignment).Expression;
            //        var newAssignment = Visit(assignment);
            //        var newbinding = Expression.Bind(binding.Member, newAssignment);

            //        newBindings.Add(newbinding);
            //    }

            //    var foo = Expression.MemberInit(newNew, newBindings);

            //    return foo;

            //}

            protected override Expression VisitExtension(Expression node)
            {
                if (node is CorrelatedCollectionMarkingExpression correlatedCollectionMarkingExpression)
                {
                    return _queryModelVisitor.BindNavigationPathPropertyExpression(
                        correlatedCollectionMarkingExpression.Operand.QueryModel.MainFromClause.FromExpression,
                        (properties, querySource) =>
                        {
                            var collectionNavigation = properties.OfType<INavigation>().SingleOrDefault(n => n.IsCollection());

                            return CorrelateSubquery2(
                                new QuerySourceReferenceExpression(correlatedCollectionMarkingExpression.OriginQuerySource),
                                new QuerySourceReferenceExpression(querySource),
                                correlatedCollectionMarkingExpression.FirstNavigation,
                                correlatedCollectionMarkingExpression.LastNavigation,
                                correlatedCollectionMarkingExpression.Operand);

                                //return properties.Count == 1 && collectionNavigation != null
                                //    ? CorrelateSubquery2(new QuerySourceReferenceExpression(querySource), collectionNavigation, expression)
                                //    : default;
                            });





                }

                return base.VisitExtension(node);
            }

            //protected override Expression VisitSubQuery(SubQueryExpression expression)
            //{
            //    var subQueryModel = expression.QueryModel;
            //    if (subQueryModel.ResultOperators.Count == 0
            //        && subQueryModel.SelectClause.Selector is QuerySourceReferenceExpression selectorQsre
            //        && selectorQsre.ReferencedQuerySource == subQueryModel.MainFromClause
            //        && subQueryModel.MainFromClause.FromExpression is MemberExpression fromMemberExpression)
            //    {
            //        var newMemberExpression = _queryModelVisitor.BindNavigationPathPropertyExpression(
            //            fromMemberExpression,
            //            (properties, querySource) =>
            //            {
            //                var collectionNavigation = properties.OfType<INavigation>().SingleOrDefault(n => n.IsCollection());

            //                return properties.Count == 1 && collectionNavigation != null
            //                    ? CorrelateSubquery2(new QuerySourceReferenceExpression(querySource), collectionNavigation, expression)
            //                    : default;
            //            });

            //        if (newMemberExpression != null)
            //        {
            //            return newMemberExpression;
            //        }
            //    }

            //    if (subQueryModel.ResultOperators.Count == 0)
            //        //&& (subQueryModel.MainFromClause.FromExpression is MemberExpression 
            //        //    || (subQueryModel.MainFromClause.FromExpression as MethodCallExpression)?.IsEFProperty()))
            //    {
            //        var querySourceReferenceFindingExpressionTreeVisitor
            //            = new QuerySourceReferenceFindingExpressionTreeVisitor();

            //        querySourceReferenceFindingExpressionTreeVisitor.Visit(subQueryModel.SelectClause.Selector);
            //        if (querySourceReferenceFindingExpressionTreeVisitor.QuerySourceReferenceExpression.ReferencedQuerySource == subQueryModel.MainFromClause)
            //        {
            //            var newMemberExpression = _queryModelVisitor.BindNavigationPathPropertyExpression(
            //            subQueryModel.MainFromClause.FromExpression,
            //            (properties, querySource) =>
            //            {
            //                var collectionNavigation = properties.OfType<INavigation>().SingleOrDefault(n => n.IsCollection());

            //                return properties.Count == 1 && collectionNavigation != null // TODO: no navigation chaining for now
            //                    ? CorrelateSubquery2(new QuerySourceReferenceExpression(querySource), collectionNavigation, expression)
            //                    : default;
            //            });

            //            if (newMemberExpression != null)
            //            {
            //                return newMemberExpression;
            //            }
            //        }
            //    }

            //    return base.VisitSubQuery(expression);
            //}


            private static MethodInfo _correlateSubqueryMethodInfo = typeof(IQueryBuffer).GetMethod(nameof(IQueryBuffer.CorrelateSubquery));

            private List<QueryModel> _processedQueryModels = new List<QueryModel>();

            private Dictionary<QueryModel, Type> _processedQueryModelsMap = new Dictionary<QueryModel, Type>();

            private Expression CorrelateSubquery2(
                QuerySourceReferenceExpression originQuerySourceExpression,
                QuerySourceReferenceExpression outerExpression,
                INavigation firstNavigation,
                INavigation collectionNavigation,
                SubQueryExpression subQueryExpression)
            {
                var subQueryModel = subQueryExpression.QueryModel;

                var outerKey = BuildKeyAccess(collectionNavigation.ForeignKey.PrincipalKey.Properties, outerExpression);
                var innerKey = BuildKeyAccess(collectionNavigation.ForeignKey.Properties, new QuerySourceReferenceExpression(subQueryModel.MainFromClause));
                var correlationnPredicate = CreateCorrelationPredicate(collectionNavigation);


                if (_processedQueryModels.Contains(subQueryModel))
                {
                    subQueryModel = subQueryModel.Clone();
                }
                else
                {
                    _processedQueryModels.Add(subQueryModel);
                }

                var subQueryResultElementType = subQueryModel.SelectClause.Selector.Type;



                //                if (!_processedQueryModelsMap.TryGetValue(subQueryModel, out var subQueryResultElementType))
                {
                    //var subQueryResultElementType = subQueryModel.SelectClause.Selector.Type;
                    //_processedQueryModelsMap.Add(subQueryModel, subQueryResultElementType);


                //}

                //if (!_processedQueryModels.Contains(subQueryModel))
                //{
                //    _processedQueryModels.Add(subQueryModel);

                    var kvp = typeof(KeyValuePair<,>).MakeGenericType(subQueryResultElementType, typeof(AnonymousObject2));
                    var kvpCtor = kvp.GetTypeInfo().DeclaredConstructors.FirstOrDefault();

                    var kvp2 = typeof(KeyValuePair<,>).MakeGenericType(kvp, typeof(AnonymousObject2));
                    var kvp2Ctor = kvp2.GetTypeInfo().DeclaredConstructors.FirstOrDefault();

                    var originEntityType = _queryModelVisitor.QueryCompilationContext.Model.FindEntityType(originQuerySourceExpression.Type);
                    var originKey = BuildKeyAccess(originEntityType.FindPrimaryKey().Properties, originQuerySourceExpression);

                    subQueryModel.SelectClause.Selector = Expression.New(
                        kvp2Ctor,
                        Expression.New(kvpCtor, subQueryModel.SelectClause.Selector, innerKey),
                        originKey);

                    //subQueryModel.SelectClause.Selector = Expression.New(kvpCtor, subQueryModel.SelectClause.Selector, innerKey);
                    subQueryModel.ResultTypeOverride = typeof(IEnumerable<>).MakeGenericType(subQueryModel.SelectClause.Selector.Type);
                }

                var arguments = new List<Expression>
                    {
                        Expression.Constant(_correlatedCollectionCount++),
                        originQuerySourceExpression,
                        Expression.Constant(collectionNavigation),
                        Expression.Constant(firstNavigation),
                        outerKey,
                        Expression.Lambda(new SubQueryExpression(subQueryModel)),
                        correlationnPredicate
                    };

                var generic = _correlateSubqueryMethodInfo.MakeGenericMethod(subQueryResultElementType);

                var result = Expression.Call(
                    Expression.Property(
                        EntityQueryModelVisitor.QueryContextParameter,
                        nameof(QueryContext.QueryBuffer)),
                    generic,
                    arguments);

                return result;

                //// TODO: needed?
                //var resultCollectionType = collectionNavigation.GetCollectionAccessor().CollectionType;

                //return resultCollectionType.GetTypeInfo().IsGenericType && resultCollectionType.GetGenericTypeDefinition() == typeof(ICollection<>)
                //    ? (Expression)result
                //    : Expression.Convert(result, resultCollectionType);
            }

            //    private Expression CorrelateSubquery(QuerySourceReferenceExpression outerExpression, INavigation collectionNavigation, Expression subqueryExpression)
            //    {
            //        var outerKey = BuildOuterKey(collectionNavigation, outerExpression);

            //        var correlationnPredicate = TryCreateCorrelationPredicate(collectionNavigation.DeclaringEntityType.ClrType, collectionNavigation);

            //        if (correlationnPredicate is DefaultExpression)
            //        {
            //            return null;
            //        }

            //        var arguments = new List<Expression>
            //        {
            //            Expression.Constant(1),
            //            Expression.Constant(collectionNavigation/*.GetCollectionAccessor()*/),
            //            //Expression.Constant(collectionNavigation.GetCollectionAccessor()),
            //            outerKey,
            //            Expression.Lambda(subqueryExpression),
            //            correlationnPredicate
            //        };

            //        var targetType = collectionNavigation.GetTargetType().ClrType;

            //        var generic = _materializeCorrelatedSubqueryMethodInfo.MakeGenericMethod(targetType);
            //        //var generic = _materializeCorrelatedSubqueryMethodInfo.MakeGenericMethod(collectionNavigation.GetTargetType().ClrType);

            //        var result = Expression.Call(
            //            Expression.Property(
            //                EntityQueryModelVisitor.QueryContextParameter,
            //                nameof(QueryContext.QueryBuffer)),
            //            generic,
            //            arguments);

            //        /*
            //         * 
            //         * 
            //         * 
            //         *             int childId,
            //IClrCollectionAccessor clrCollectionAccessor,
            //TOuter outer,
            //Func<IEnumerable<TInner>> relatedEntitiesFactory,
            //Func<TOuter, TInner, bool> joinPredicate)
            //         * 
            //         * 
            //         * 
            //         */

            //        var resultCollectionType = collectionNavigation.GetCollectionAccessor().CollectionType;

            //        //TODO: not needed?
            //        result = Expression.Call(
            //             CollectionNavigationSubqueryInjector.MaterializeCollectionNavigationMethodInfo.MakeGenericMethod(targetType),
            //            Expression.Constant(collectionNavigation), /*subqueryExpression*/result);

            //        return resultCollectionType.GetTypeInfo().IsGenericType && resultCollectionType.GetGenericTypeDefinition() == typeof(ICollection<>)
            //            ? (Expression)result
            //            : Expression.Convert(result, resultCollectionType);




            //        //return result;
            //        //var targetType = collectionNavigation.GetTargetType().ClrType;
            //        //var mainFromClause = new MainFromClause(targetType.Name.Substring(0, 1).ToLowerInvariant(), targetType, expression);
            //        //var selector = new QuerySourceReferenceExpression(mainFromClause);

            //        //var subqueryModel = new QueryModel(mainFromClause, new SelectClause(selector));
            //        //var subqueryExpression = new SubQueryExpression(subqueryModel);

            //        //var resultCollectionType = collectionNavigation.GetCollectionAccessor().CollectionType;

            //        //var result = Expression.Call(
            //        //    MaterializeCollectionNavigationMethodInfo.MakeGenericMethod(targetType),
            //        //    Expression.Constant(collectionNavigation), subqueryExpression);

            //        //return resultCollectionType.GetTypeInfo().IsGenericType && resultCollectionType.GetGenericTypeDefinition() == typeof(ICollection<>)
            //        //    ? (Expression)result
            //        //    : Expression.Convert(result, resultCollectionType);
            //    }



            private static Expression BuildKeyAccess(IEnumerable<IProperty> keyProperties, Expression qsre)
            {
                var keyAccessExpressions = keyProperties.Select(p => qsre.CreateEFPropertyExpression(p)).ToArray();

                //                    var keyAccessExpressions = keyProperties.Select(p => Expression.MakeMemberAccess(qsre, p.GetMemberInfo(forConstruction: false, forSet: false))).ToArray();

                return Expression.New(
                    AnonymousObject2.AnonymousObjectCtor,
                    Expression.NewArrayInit(
                        typeof(object),
                        keyAccessExpressions.Select(k => Expression.Convert(k, typeof(object)))));
            }

            private static Expression BuildOuterKey(INavigation navigation, Expression outerQsre)
            {
                var foreignKey = navigation.ForeignKey;
                var primaryKeyProperties = foreignKey.PrincipalKey.Properties;

                var pks = primaryKeyProperties.Select(p => Expression.MakeMemberAccess(outerQsre, p.GetMemberInfo(forConstruction: false, forSet: false))).ToArray();

                return Expression.New(
                    AnonymousObject.AnonymousObjectCtor,
                    Expression.NewArrayInit(
                        typeof(object),
                        pks.Select(k => Expression.Convert(k, typeof(object)))));
            }

            private static Expression CreateCorrelationPredicate(INavigation navigation)
            {
                var foreignKey = navigation.ForeignKey;
                var primaryKeyProperties = foreignKey.PrincipalKey.Properties;
                var foreignKeyProperties = foreignKey.Properties;

                var outerKeyParameter = Expression.Parameter(typeof(AnonymousObject2), "o");
                var innerKeyParameter = Expression.Parameter(typeof(AnonymousObject2), "i");

                return Expression.Lambda(
                    primaryKeyProperties
                        .Select((pk, i) => new { pk, i })
                        .Zip(
                            foreignKeyProperties,
                            (outer, inner) =>
                            {
                                    //Expression outerKeyAccess =
                                    //    Expression.Convert(
                                    //        Expression.Call(
                                    //            outerKeyParameter,
                                    //            AnonymousObject.GetValueMethodInfo,
                                    //            Expression.Constant(outer.i)),
                                    //        primaryKeyProperties[outer.i].ClrType);

                                    var outerKeyAccess =
                                    Expression.Call(
                                        outerKeyParameter,
                                        AnonymousObject2.GetValueMethodInfo,
                                        Expression.Constant(outer.i));

                                var typedOuterKeyAccess =
                                    Expression.Convert(
                                        outerKeyAccess,
                                        primaryKeyProperties[outer.i].ClrType);

                                    //Expression innerKeyAccess =
                                    //    Expression.Convert(
                                    //        Expression.Call(
                                    //            innerKeyParameter,
                                    //            AnonymousObject.GetValueMethodInfo,
                                    //            Expression.Constant(outer.i)),
                                    //        foreignKeyProperties[outer.i].ClrType);

                                    var innerKeyAccess =
                                    Expression.Call(
                                        innerKeyParameter,
                                        AnonymousObject2.GetValueMethodInfo,
                                        Expression.Constant(outer.i));

                                var typedInnerKeyAccess =
                                    Expression.Convert(
                                        innerKeyAccess,
                                        foreignKeyProperties[outer.i].ClrType);







                                    //Expression equalityExpression;
                                    //if (outerKeyAccess.Type != innerKeyAccess.Type)
                                    //{
                                    //    if (outerKeyAccess.Type.IsNullableType())
                                    //    {
                                    //        innerKeyAccess = Expression.Convert(innerKeyAccess, outerKeyAccess.Type);
                                    //    }
                                    //    else
                                    //    {
                                    //        outerKeyAccess = Expression.Convert(outerKeyAccess, innerKeyAccess.Type);
                                    //    }
                                    //}





                                    Expression equalityExpression;
                                if (typedOuterKeyAccess.Type != typedInnerKeyAccess.Type)
                                {
                                    if (typedOuterKeyAccess.Type.IsNullableType())
                                    {
                                        typedInnerKeyAccess = Expression.Convert(typedInnerKeyAccess, typedOuterKeyAccess.Type);
                                    }
                                    else
                                    {
                                        typedOuterKeyAccess = Expression.Convert(typedOuterKeyAccess, typedInnerKeyAccess.Type);
                                    }
                                }













                                    //if (typeof(IStructuralEquatable).GetTypeInfo()
                                    //    .IsAssignableFrom(pkMemberAccess.Type.GetTypeInfo()))
                                    //{
                                    //    equalityExpression
                                    //        = Expression.Call(_structuralEqualsMethod, pkMemberAccess, fkMemberAccess);
                                    //}
                                    //else
                                    {
                                        //equalityExpression = Expression.Equal(outerKeyAccess, innerKeyAccess);
                                        equalityExpression = Expression.Equal(typedOuterKeyAccess, typedInnerKeyAccess);
                                }

                                return inner.ClrType.IsNullableType()
                                    ? Expression.Condition(
                                        Expression.OrElse(
                                            Expression.Equal(innerKeyAccess, Expression.Default(innerKeyAccess.Type)),
                                            Expression.Equal(outerKeyAccess, Expression.Default(outerKeyAccess.Type)))
                                            ,
                                        //Expression.Equal(innerKeyAccess, Expression.Constant(null)),
                                        Expression.Constant(false),
                                        equalityExpression)
                                    : equalityExpression;
                            })
                        .Aggregate((e1, e2) => Expression.AndAlso(e1, e2)),
                    outerKeyParameter,
                    innerKeyParameter);
            }

            private static Expression TryCreateCorrelationPredicate(Type targetType, INavigation navigation)
            {
                var foreignKey = navigation.ForeignKey;
                var primaryKeyProperties = foreignKey.PrincipalKey.Properties;
                var foreignKeyProperties = foreignKey.Properties;
                var relatedType = navigation.GetTargetType().ClrType;

                if (primaryKeyProperties.Any(p => p.IsShadowProperty)
                    || foreignKeyProperties.Any(p => p.IsShadowProperty))
                {
                    return
                        Expression.Default(typeof(Func<,,>)
                            .MakeGenericType(targetType, relatedType, typeof(bool)));
                }

                var targetEntityParameter = Expression.Parameter(typeof(AnonymousObject) /* targetType*/, "p");
                var relatedEntityParameter = Expression.Parameter(relatedType, "d");

                return Expression.Lambda(
                    primaryKeyProperties.Zip(foreignKeyProperties,
                            (pk, fk) =>
                            {
                                Expression pkMemberAccess =
                                    Expression.Call(
                                        targetEntityParameter,
                                        AnonymousObject.GetValueMethodInfo,
                                        Expression.Constant(0)); // TODO: hack






                                    //Expression pkMemberAccess
                                    //    = Expression.MakeMemberAccess(
                                    //        targetEntityParameter,
                                    //        pk.GetMemberInfo(forConstruction: false, forSet: false));

                                    Expression fkMemberAccess
                                    = Expression.MakeMemberAccess(
                                        relatedEntityParameter,
                                        fk.GetMemberInfo(forConstruction: false, forSet: false));

                                if (pkMemberAccess.Type != fkMemberAccess.Type)
                                {
                                        // PK is always object because it comes from AnonymousObject - we need to type it correctly to avoid reference comparison
                                        pkMemberAccess = Expression.Convert(pkMemberAccess, fkMemberAccess.Type);


                                        //if (pkMemberAccess.Type.IsNullableType())
                                        //{
                                        //    fkMemberAccess = Expression.Convert(fkMemberAccess, pkMemberAccess.Type);
                                        //}
                                        //else
                                        //{
                                        //    pkMemberAccess = Expression.Convert(pkMemberAccess, fkMemberAccess.Type);
                                        //}
                                    }

                                Expression equalityExpression;

                                    //if (typeof(IStructuralEquatable).GetTypeInfo()
                                    //    .IsAssignableFrom(pkMemberAccess.Type.GetTypeInfo()))
                                    //{
                                    //    equalityExpression
                                    //        = Expression.Call(_structuralEqualsMethod, pkMemberAccess, fkMemberAccess);
                                    //}
                                    //else
                                    {
                                    equalityExpression = Expression.Equal(pkMemberAccess, fkMemberAccess);
                                }

                                return fk.ClrType.IsNullableType()
                                    ? Expression.Condition(
                                        Expression.Equal(fkMemberAccess, Expression.Default(fk.ClrType)),
                                        Expression.Constant(false),
                                        equalityExpression)
                                    : equalityExpression;
                            })
                        .Aggregate((e1, e2) => Expression.AndAlso(e1, e2)),
                    targetEntityParameter,
                    relatedEntityParameter);
            }



        }


    }
}
