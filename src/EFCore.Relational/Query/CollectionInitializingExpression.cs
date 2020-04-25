// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.Query
{
    public class CollectionInitializingExpression : Expression, IPrintableExpression
    {
        public CollectionInitializingExpression(
            int collectionId,
            [CanBeNull] Expression parent,
            [NotNull] Expression parentIdentifier,
            [NotNull] Expression outerIdentifier,
            [CanBeNull] INavigation navigation,
            [NotNull] Type type)
        {
            Check.NotNull(parentIdentifier, nameof(parentIdentifier));
            Check.NotNull(outerIdentifier, nameof(outerIdentifier));
            Check.NotNull(type, nameof(type));

            CollectionId = collectionId;
            Parent = parent;
            ParentIdentifier = parentIdentifier;
            OuterIdentifier = outerIdentifier;
            Navigation = navigation;
            Type = type;
        }

        protected override Expression VisitChildren(ExpressionVisitor visitor)
        {
            Check.NotNull(visitor, nameof(visitor));

            var parent = visitor.Visit(Parent);
            var parentIdentifier = visitor.Visit(ParentIdentifier);
            var outerIdentifier = visitor.Visit(OuterIdentifier);

            return parent != Parent || parentIdentifier != ParentIdentifier || outerIdentifier != OuterIdentifier
                ? new CollectionInitializingExpression(CollectionId, parent, parentIdentifier, outerIdentifier, Navigation, Type)
                : this;
        }

        public virtual void Print(ExpressionPrinter expressionPrinter)
        {
            Check.NotNull(expressionPrinter, nameof(expressionPrinter));

            expressionPrinter.AppendLine("InitializeCollection:");
            using (expressionPrinter.Indent())
            {
                expressionPrinter.AppendLine($"CollectionId: {CollectionId}");
                expressionPrinter.AppendLine($"Navigation: {Navigation?.Name}");
                expressionPrinter.Append("Parent:");
                expressionPrinter.Visit(Parent);
                expressionPrinter.AppendLine();
                expressionPrinter.Append("ParentIdentifier:");
                expressionPrinter.Visit(ParentIdentifier);
                expressionPrinter.AppendLine();
                expressionPrinter.Append("OuterIdentifier:");
                expressionPrinter.Visit(OuterIdentifier);
                expressionPrinter.AppendLine();
            }
        }

        public override Type Type { get; }

        public sealed override ExpressionType NodeType => ExpressionType.Extension;

        public virtual int CollectionId { get; }
        public virtual Expression Parent { get; }
        public virtual Expression ParentIdentifier { get; }
        public virtual Expression OuterIdentifier { get; }
        public virtual INavigation Navigation { get; }
    }
}
