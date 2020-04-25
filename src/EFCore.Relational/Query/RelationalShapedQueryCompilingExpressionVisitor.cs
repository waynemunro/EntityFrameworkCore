// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.Query
{
    public partial class RelationalShapedQueryCompilingExpressionVisitor : ShapedQueryCompilingExpressionVisitor
    {
        private readonly Type _contextType;
        private readonly IDiagnosticsLogger<DbLoggerCategory.Query> _logger;
        private readonly ISet<string> _tags;
        private readonly bool _detailedErrorsEnabled;
        private readonly bool _useRelationalNulls;

        public RelationalShapedQueryCompilingExpressionVisitor(
            [NotNull] ShapedQueryCompilingExpressionVisitorDependencies dependencies,
            [NotNull] RelationalShapedQueryCompilingExpressionVisitorDependencies relationalDependencies,
            [NotNull] QueryCompilationContext queryCompilationContext)
            : base(dependencies, queryCompilationContext)
        {
            Check.NotNull(relationalDependencies, nameof(relationalDependencies));

            RelationalDependencies = relationalDependencies;

            _contextType = queryCompilationContext.ContextType;
            _logger = queryCompilationContext.Logger;
            _tags = queryCompilationContext.Tags;
            _detailedErrorsEnabled = relationalDependencies.CoreSingletonOptions.AreDetailedErrorsEnabled;
            _useRelationalNulls = RelationalOptionsExtension.Extract(queryCompilationContext.ContextOptions).UseRelationalNulls;
        }

        protected virtual RelationalShapedQueryCompilingExpressionVisitorDependencies RelationalDependencies { get; }

        protected override Expression VisitShapedQuery(ShapedQueryExpression shapedQueryExpression)
        {
            Check.NotNull(shapedQueryExpression, nameof(shapedQueryExpression));

            var selectExpression = (SelectExpression)shapedQueryExpression.QueryExpression;
            selectExpression.ApplyTags(_tags);

            var dataReaderParameter = Expression.Parameter(typeof(DbDataReader), "dataReader");
            var resultCoordinatorParameter = Expression.Parameter(typeof(ResultCoordinator), "resultCoordinator");
            var indexMapParameter = Expression.Parameter(typeof(int[]), "indexMap");

            var shaper = new ShaperExpressionProcessingExpressionVisitor(
                    selectExpression,
                    dataReaderParameter,
                    resultCoordinatorParameter,
                    indexMapParameter)
                .Inject(shapedQueryExpression.ShaperExpression);

            shaper = InjectEntityMaterializers(shaper);

            var isNonComposedFromSql = selectExpression.IsNonComposedFromSql();
            shaper = new RelationalProjectionBindingRemovingExpressionVisitor(
                    selectExpression,
                    dataReaderParameter,
                    isNonComposedFromSql ? indexMapParameter : null,
                    _detailedErrorsEnabled,
                    QueryCompilationContext.IsBuffering)
                .Visit(shaper, out var projectionColumns);

            shaper = new CustomShaperCompilingExpressionVisitor(dataReaderParameter, resultCoordinatorParameter, QueryCompilationContext.IsTracking)
                .Visit(shaper);

            IReadOnlyList<string> columnNames = null;
            if (isNonComposedFromSql)
            {
                columnNames = selectExpression.Projection.Select(pe => ((ColumnExpression)pe.Expression).Name).ToList();
            }

            var relationalCommandCache = new RelationalCommandCache(
                Dependencies.MemoryCache,
                RelationalDependencies.SqlExpressionFactory,
                RelationalDependencies.QuerySqlGeneratorFactory,
                RelationalDependencies.RelationalParameterBasedQueryTranslationPostprocessorFactory,
                _useRelationalNulls,
                selectExpression);

            var shaperLambda = (LambdaExpression)shaper;

            return Expression.New(
                typeof(QueryingEnumerable<>).MakeGenericType(shaperLambda.ReturnType).GetConstructors()[0],
                Expression.Convert(QueryCompilationContext.QueryContextParameter, typeof(RelationalQueryContext)),
                Expression.Constant(relationalCommandCache),
                Expression.Constant(columnNames, typeof(IReadOnlyList<string>)),
                Expression.Constant(projectionColumns, typeof(IReadOnlyList<ReaderColumn>)),
                Expression.Constant(shaperLambda.Compile()),
                Expression.Constant(_contextType),
                Expression.Constant(_logger));
        }
    }
}
