// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.Query.Internal
{
    public partial class NavigationExpandingExpressionVisitor
    {
        private sealed class EntityReference : Expression, IPrintableExpression
        {
            public EntityReference(IEntityType entityType)
            {
                EntityType = entityType;
                IncludePaths = new IncludeTreeNode(entityType, this);
            }

            public IEntityType EntityType { get; }
            public IDictionary<INavigation, Expression> NavigationMap { get; } = new Dictionary<INavigation, Expression>();

            public bool IsOptional { get; private set; }
            public IncludeTreeNode IncludePaths { get; private set; }
            public IncludeTreeNode LastIncludeTreeNode { get; private set; }
            public override ExpressionType NodeType => ExpressionType.Extension;
            public override Type Type => EntityType.ClrType;

            protected override Expression VisitChildren(ExpressionVisitor visitor)
            {
                Check.NotNull(visitor, nameof(visitor));

                return this;
            }

            public void SetIncludePaths(IncludeTreeNode includePaths)
            {
                IncludePaths = includePaths;
                includePaths.SetEntityReference(this);
            }

            public EntityReference Clone()
            {
                var result = new EntityReference(EntityType) { IsOptional = IsOptional };
                result.IncludePaths = IncludePaths.Clone(result);

                return result;
            }

            public void SetLastInclude(IncludeTreeNode lastIncludeTree) => LastIncludeTreeNode = lastIncludeTree;

            public void MarkAsOptional() => IsOptional = true;

            public void Print(ExpressionPrinter expressionPrinter)
            {
                Check.NotNull(expressionPrinter, nameof(expressionPrinter));

                expressionPrinter.Append($"{nameof(EntityReference)}: {EntityType.DisplayName()}");
                if (IsOptional)
                {
                    expressionPrinter.Append("[Optional]");
                }

                if (IncludePaths.Count > 0)
                {
                    // TODO: fully render nested structure of include tree
                    expressionPrinter.Append(
                        " | IncludePaths: "
                        + string.Join(
                            " ", IncludePaths.Select(ip => ip.Value.Count() > 0 ? ip.Key.Name + "->..." : ip.Key.Name)));
                }
            }
        }

        /// <summary>
        ///     A tree structure of includes for a given entity type in <see cref="EntityReference"/>.
        /// </summary>
        private sealed class IncludeTreeNode : Dictionary<INavigation, IncludeTreeNode>
        {
            private EntityReference _entityReference;

            public LambdaExpression FilterExpression { get; set; }

            public IncludeTreeNode(IEntityType entityType, EntityReference entityReference)
            {
                EntityType = entityType;
                _entityReference = entityReference;
            }

            public IEntityType EntityType { get; private set; }

            public IncludeTreeNode AddNavigation(INavigation navigation)
            {
                if (TryGetValue(navigation, out var existingValue))
                {
                    return existingValue;
                }

                if (_entityReference != null
                    && _entityReference.NavigationMap.TryGetValue(navigation, out var expandedNavigation))
                {
                    var entityReference = expandedNavigation switch
                    {
                        NavigationTreeExpression navigationTree => (EntityReference)navigationTree.Value,
                        OwnedNavigationReference ownedNavigationReference => ownedNavigationReference.EntityReference,
                        _ => throw new InvalidOperationException(CoreStrings.InvalidExpressionTypeStoredInNavigationMap),
                    };

                    this[navigation] = entityReference.IncludePaths;
                }
                else
                {
                    this[navigation] = new IncludeTreeNode(navigation.TargetEntityType, null);
                }

                return this[navigation];
            }

            public void SetEntityReference(EntityReference entityReference)
            {
                _entityReference = entityReference;
                EntityType = entityReference.EntityType;
            }

            public IncludeTreeNode Clone(EntityReference entityReference)
            {
                var result = new IncludeTreeNode(EntityType, entityReference);
                foreach (var kvp in this)
                {
                    result[kvp.Key] = kvp.Value.Clone(kvp.Value._entityReference);
                }

                return result;
            }

            public override bool Equals(object obj)
                => obj != null
                    && (ReferenceEquals(this, obj)
                        || obj is IncludeTreeNode includeTreeNode
                        && Equals(includeTreeNode));

            private bool Equals(IncludeTreeNode includeTreeNode)
            {
                if (Count != includeTreeNode.Count)
                {
                    return false;
                }

                foreach (var kvp in this)
                {
                    if (!includeTreeNode.TryGetValue(kvp.Key, out var otherIncludeTreeNode)
                        || !kvp.Value.Equals(otherIncludeTreeNode))
                    {
                        return false;
                    }
                }

                return true;
            }

            public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), EntityType);
        }

        /// <summary>
        ///     Stores information about the current queryable, its source, structure of projection, parameter type etc.
        ///     This is needed because once navigations are expanded we still remember these to avoid expanding again.
        /// </summary>
        private sealed class NavigationExpansionExpression : Expression, IPrintableExpression
        {
            private readonly List<(MethodInfo OrderingMethod, Expression KeySelector)> _pendingOrderings
                = new List<(MethodInfo OrderingMethod, Expression KeySelector)>();
            private readonly string _parameterName;

            private NavigationTreeNode _currentTree;

            public NavigationExpansionExpression(
                Expression source, NavigationTreeNode currentTree, Expression pendingSelector, string parameterName)
            {
                Source = source;
                _parameterName = parameterName;
                CurrentTree = currentTree;
                PendingSelector = pendingSelector;
            }

            public Expression Source { get; private set; }
            public ParameterExpression CurrentParameter => CurrentTree.CurrentParameter;

            public NavigationTreeNode CurrentTree
            {
                get => _currentTree;
                private set
                {
                    _currentTree = value;
                    _currentTree.SetParameter(_parameterName);
                }
            }

            public Expression PendingSelector { get; private set; }
            public MethodInfo CardinalityReducingGenericMethodInfo { get; private set; }
            public Type SourceElementType => CurrentParameter.Type;
            public IReadOnlyList<(MethodInfo OrderingMethod, Expression KeySelector)> PendingOrderings => _pendingOrderings;

            public void UpdateSource(Expression source) => Source = source;

            public void UpdateCurrentTree(NavigationTreeNode currentTree) => CurrentTree = currentTree;

            public void ApplySelector(Expression selector) => PendingSelector = selector;

            public void AddPendingOrdering(MethodInfo orderingMethod, Expression keySelector)
            {
                _pendingOrderings.Clear();
                _pendingOrderings.Add((orderingMethod, keySelector));
            }

            public void AppendPendingOrdering(MethodInfo orderingMethod, Expression keySelector)
                => _pendingOrderings.Add((orderingMethod, keySelector));

            public void ClearPendingOrderings()
                => _pendingOrderings.Clear();

            public void ConvertToSingleResult(MethodInfo genericMethod)
                => CardinalityReducingGenericMethodInfo = genericMethod;

            public override ExpressionType NodeType => ExpressionType.Extension;
            public override Type Type => CardinalityReducingGenericMethodInfo == null
                ? typeof(IQueryable<>).MakeGenericType(PendingSelector.Type)
                : PendingSelector.Type;
            protected override Expression VisitChildren(ExpressionVisitor visitor)
            {
                Check.NotNull(visitor, nameof(visitor));

                return this;
            }

            public void Print(ExpressionPrinter expressionPrinter)
            {
                Check.NotNull(expressionPrinter, nameof(expressionPrinter));

                expressionPrinter.AppendLine(nameof(NavigationExpansionExpression));
                using (expressionPrinter.Indent())
                {
                    expressionPrinter.Append("Source: ");
                    expressionPrinter.Visit(Source);
                    expressionPrinter.AppendLine();
                    expressionPrinter.Append("PendingSelector: ");
                    expressionPrinter.Visit(Lambda(PendingSelector, CurrentParameter));
                    expressionPrinter.AppendLine();
                    if (CardinalityReducingGenericMethodInfo != null)
                    {
                        expressionPrinter.AppendLine("CardinalityReducingMethod: " + CardinalityReducingGenericMethodInfo.Name);
                    }
                }
            }
        }

        /// <summary>
        ///     A leaf node on navigation tree, representing projection structures of
        ///     <see cref="NavigationExpansionExpression"/>. Contains <see cref="Value"/>,
        ///     which can be <see cref="NewExpression"/> or <see cref="EntityReference"/>.
        /// </summary>
        private sealed class NavigationTreeExpression : NavigationTreeNode, IPrintableExpression
        {
            public NavigationTreeExpression(Expression value)
                : base(null, null)
            {
                Value = value;
            }

            /// <summary>
            ///     Either <see cref="NewExpression"/> or <see cref="EntityReference"/>.
            /// </summary>
            public Expression Value { get; private set; }

            protected override Expression VisitChildren(ExpressionVisitor visitor)
            {
                Check.NotNull(visitor, nameof(visitor));

                Value = visitor.Visit(Value);

                return this;
            }

            public override Type Type => Value.Type;

            public void Print(ExpressionPrinter expressionPrinter)
            {
                Check.NotNull(expressionPrinter, nameof(expressionPrinter));

                expressionPrinter.AppendLine(nameof(NavigationTreeExpression));
                using (expressionPrinter.Indent())
                {
                    expressionPrinter.Append("Value: ");
                    expressionPrinter.Visit(Value);
                    expressionPrinter.AppendLine();
                    expressionPrinter.Append("Expression: ");
                    expressionPrinter.Visit(GetExpression());
                }
            }
        }

        /// <summary>
        ///     A node in navigation binary tree. A navigation tree is a structure of the current parameter, which
        ///     would be transparent identifier (hence it's a binary structure). This allows us to easily condense to
        ///     inner/outer member access.
        /// </summary>
        private class NavigationTreeNode : Expression
        {
            private NavigationTreeNode _parent;

            public NavigationTreeNode(NavigationTreeNode left, NavigationTreeNode right)
            {
                Left = left;
                Right = right;
                if (left != null)
                {
                    Left.Parent = this;
                    Right.Parent = this;
                }
            }

            public NavigationTreeNode Parent
            {
                get => _parent;
                private set
                {
                    _parent = value;
                    CurrentParameter = null;
                }
            }

            public NavigationTreeNode Left { get; }
            public NavigationTreeNode Right { get; }
            public ParameterExpression CurrentParameter { get; private set; }

            public void SetParameter(string parameterName) => CurrentParameter = Parameter(Type, parameterName);

            public override ExpressionType NodeType => ExpressionType.Extension;
            public override Type Type => TransparentIdentifierFactory.Create(Left.Type, Right.Type);

            public Expression GetExpression()
            {
                if (Parent == null)
                {
                    return CurrentParameter;
                }

                var parentExperssion = Parent.GetExpression();
                return Parent.Left == this
                    ? MakeMemberAccess(parentExperssion, parentExperssion.Type.GetMember("Outer")[0])
                    : MakeMemberAccess(parentExperssion, parentExperssion.Type.GetMember("Inner")[0]);
            }
        }

        /// <summary>
        ///     Owned navigations are not expanded, since they map differently in different providers.
        ///     This remembers such references so that they can still be treated like navigations.
        /// </summary>
        private sealed class OwnedNavigationReference : Expression
        {
            public OwnedNavigationReference(Expression parent, INavigation navigation, EntityReference entityReference)
            {
                Parent = parent;
                Navigation = navigation;
                EntityReference = entityReference;
            }

            protected override Expression VisitChildren(ExpressionVisitor visitor)
            {
                Check.NotNull(visitor, nameof(visitor));

                Parent = visitor.Visit(Parent);

                return this;
            }

            public Expression Parent { get; private set; }
            public INavigation Navigation { get; }
            public EntityReference EntityReference { get; }

            public override Type Type => Navigation.ClrType;
            public override ExpressionType NodeType => ExpressionType.Extension;
        }
    }
}
