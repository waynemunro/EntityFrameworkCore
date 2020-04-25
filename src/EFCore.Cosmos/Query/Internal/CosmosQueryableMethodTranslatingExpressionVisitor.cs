﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.Cosmos.Query.Internal
{
    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    public class CosmosQueryableMethodTranslatingExpressionVisitor : QueryableMethodTranslatingExpressionVisitor
    {
        private readonly IModel _model;
        private readonly ISqlExpressionFactory _sqlExpressionFactory;
        private readonly CosmosSqlTranslatingExpressionVisitor _sqlTranslator;
        private readonly CosmosProjectionBindingExpressionVisitor _projectionBindingExpressionVisitor;

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public CosmosQueryableMethodTranslatingExpressionVisitor(
            [NotNull] QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
            [NotNull] QueryCompilationContext queryCompilationContext,
            [NotNull] ISqlExpressionFactory sqlExpressionFactory,
            [NotNull] IMemberTranslatorProvider memberTranslatorProvider,
            [NotNull] IMethodCallTranslatorProvider methodCallTranslatorProvider)
            : base(dependencies, queryCompilationContext, subquery: false)
        {
            _model = queryCompilationContext.Model;
            _sqlExpressionFactory = sqlExpressionFactory;
            _sqlTranslator = new CosmosSqlTranslatingExpressionVisitor(
                queryCompilationContext,
                sqlExpressionFactory,
                memberTranslatorProvider,
                methodCallTranslatorProvider);
            _projectionBindingExpressionVisitor = new CosmosProjectionBindingExpressionVisitor(_model, _sqlTranslator);
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected CosmosQueryableMethodTranslatingExpressionVisitor(
            [NotNull] CosmosQueryableMethodTranslatingExpressionVisitor parentVisitor)
            : base(parentVisitor.Dependencies, parentVisitor.QueryCompilationContext, subquery: true)
        {
            _model = parentVisitor._model;
            _sqlExpressionFactory = parentVisitor._sqlExpressionFactory;
            _sqlTranslator = parentVisitor._sqlTranslator;
            _projectionBindingExpressionVisitor = new CosmosProjectionBindingExpressionVisitor(_model, _sqlTranslator);
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public override Expression Visit(Expression expression)
        {
            if (expression is MethodCallExpression methodCallExpression
                && methodCallExpression.Method.IsGenericMethod
                && methodCallExpression.Method.GetGenericMethodDefinition() == QueryableMethods.FirstOrDefaultWithoutPredicate)
            {
                if (methodCallExpression.Arguments[0] is MethodCallExpression queryRootMethodCallExpression
                    && methodCallExpression.Method.IsGenericMethod
                    && queryRootMethodCallExpression.Method.GetGenericMethodDefinition() == QueryableMethods.Where)
                {
                    if (queryRootMethodCallExpression.Arguments[0] is QueryRootExpression queryRootExpression)
                    {
                        var entityType = queryRootExpression.EntityType;

                        if (queryRootMethodCallExpression.Arguments[1] is UnaryExpression unaryExpression
                            && unaryExpression.Operand is LambdaExpression lambdaExpression
                            && lambdaExpression.Body is BinaryExpression lambdaBodyBinaryExpression)
                        {
                            var queryProperties = new List<IProperty>();
                            var parameterNames = new List<string>();

                            if (ProcessJoinCondition(lambdaBodyBinaryExpression, queryProperties, parameterNames))
                            {
                                var entityTypePrimaryKeyProperties = entityType.FindPrimaryKey().Properties;
                                var idProperty = entityType.GetProperties()
                                    .First(p => p.GetJsonPropertyName() == StoreKeyConvention.IdPropertyName);

                                if (TryGetPartitionKeyProperty(out var partitionKeyProperty)
                                    && entityTypePrimaryKeyProperties.SequenceEqual(queryProperties)
                                    && (partitionKeyProperty == null
                                        || entityTypePrimaryKeyProperties.Contains(partitionKeyProperty))
                                    && (idProperty.GetValueGeneratorFactory() != null
                                        || entityTypePrimaryKeyProperties.Contains(idProperty)))
                                {
                                    var propertyParameterList = queryProperties.Zip(parameterNames,
                                        (property, parameter) => (property, parameter))
                                        .ToDictionary(tuple => tuple.property, tuple => tuple.parameter);

                                    var readItemExpression = new ReadItemExpression(entityType, propertyParameterList);

                                    var shapedQueryExpression = new ShapedQueryExpression(
                                        readItemExpression,
                                        new EntityShaperExpression(
                                            entityType,
                                            new ProjectionBindingExpression(
                                                readItemExpression,
                                                new ProjectionMember(),
                                                typeof(ValueBuffer)),
                                            false));

                                    shapedQueryExpression = shapedQueryExpression.UpdateResultCardinality(ResultCardinality.Single);

                                    return shapedQueryExpression;
                                }
                            }

                            bool ProcessJoinCondition(
                                Expression joinCondition, ICollection<IProperty> properties, ICollection<string> paramNames)
                            {
                                if (joinCondition is BinaryExpression binaryExpression)
                                {
                                    switch (binaryExpression.NodeType)
                                    {
                                        case ExpressionType.AndAlso:
                                            return ProcessJoinCondition(binaryExpression.Left, properties, paramNames)
                                                && ProcessJoinCondition(binaryExpression.Right, properties, paramNames);

                                        case ExpressionType.Equal:
                                            if (binaryExpression.Left is MethodCallExpression methodCallExpr
                                                && binaryExpression.Right is ParameterExpression parameterExpr)
                                            {
                                                if (methodCallExpr.TryGetEFPropertyArguments(out _, out var propertyName))
                                                {
                                                    var property = entityType.FindProperty(propertyName);
                                                    if (property == null)
                                                    {
                                                        return false;
                                                    }
                                                    properties.Add(property);
                                                    paramNames.Add(parameterExpr.Name);
                                                    return true;
                                                }
                                            }
                                            return false;

                                        default:
                                            return false;
                                    }
                                }
                                return false;
                            }

                            bool TryGetPartitionKeyProperty(out IProperty partitionKeyProperty)
                            {
                                var partitionKeyPropertyName = entityType.GetPartitionKeyPropertyName();
                                if (partitionKeyPropertyName is null)
                                {
                                    partitionKeyProperty = null;
                                    return true;
                                }

                                partitionKeyProperty = entityType.FindProperty(partitionKeyPropertyName);
                                return true;
                            }
                        }
                    }
                }
            }
            return base.Visit(expression);
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override QueryableMethodTranslatingExpressionVisitor CreateSubqueryVisitor()
            => new CosmosQueryableMethodTranslatingExpressionVisitor(this);

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public override ShapedQueryExpression TranslateSubquery(Expression expression)
        {
            Check.NotNull(expression, nameof(expression));

            throw new InvalidOperationException(CoreStrings.TranslationFailed(expression.Print()));
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        [Obsolete("Use overload which takes IEntityType.")]
        protected override ShapedQueryExpression CreateShapedQueryExpression(Type elementType)
        {
            Check.NotNull(elementType, nameof(elementType));

            var entityType = _model.FindEntityType(elementType);
            var selectExpression = _sqlExpressionFactory.Select(entityType);

            return new ShapedQueryExpression(
                selectExpression,
                new EntityShaperExpression(
                    entityType,
                    new ProjectionBindingExpression(
                        selectExpression,
                        new ProjectionMember(),
                        typeof(ValueBuffer)),
                    false));
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression CreateShapedQueryExpression(IEntityType entityType)
        {
            Check.NotNull(entityType, nameof(entityType));

            var selectExpression = _sqlExpressionFactory.Select(entityType);

            return new ShapedQueryExpression(
                selectExpression,
                new EntityShaperExpression(
                    entityType,
                    new ProjectionBindingExpression(
                        selectExpression,
                        new ProjectionMember(),
                        typeof(ValueBuffer)),
                    false));
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateAll(ShapedQueryExpression source, LambdaExpression predicate)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(predicate, nameof(predicate));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateAny(ShapedQueryExpression source, LambdaExpression predicate)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(predicate, nameof(predicate));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateAverage(ShapedQueryExpression source, LambdaExpression selector, Type resultType)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(resultType, nameof(resultType));

            var selectExpression = (SelectExpression)source.QueryExpression;
            if (selectExpression.IsDistinct
                || selectExpression.Limit != null
                || selectExpression.Offset != null)
            {
                return null;
            }

            if (selector != null)
            {
                source = TranslateSelect(source, selector);
            }

            var projection = (SqlExpression)selectExpression.GetMappedProjection(new ProjectionMember());
            projection = _sqlExpressionFactory.Function(
                "AVG", new[] { projection }, projection.Type, projection.TypeMapping);

            return AggregateResultShaper(source, projection, throwOnNullResult: true, resultType);
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateCast(ShapedQueryExpression source, Type resultType)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(resultType, nameof(resultType));

            return source.ShaperExpression.Type != resultType
                ? source.UpdateShaperExpression(Expression.Convert(source.ShaperExpression, resultType))
                : source;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateConcat(ShapedQueryExpression source1, ShapedQueryExpression source2)
        {
            Check.NotNull(source1, nameof(source1));
            Check.NotNull(source2, nameof(source2));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateContains(ShapedQueryExpression source, Expression item)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(item, nameof(item));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateCount(ShapedQueryExpression source, LambdaExpression predicate)
        {
            Check.NotNull(source, nameof(source));

            var selectExpression = (SelectExpression)source.QueryExpression;
            if (selectExpression.IsDistinct
                || selectExpression.Limit != null
                || selectExpression.Offset != null)
            {
                return null;
            }

            if (predicate != null)
            {
                source = TranslateWhere(source, predicate);
                if (source == null)
                {
                    return null;
                }
            }

            var translation = _sqlExpressionFactory.ApplyDefaultTypeMapping(
                _sqlExpressionFactory.Function("COUNT", new[] { _sqlExpressionFactory.Constant(1) }, typeof(int)));

            var projectionMapping = new Dictionary<ProjectionMember, Expression> { { new ProjectionMember(), translation } };

            selectExpression.ClearOrdering();
            selectExpression.ReplaceProjectionMapping(projectionMapping);
            return source.UpdateShaperExpression(new ProjectionBindingExpression(source.QueryExpression, new ProjectionMember(), typeof(int)));
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateDefaultIfEmpty(ShapedQueryExpression source, Expression defaultValue)
        {
            Check.NotNull(source, nameof(source));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateDistinct(ShapedQueryExpression source)
        {
            Check.NotNull(source, nameof(source));

            ((SelectExpression)source.QueryExpression).ApplyDistinct();

            return source;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateElementAtOrDefault(
            ShapedQueryExpression source, Expression index, bool returnDefault)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(index, nameof(index));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateExcept(ShapedQueryExpression source1, ShapedQueryExpression source2)
        {
            Check.NotNull(source1, nameof(source1));
            Check.NotNull(source2, nameof(source2));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateFirstOrDefault(
            ShapedQueryExpression source, LambdaExpression predicate, Type returnType, bool returnDefault)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(returnType, nameof(returnType));

            if (predicate != null)
            {
                source = TranslateWhere(source, predicate);
                if (source == null)
                {
                    return null;
                }
            }

            var selectExpression = (SelectExpression)source.QueryExpression;
            selectExpression.ApplyLimit(TranslateExpression(Expression.Constant(1)));

            return source.ShaperExpression.Type != returnType
                ? source.UpdateShaperExpression(Expression.Convert(source.ShaperExpression, returnType))
                : source;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateGroupBy(
            ShapedQueryExpression source,
            LambdaExpression keySelector,
            LambdaExpression elementSelector,
            LambdaExpression resultSelector)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(keySelector, nameof(keySelector));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateGroupJoin(
            ShapedQueryExpression outer,
            ShapedQueryExpression inner,
            LambdaExpression outerKeySelector,
            LambdaExpression innerKeySelector,
            LambdaExpression resultSelector)
        {
            Check.NotNull(outer, nameof(outer));
            Check.NotNull(inner, nameof(inner));
            Check.NotNull(outerKeySelector, nameof(outerKeySelector));
            Check.NotNull(innerKeySelector, nameof(innerKeySelector));
            Check.NotNull(resultSelector, nameof(resultSelector));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateIntersect(ShapedQueryExpression source1, ShapedQueryExpression source2)
        {
            Check.NotNull(source1, nameof(source1));
            Check.NotNull(source2, nameof(source2));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateJoin(
            ShapedQueryExpression outer,
            ShapedQueryExpression inner,
            LambdaExpression outerKeySelector,
            LambdaExpression innerKeySelector,
            LambdaExpression resultSelector)
        {
            Check.NotNull(outer, nameof(outer));
            Check.NotNull(inner, nameof(inner));
            Check.NotNull(resultSelector, nameof(resultSelector));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateLastOrDefault(
            ShapedQueryExpression source, LambdaExpression predicate, Type returnType, bool returnDefault)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(returnType, nameof(returnType));

            if (predicate != null)
            {
                source = TranslateWhere(source, predicate);
                if (source == null)
                {
                    return null;
                }
            }

            var selectExpression = (SelectExpression)source.QueryExpression;
            selectExpression.ReverseOrderings();
            selectExpression.ApplyLimit(TranslateExpression(Expression.Constant(1)));

            return source.ShaperExpression.Type != returnType
                ? source.UpdateShaperExpression(Expression.Convert(source.ShaperExpression, returnType))
                : source;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateLeftJoin(
            ShapedQueryExpression outer,
            ShapedQueryExpression inner,
            LambdaExpression outerKeySelector,
            LambdaExpression innerKeySelector,
            LambdaExpression resultSelector)
        {
            Check.NotNull(outer, nameof(outer));
            Check.NotNull(inner, nameof(inner));
            Check.NotNull(resultSelector, nameof(resultSelector));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateLongCount(ShapedQueryExpression source, LambdaExpression predicate)
        {
            Check.NotNull(source, nameof(source));

            var selectExpression = (SelectExpression)source.QueryExpression;
            if (selectExpression.IsDistinct
                || selectExpression.Limit != null
                || selectExpression.Offset != null)
            {
                return null;
            }

            if (predicate != null)
            {
                source = TranslateWhere(source, predicate);
                if (source == null)
                {
                    return null;
                }
            }

            var translation = _sqlExpressionFactory.ApplyDefaultTypeMapping(
                _sqlExpressionFactory.Function("COUNT", new[] { _sqlExpressionFactory.Constant(1) }, typeof(long)));
            var projectionMapping = new Dictionary<ProjectionMember, Expression> { { new ProjectionMember(), translation } };

            selectExpression.ClearOrdering();
            selectExpression.ReplaceProjectionMapping(projectionMapping);
            return source.UpdateShaperExpression(new ProjectionBindingExpression(source.QueryExpression, new ProjectionMember(), typeof(long)));
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateMax(ShapedQueryExpression source, LambdaExpression selector, Type resultType)
        {
            Check.NotNull(source, nameof(source));

            var selectExpression = (SelectExpression)source.QueryExpression;
            if (selectExpression.IsDistinct
                || selectExpression.Limit != null
                || selectExpression.Offset != null)
            {
                return null;
            }

            if (selector != null)
            {
                source = TranslateSelect(source, selector);
            }

            var projection = (SqlExpression)selectExpression.GetMappedProjection(new ProjectionMember());

            projection = _sqlExpressionFactory.Function("MAX", new[] { projection }, resultType, projection.TypeMapping);

            return AggregateResultShaper(source, projection, throwOnNullResult: true, resultType);
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateMin(ShapedQueryExpression source, LambdaExpression selector, Type resultType)
        {
            Check.NotNull(source, nameof(source));

            var selectExpression = (SelectExpression)source.QueryExpression;
            if (selectExpression.IsDistinct
                || selectExpression.Limit != null
                || selectExpression.Offset != null)
            {
                return null;
            }

            if (selector != null)
            {
                source = TranslateSelect(source, selector);
            }

            var projection = (SqlExpression)selectExpression.GetMappedProjection(new ProjectionMember());

            projection = _sqlExpressionFactory.Function("MIN", new[] { projection }, resultType, projection.TypeMapping);

            return AggregateResultShaper(source, projection, throwOnNullResult: true, resultType);
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateOfType(ShapedQueryExpression source, Type resultType)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(resultType, nameof(resultType));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateOrderBy(
            ShapedQueryExpression source, LambdaExpression keySelector, bool ascending)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(keySelector, nameof(keySelector));

            var translation = TranslateLambdaExpression(source, keySelector);
            if (translation != null)
            {
                ((SelectExpression)source.QueryExpression).ApplyOrdering(new OrderingExpression(translation, ascending));

                return source;
            }

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateReverse(ShapedQueryExpression source)
        {
            Check.NotNull(source, nameof(source));

            var selectExpression = (SelectExpression)source.QueryExpression;
            if (selectExpression.Orderings.Count == 0)
            {
                return null;
            }

            selectExpression.ReverseOrderings();

            return source;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateSelect(ShapedQueryExpression source, LambdaExpression selector)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(selector, nameof(selector));

            if (selector.Body == selector.Parameters[0])
            {
                return source;
            }

            var selectExpression = (SelectExpression)source.QueryExpression;
            if (selectExpression.IsDistinct)
            {
                return null;
            }

            var newSelectorBody = ReplacingExpressionVisitor.Replace(selector.Parameters.Single(), source.ShaperExpression, selector.Body);

            return source.UpdateShaperExpression(_projectionBindingExpressionVisitor.Translate(selectExpression, newSelectorBody));
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateSelectMany(
            ShapedQueryExpression source, LambdaExpression collectionSelector, LambdaExpression resultSelector)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(collectionSelector, nameof(collectionSelector));
            Check.NotNull(resultSelector, nameof(resultSelector));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateSelectMany(ShapedQueryExpression source, LambdaExpression selector)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(selector, nameof(selector));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateSingleOrDefault(
            ShapedQueryExpression source, LambdaExpression predicate, Type returnType, bool returnDefault)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(returnType, nameof(returnType));

            if (predicate != null)
            {
                source = TranslateWhere(source, predicate);
                if (source == null)
                {
                    return null;
                }
            }

            var selectExpression = (SelectExpression)source.QueryExpression;
            selectExpression.ApplyLimit(TranslateExpression(Expression.Constant(2)));

            return source.ShaperExpression.Type != returnType
                ? source.UpdateShaperExpression(Expression.Convert(source.ShaperExpression, returnType))
                : source;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateSkip(ShapedQueryExpression source, Expression count)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(count, nameof(count));

            var selectExpression = (SelectExpression)source.QueryExpression;
            var translation = TranslateExpression(count);

            if (translation != null)
            {
                selectExpression.ApplyOffset(translation);

                return source;
            }

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateSkipWhile(ShapedQueryExpression source, LambdaExpression predicate)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(predicate, nameof(predicate));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateSum(ShapedQueryExpression source, LambdaExpression selector, Type resultType)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(resultType, nameof(resultType));

            var selectExpression = (SelectExpression)source.QueryExpression;
            if (selectExpression.IsDistinct
                || selectExpression.Limit != null
                || selectExpression.Offset != null)
            {
                return null;
            }

            if (selector != null)
            {
                source = TranslateSelect(source, selector);
            }

            var serverOutputType = resultType.UnwrapNullableType();
            var projection = (SqlExpression)selectExpression.GetMappedProjection(new ProjectionMember());

            projection = _sqlExpressionFactory.Function(
                "SUM", new[] { projection }, serverOutputType, projection.TypeMapping);

            return AggregateResultShaper(source, projection, throwOnNullResult: false, resultType);
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateTake(ShapedQueryExpression source, Expression count)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(count, nameof(count));

            var selectExpression = (SelectExpression)source.QueryExpression;
            var translation = TranslateExpression(count);

            if (translation != null)
            {
                selectExpression.ApplyLimit(translation);

                return source;
            }

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateTakeWhile(ShapedQueryExpression source, LambdaExpression predicate)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(predicate, nameof(predicate));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateThenBy(ShapedQueryExpression source, LambdaExpression keySelector, bool ascending)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(keySelector, nameof(keySelector));

            var translation = TranslateLambdaExpression(source, keySelector);
            if (translation != null)
            {
                ((SelectExpression)source.QueryExpression).AppendOrdering(new OrderingExpression(translation, ascending));

                return source;
            }

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateUnion(ShapedQueryExpression source1, ShapedQueryExpression source2)
        {
            Check.NotNull(source1, nameof(source1));
            Check.NotNull(source2, nameof(source2));

            return null;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected override ShapedQueryExpression TranslateWhere(ShapedQueryExpression source, LambdaExpression predicate)
        {
            Check.NotNull(source, nameof(source));
            Check.NotNull(predicate, nameof(predicate));

            var translation = TranslateLambdaExpression(source, predicate);
            if (translation != null)
            {
                ((SelectExpression)source.QueryExpression).ApplyPredicate(translation);

                return source;
            }

            return null;
        }

        private SqlExpression TranslateExpression(Expression expression)
            => _sqlTranslator.Translate(expression);

        private SqlExpression TranslateLambdaExpression(
            ShapedQueryExpression shapedQueryExpression, LambdaExpression lambdaExpression)
        {
            var lambdaBody = RemapLambdaBody(shapedQueryExpression.ShaperExpression, lambdaExpression);

            return TranslateExpression(lambdaBody);
        }

        private static Expression RemapLambdaBody(Expression shaperBody, LambdaExpression lambdaExpression)
        {
            return ReplacingExpressionVisitor.Replace(lambdaExpression.Parameters.Single(), shaperBody, lambdaExpression.Body);
        }

        private ShapedQueryExpression AggregateResultShaper(
            ShapedQueryExpression source, Expression projection, bool throwOnNullResult, Type resultType)
        {
            var selectExpression = (SelectExpression)source.QueryExpression;
            selectExpression.ReplaceProjectionMapping(
                new Dictionary<ProjectionMember, Expression> { { new ProjectionMember(), projection } });

            selectExpression.ClearOrdering();

            var nullableResultType = resultType.MakeNullable();
            Expression shaper = new ProjectionBindingExpression(
                source.QueryExpression, new ProjectionMember(), throwOnNullResult ? nullableResultType : projection.Type);

            if (throwOnNullResult)
            {
                var resultVariable = Expression.Variable(nullableResultType, "result");
                var returnValueForNull = resultType.IsNullableType()
                    ? (Expression)Expression.Constant(null, resultType)
                    : Expression.Throw(
                        Expression.New(
                            typeof(InvalidOperationException).GetConstructors()
                                .Single(ci => ci.GetParameters().Length == 1),
                            Expression.Constant(CoreStrings.NoElements)),
                        resultType);

                shaper = Expression.Block(
                    new[] { resultVariable },
                    Expression.Assign(resultVariable, shaper),
                    Expression.Condition(
                        Expression.Equal(resultVariable, Expression.Default(nullableResultType)),
                        returnValueForNull,
                        resultType != resultVariable.Type
                            ? Expression.Convert(resultVariable, resultType)
                            : (Expression)resultVariable));
            }
            else if (resultType != shaper.Type)
            {
                shaper = Expression.Convert(shaper, resultType);
            }

            return source.UpdateShaperExpression(shaper);
        }
    }
}
