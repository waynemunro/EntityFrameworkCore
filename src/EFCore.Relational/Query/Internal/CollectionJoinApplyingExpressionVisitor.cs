// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.Query.Internal
{
    public class CollectionJoinApplyingExpressionVisitor : ExpressionVisitor
    {
        private int _collectionId;

        protected override Expression VisitExtension(Expression extensionExpression)
        {
            Check.NotNull(extensionExpression, nameof(extensionExpression));

            if (extensionExpression is CollectionShaperExpression collectionShaperExpression)
            {
                var collectionId = _collectionId++;
                var projectionBindingExpression = (ProjectionBindingExpression)collectionShaperExpression.Projection;
                var selectExpression = (SelectExpression)projectionBindingExpression.QueryExpression;
                // Do pushdown beforehand so it updates all pending collections first
                if (selectExpression.IsDistinct
                    || selectExpression.Limit != null
                    || selectExpression.Offset != null
                    || selectExpression.GroupBy.Count > 0)
                {
                    selectExpression.PushdownIntoSubquery();
                }

                var innerShaper = Visit(collectionShaperExpression.InnerShaper);

                return selectExpression.ApplyCollectionJoin(
                    projectionBindingExpression.Index.Value,
                    collectionId,
                    innerShaper,
                    collectionShaperExpression.Navigation,
                    collectionShaperExpression.ElementType);
            }

            return extensionExpression is ShapedQueryExpression shapedQueryExpression
                ? shapedQueryExpression.UpdateShaperExpression(Visit(shapedQueryExpression.ShaperExpression))
                : base.VisitExtension(extensionExpression);
        }
    }
}
