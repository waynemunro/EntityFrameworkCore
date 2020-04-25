// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.Query.SqlExpressions
{
    public class IntersectExpression : SetOperationBase
    {
        public IntersectExpression(
            [NotNull] string alias, [NotNull] SelectExpression source1, [NotNull] SelectExpression source2, bool distinct)
            : base(alias, source1, source2, distinct)
        {
        }

        protected override Expression VisitChildren(ExpressionVisitor visitor)
        {
            Check.NotNull(visitor, nameof(visitor));

            var source1 = (SelectExpression)visitor.Visit(Source1);
            var source2 = (SelectExpression)visitor.Visit(Source2);

            return Update(source1, source2);
        }

        public virtual IntersectExpression Update([NotNull] SelectExpression source1, [NotNull] SelectExpression source2)
        {
            Check.NotNull(source1, nameof(source1));
            Check.NotNull(source2, nameof(source2));

            return source1 != Source1 || source2 != Source2
                ? new IntersectExpression(Alias, source1, source2, IsDistinct)
                : this;
        }

        public override void Print(ExpressionPrinter expressionPrinter)
        {
            Check.NotNull(expressionPrinter, nameof(expressionPrinter));

            expressionPrinter.Append("(");
            using (expressionPrinter.Indent())
            {
                expressionPrinter.Visit(Source1);
                expressionPrinter.AppendLine();
                expressionPrinter.Append("INTERSECT");
                if (!IsDistinct)
                {
                    expressionPrinter.AppendLine(" ALL");
                }

                expressionPrinter.Visit(Source2);
            }

            expressionPrinter.AppendLine()
                .AppendLine($") AS {Alias}");
        }

        public override bool Equals(object obj)
            => obj != null
                && (ReferenceEquals(this, obj)
                    || obj is IntersectExpression intersectExpression
                    && Equals(intersectExpression));

        private bool Equals(IntersectExpression intersectExpression)
            => base.Equals(intersectExpression);

        public override int GetHashCode()
            => HashCode.Combine(base.GetHashCode(), GetType());
    }
}
