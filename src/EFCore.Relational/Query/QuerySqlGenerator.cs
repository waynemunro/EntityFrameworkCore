// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.Query
{
    public class QuerySqlGenerator : SqlExpressionVisitor
    {
        private static readonly Regex _composableSql
            = new Regex(@"^\s*?SELECT\b", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(value: 1000.0));

        private readonly IRelationalCommandBuilderFactory _relationalCommandBuilderFactory;
        private readonly ISqlGenerationHelper _sqlGenerationHelper;
        private IRelationalCommandBuilder _relationalCommandBuilder;

        private static readonly Dictionary<ExpressionType, string> _operatorMap = new Dictionary<ExpressionType, string>
        {
            { ExpressionType.Equal, " = " },
            { ExpressionType.NotEqual, " <> " },
            { ExpressionType.GreaterThan, " > " },
            { ExpressionType.GreaterThanOrEqual, " >= " },
            { ExpressionType.LessThan, " < " },
            { ExpressionType.LessThanOrEqual, " <= " },
            { ExpressionType.AndAlso, " AND " },
            { ExpressionType.OrElse, " OR " },
            { ExpressionType.Add, " + " },
            { ExpressionType.Subtract, " - " },
            { ExpressionType.Multiply, " * " },
            { ExpressionType.Divide, " / " },
            { ExpressionType.Modulo, " % " },
            { ExpressionType.And, " & " },
            { ExpressionType.Or, " | " }
        };

        public QuerySqlGenerator([NotNull] QuerySqlGeneratorDependencies dependencies)
        {
            Check.NotNull(dependencies, nameof(dependencies));

            Dependencies = dependencies;

            _relationalCommandBuilderFactory = dependencies.RelationalCommandBuilderFactory;
            _sqlGenerationHelper = dependencies.SqlGenerationHelper;
        }

        protected virtual QuerySqlGeneratorDependencies Dependencies { get; }

        public virtual IRelationalCommand GetCommand([NotNull] SelectExpression selectExpression)
        {
            Check.NotNull(selectExpression, nameof(selectExpression));

            _relationalCommandBuilder = _relationalCommandBuilderFactory.Create();

            GenerateTagsHeaderComment(selectExpression);

            if (selectExpression.IsNonComposedFromSql())
            {
                GenerateFromSql((FromSqlExpression)selectExpression.Tables[0]);
            }
            else
            {
                VisitSelect(selectExpression);
            }

            return _relationalCommandBuilder.Build();
        }

        /// <summary>
        ///     The default alias separator.
        /// </summary>
        protected virtual string AliasSeparator { get; } = " AS ";

        protected virtual IRelationalCommandBuilder Sql => _relationalCommandBuilder;

        protected virtual void GenerateTagsHeaderComment([NotNull] SelectExpression selectExpression)
        {
            Check.NotNull(selectExpression, nameof(selectExpression));

            if (selectExpression.Tags.Count > 0)
            {
                foreach (var tag in selectExpression.Tags)
                {
                    _relationalCommandBuilder
                        .AppendLines(_sqlGenerationHelper.GenerateComment(tag))
                        .AppendLine();
                }
            }
        }

        protected override Expression VisitSqlFragment(SqlFragmentExpression sqlFragmentExpression)
        {
            Check.NotNull(sqlFragmentExpression, nameof(sqlFragmentExpression));

            _relationalCommandBuilder.Append(sqlFragmentExpression.Sql);

            return sqlFragmentExpression;
        }

        private bool IsNonComposedSetOperation(SelectExpression selectExpression)
            => selectExpression.Offset == null
                && selectExpression.Limit == null
                && !selectExpression.IsDistinct
                && selectExpression.Predicate == null
                && selectExpression.Having == null
                && selectExpression.Orderings.Count == 0
                && selectExpression.GroupBy.Count == 0
                && selectExpression.Tables.Count == 1
                && selectExpression.Tables[0] is SetOperationBase setOperation
                && selectExpression.Projection.Count == setOperation.Source1.Projection.Count
                && selectExpression.Projection.Select(
                        (pe, index) => pe.Expression is ColumnExpression column
                            && string.Equals(column.Table.Alias, setOperation.Alias, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(
                                column.Name, setOperation.Source1.Projection[index].Alias, StringComparison.OrdinalIgnoreCase))
                    .All(e => e);

        protected override Expression VisitSelect(SelectExpression selectExpression)
        {
            Check.NotNull(selectExpression, nameof(selectExpression));

            if (IsNonComposedSetOperation(selectExpression))
            {
                // Naked set operation
                GenerateSetOperation((SetOperationBase)selectExpression.Tables[0]);

                return selectExpression;
            }

            IDisposable subQueryIndent = null;

            if (selectExpression.Alias != null)
            {
                _relationalCommandBuilder.AppendLine("(");
                subQueryIndent = _relationalCommandBuilder.Indent();
            }

            _relationalCommandBuilder.Append("SELECT ");

            if (selectExpression.IsDistinct)
            {
                _relationalCommandBuilder.Append("DISTINCT ");
            }

            GenerateTop(selectExpression);

            if (selectExpression.Projection.Any())
            {
                GenerateList(selectExpression.Projection, e => Visit(e));
            }
            else
            {
                _relationalCommandBuilder.Append("1");
            }

            if (selectExpression.Tables.Any())
            {
                _relationalCommandBuilder.AppendLine().Append("FROM ");

                GenerateList(selectExpression.Tables, e => Visit(e), sql => sql.AppendLine());
            }
            else
            {
                GeneratePseudoFromClause();
            }

            if (selectExpression.Predicate != null)
            {
                _relationalCommandBuilder.AppendLine().Append("WHERE ");

                Visit(selectExpression.Predicate);
            }

            if (selectExpression.GroupBy.Count > 0)
            {
                _relationalCommandBuilder.AppendLine().Append("GROUP BY ");

                GenerateList(selectExpression.GroupBy, e => Visit(e));
            }

            if (selectExpression.Having != null)
            {
                _relationalCommandBuilder.AppendLine().Append("HAVING ");

                Visit(selectExpression.Having);
            }

            GenerateOrderings(selectExpression);
            GenerateLimitOffset(selectExpression);

            if (selectExpression.Alias != null)
            {
                subQueryIndent.Dispose();

                _relationalCommandBuilder.AppendLine()
                    .Append(")" + AliasSeparator + _sqlGenerationHelper.DelimitIdentifier(selectExpression.Alias));
            }

            return selectExpression;
        }

        /// <summary>
        ///     Generates a pseudo FROM clause. Required by some providers
        ///     when a query has no actual FROM clause.
        /// </summary>
        protected virtual void GeneratePseudoFromClause()
        {
        }

        protected override Expression VisitProjection(ProjectionExpression projectionExpression)
        {
            Check.NotNull(projectionExpression, nameof(projectionExpression));

            Visit(projectionExpression.Expression);

            if (!string.Equals(string.Empty, projectionExpression.Alias)
                && !(projectionExpression.Expression is ColumnExpression column
                    && string.Equals(column.Name, projectionExpression.Alias)))
            {
                _relationalCommandBuilder.Append(AliasSeparator + _sqlGenerationHelper.DelimitIdentifier(projectionExpression.Alias));
            }

            return projectionExpression;
        }

        protected override Expression VisitSqlFunction(SqlFunctionExpression sqlFunctionExpression)
        {
            Check.NotNull(sqlFunctionExpression, nameof(sqlFunctionExpression));

            if (sqlFunctionExpression.IsBuiltIn)
            {
                if (sqlFunctionExpression.Instance != null)
                {
                    Visit(sqlFunctionExpression.Instance);
                    _relationalCommandBuilder.Append(".");
                }

                _relationalCommandBuilder.Append(sqlFunctionExpression.Name);
            }
            else
            {
                if (!string.IsNullOrEmpty(sqlFunctionExpression.Schema))
                {
                    _relationalCommandBuilder
                        .Append(_sqlGenerationHelper.DelimitIdentifier(sqlFunctionExpression.Schema))
                        .Append(".");
                }

                _relationalCommandBuilder
                    .Append(_sqlGenerationHelper.DelimitIdentifier(sqlFunctionExpression.Name));
            }

            if (!sqlFunctionExpression.IsNiladic)
            {
                _relationalCommandBuilder.Append("(");
                GenerateList(sqlFunctionExpression.Arguments, e => Visit(e));
                _relationalCommandBuilder.Append(")");
            }

            return sqlFunctionExpression;
        }

        protected override Expression VisitQueryableFunction(QueryableFunctionExpression queryableFunctionExpression)
        {
            Check.NotNull(queryableFunctionExpression, nameof(queryableFunctionExpression));

            if (!string.IsNullOrEmpty(queryableFunctionExpression.Schema))
            {
                _relationalCommandBuilder
                    .Append(_sqlGenerationHelper.DelimitIdentifier(queryableFunctionExpression.Schema))
                    .Append(".");
            }

            _relationalCommandBuilder
                .Append(_sqlGenerationHelper.DelimitIdentifier(queryableFunctionExpression.Name))
                .Append("(");

            GenerateList(queryableFunctionExpression.Arguments, e => Visit(e));

            _relationalCommandBuilder
                .Append(")")
                .Append(AliasSeparator)
                .Append(_sqlGenerationHelper.DelimitIdentifier(queryableFunctionExpression.Alias));

            return queryableFunctionExpression;
        }

        protected override Expression VisitColumn(ColumnExpression columnExpression)
        {
            Check.NotNull(columnExpression, nameof(columnExpression));

            _relationalCommandBuilder
                .Append(_sqlGenerationHelper.DelimitIdentifier(columnExpression.Table.Alias))
                .Append(".")
                .Append(_sqlGenerationHelper.DelimitIdentifier(columnExpression.Name));

            return columnExpression;
        }

        protected override Expression VisitTable(TableExpression tableExpression)
        {
            Check.NotNull(tableExpression, nameof(tableExpression));

            _relationalCommandBuilder
                .Append(_sqlGenerationHelper.DelimitIdentifier(tableExpression.Name, tableExpression.Schema))
                .Append(AliasSeparator)
                .Append(_sqlGenerationHelper.DelimitIdentifier(tableExpression.Alias));

            return tableExpression;
        }

        private void GenerateFromSql(FromSqlExpression fromSqlExpression)
        {
            var sql = fromSqlExpression.Sql;
            string[] substitutions = null;

            switch (fromSqlExpression.Arguments)
            {
                case ConstantExpression constantExpression
                    when constantExpression.Value is CompositeRelationalParameter compositeRelationalParameter:
                {
                    var subParameters = compositeRelationalParameter.RelationalParameters;
                    substitutions = new string[subParameters.Count];
                    for (var i = 0; i < subParameters.Count; i++)
                    {
                        substitutions[i] = _sqlGenerationHelper.GenerateParameterNamePlaceholder(subParameters[i].InvariantName);
                    }

                    _relationalCommandBuilder.AddParameter(compositeRelationalParameter);

                    break;
                }

                case ConstantExpression constantExpression
                    when constantExpression.Value is object[] constantValues:
                {
                    substitutions = new string[constantValues.Length];
                    for (var i = 0; i < constantValues.Length; i++)
                    {
                        var value = constantValues[i];
                        if (value is RawRelationalParameter rawRelationalParameter)
                        {
                            substitutions[i] = _sqlGenerationHelper.GenerateParameterNamePlaceholder(rawRelationalParameter.InvariantName);
                            _relationalCommandBuilder.AddParameter(rawRelationalParameter);
                        }
                        else if (value is SqlConstantExpression sqlConstantExpression)
                        {
                            substitutions[i] = sqlConstantExpression.TypeMapping.GenerateSqlLiteral(sqlConstantExpression.Value);
                        }
                    }

                    break;
                }
            }

            if (substitutions != null)
            {
                // ReSharper disable once CoVariantArrayConversion
                // InvariantCulture not needed since substitutions are all strings
                sql = string.Format(sql, substitutions);
            }

            _relationalCommandBuilder.AppendLines(sql);
        }

        protected override Expression VisitFromSql(FromSqlExpression fromSqlExpression)
        {
            Check.NotNull(fromSqlExpression, nameof(fromSqlExpression));

            _relationalCommandBuilder.AppendLine("(");

            if (!_composableSql.IsMatch(fromSqlExpression.Sql))
            {
                throw new InvalidOperationException(RelationalStrings.FromSqlNonComposable);
            }

            using (_relationalCommandBuilder.Indent())
            {
                GenerateFromSql(fromSqlExpression);
            }

            _relationalCommandBuilder.Append(")")
                .Append(AliasSeparator)
                .Append(_sqlGenerationHelper.DelimitIdentifier(fromSqlExpression.Alias));

            return fromSqlExpression;
        }

        protected override Expression VisitSqlBinary(SqlBinaryExpression sqlBinaryExpression)
        {
            Check.NotNull(sqlBinaryExpression, nameof(sqlBinaryExpression));

            var requiresBrackets = RequiresBrackets(sqlBinaryExpression.Left);

            if (requiresBrackets)
            {
                _relationalCommandBuilder.Append("(");
            }

            Visit(sqlBinaryExpression.Left);

            if (requiresBrackets)
            {
                _relationalCommandBuilder.Append(")");
            }

            _relationalCommandBuilder.Append(GenerateOperator(sqlBinaryExpression));

            requiresBrackets = RequiresBrackets(sqlBinaryExpression.Right);

            if (requiresBrackets)
            {
                _relationalCommandBuilder.Append("(");
            }

            Visit(sqlBinaryExpression.Right);

            if (requiresBrackets)
            {
                _relationalCommandBuilder.Append(")");
            }

            return sqlBinaryExpression;
        }

        private static bool RequiresBrackets(SqlExpression expression)
            => expression is SqlBinaryExpression || expression is LikeExpression;

        protected override Expression VisitSqlConstant(SqlConstantExpression sqlConstantExpression)
        {
            Check.NotNull(sqlConstantExpression, nameof(sqlConstantExpression));

            _relationalCommandBuilder
                .Append(sqlConstantExpression.TypeMapping.GenerateSqlLiteral(sqlConstantExpression.Value));

            return sqlConstantExpression;
        }

        protected override Expression VisitSqlParameter(SqlParameterExpression sqlParameterExpression)
        {
            Check.NotNull(sqlParameterExpression, nameof(sqlParameterExpression));

            var parameterNameInCommand = _sqlGenerationHelper.GenerateParameterName(sqlParameterExpression.Name);

            if (_relationalCommandBuilder.Parameters
                .All(p => p.InvariantName != sqlParameterExpression.Name))
            {
                _relationalCommandBuilder.AddParameter(
                    sqlParameterExpression.Name,
                    parameterNameInCommand,
                    sqlParameterExpression.TypeMapping,
                    sqlParameterExpression.Type.IsNullableType());
            }

            _relationalCommandBuilder
                .Append(_sqlGenerationHelper.GenerateParameterNamePlaceholder(sqlParameterExpression.Name));

            return sqlParameterExpression;
        }

        protected override Expression VisitOrdering(OrderingExpression orderingExpression)
        {
            Check.NotNull(orderingExpression, nameof(orderingExpression));

            if (orderingExpression.Expression is SqlConstantExpression
                || orderingExpression.Expression is SqlParameterExpression)
            {
                _relationalCommandBuilder.Append("(SELECT 1)");
            }
            else
            {
                Visit(orderingExpression.Expression);
            }

            if (!orderingExpression.IsAscending)
            {
                _relationalCommandBuilder.Append(" DESC");
            }

            return orderingExpression;
        }

        protected override Expression VisitLike(LikeExpression likeExpression)
        {
            Check.NotNull(likeExpression, nameof(likeExpression));

            Visit(likeExpression.Match);
            _relationalCommandBuilder.Append(" LIKE ");
            Visit(likeExpression.Pattern);

            if (likeExpression.EscapeChar != null)
            {
                _relationalCommandBuilder.Append(" ESCAPE ");
                Visit(likeExpression.EscapeChar);
            }

            return likeExpression;
        }

        protected override Expression VisitCollate(CollateExpression collateExpresion)
        {
            Check.NotNull(collateExpresion, nameof(collateExpresion));

            Visit(collateExpresion.Operand);

            _relationalCommandBuilder
                .Append(" COLLATE ")
                .Append(collateExpresion.Collation);

            return collateExpresion;
        }

        protected override Expression VisitCase(CaseExpression caseExpression)
        {
            Check.NotNull(caseExpression, nameof(caseExpression));

            _relationalCommandBuilder.Append("CASE");

            if (caseExpression.Operand != null)
            {
                _relationalCommandBuilder.Append(" ");
                Visit(caseExpression.Operand);
            }

            using (_relationalCommandBuilder.Indent())
            {
                foreach (var whenClause in caseExpression.WhenClauses)
                {
                    _relationalCommandBuilder
                        .AppendLine()
                        .Append("WHEN ");
                    Visit(whenClause.Test);
                    _relationalCommandBuilder.Append(" THEN ");
                    Visit(whenClause.Result);
                }

                if (caseExpression.ElseResult != null)
                {
                    _relationalCommandBuilder
                        .AppendLine()
                        .Append("ELSE ");
                    Visit(caseExpression.ElseResult);
                }
            }

            _relationalCommandBuilder
                .AppendLine()
                .Append("END");

            return caseExpression;
        }

        protected override Expression VisitSqlUnary(SqlUnaryExpression sqlUnaryExpression)
        {
            Check.NotNull(sqlUnaryExpression, nameof(sqlUnaryExpression));

            switch (sqlUnaryExpression.OperatorType)
            {
                case ExpressionType.Convert:
                {
                    _relationalCommandBuilder.Append("CAST(");
                    var requiresBrackets = RequiresBrackets(sqlUnaryExpression.Operand);
                    if (requiresBrackets)
                    {
                        _relationalCommandBuilder.Append("(");
                    }

                    Visit(sqlUnaryExpression.Operand);
                    if (requiresBrackets)
                    {
                        _relationalCommandBuilder.Append(")");
                    }

                    _relationalCommandBuilder.Append(" AS ");
                    _relationalCommandBuilder.Append(sqlUnaryExpression.TypeMapping.StoreType);
                    _relationalCommandBuilder.Append(")");
                    break;
                }

                case ExpressionType.Not
                    when sqlUnaryExpression.IsLogicalNot():
                {
                    _relationalCommandBuilder.Append("NOT (");
                    Visit(sqlUnaryExpression.Operand);
                    _relationalCommandBuilder.Append(")");
                    break;
                }

                case ExpressionType.Not:
                {
                    _relationalCommandBuilder.Append("~");
                    Visit(sqlUnaryExpression.Operand);
                    break;
                }

                case ExpressionType.Equal:
                {
                    Visit(sqlUnaryExpression.Operand);
                    _relationalCommandBuilder.Append(" IS NULL");
                    break;
                }

                case ExpressionType.NotEqual:
                {
                    Visit(sqlUnaryExpression.Operand);
                    _relationalCommandBuilder.Append(" IS NOT NULL");
                    break;
                }

                case ExpressionType.Negate:
                {
                    _relationalCommandBuilder.Append("-");
                    Visit(sqlUnaryExpression.Operand);
                    break;
                }
            }

            return sqlUnaryExpression;
        }

        protected override Expression VisitExists(ExistsExpression existsExpression)
        {
            Check.NotNull(existsExpression, nameof(existsExpression));

            if (existsExpression.IsNegated)
            {
                _relationalCommandBuilder.Append("NOT ");
            }

            _relationalCommandBuilder.AppendLine("EXISTS (");

            using (_relationalCommandBuilder.Indent())
            {
                Visit(existsExpression.Subquery);
            }

            _relationalCommandBuilder.Append(")");

            return existsExpression;
        }

        protected override Expression VisitIn(InExpression inExpression)
        {
            Check.NotNull(inExpression, nameof(inExpression));

            if (inExpression.Values != null)
            {
                Visit(inExpression.Item);
                _relationalCommandBuilder.Append(inExpression.IsNegated ? " NOT IN " : " IN ");
                _relationalCommandBuilder.Append("(");
                var valuesConstant = (SqlConstantExpression)inExpression.Values;
                var valuesList = ((IEnumerable<object>)valuesConstant.Value)
                    .Select(v => new SqlConstantExpression(Expression.Constant(v), valuesConstant.TypeMapping)).ToList();
                GenerateList(valuesList, e => Visit(e));
                _relationalCommandBuilder.Append(")");
            }
            else
            {
                Visit(inExpression.Item);
                _relationalCommandBuilder.Append(inExpression.IsNegated ? " NOT IN " : " IN ");
                _relationalCommandBuilder.AppendLine("(");

                using (_relationalCommandBuilder.Indent())
                {
                    Visit(inExpression.Subquery);
                }

                _relationalCommandBuilder.AppendLine().Append(")");
            }

            return inExpression;
        }

        protected virtual string GenerateOperator([NotNull] SqlBinaryExpression binaryExpression)
        {
            Check.NotNull(binaryExpression, nameof(binaryExpression));

            return _operatorMap[binaryExpression.OperatorType];
        }

        protected virtual void GenerateTop([NotNull] SelectExpression selectExpression)
        {
            Check.NotNull(selectExpression, nameof(selectExpression));
        }

        protected virtual void GenerateOrderings([NotNull] SelectExpression selectExpression)
        {
            Check.NotNull(selectExpression, nameof(selectExpression));

            if (selectExpression.Orderings.Any())
            {
                var orderings = selectExpression.Orderings.ToList();

                if (selectExpression.Limit == null
                    && selectExpression.Offset == null)
                {
                    orderings.RemoveAll(oe => oe.Expression is SqlConstantExpression || oe.Expression is SqlParameterExpression);
                }

                if (orderings.Count > 0)
                {
                    _relationalCommandBuilder.AppendLine()
                        .Append("ORDER BY ");

                    GenerateList(orderings, e => Visit(e));
                }
            }
            else if (selectExpression.Offset != null)
            {
                _relationalCommandBuilder.AppendLine().Append("ORDER BY (SELECT 1)");
            }
        }

        protected virtual void GenerateLimitOffset([NotNull] SelectExpression selectExpression)
        {
            Check.NotNull(selectExpression, nameof(selectExpression));

            if (selectExpression.Offset != null)
            {
                _relationalCommandBuilder.AppendLine()
                    .Append("OFFSET ");

                Visit(selectExpression.Offset);

                _relationalCommandBuilder.Append(" ROWS");

                if (selectExpression.Limit != null)
                {
                    _relationalCommandBuilder.Append(" FETCH NEXT ");

                    Visit(selectExpression.Limit);

                    _relationalCommandBuilder.Append(" ROWS ONLY");
                }
            }
            else if (selectExpression.Limit != null)
            {
                _relationalCommandBuilder.AppendLine()
                    .Append("FETCH FIRST ");

                Visit(selectExpression.Limit);

                _relationalCommandBuilder.Append(" ROWS ONLY");
            }
        }

        private void GenerateList<T>(
            IReadOnlyList<T> items,
            Action<T> generationAction,
            Action<IRelationalCommandBuilder> joinAction = null)
        {
            joinAction ??= (isb => isb.Append(", "));

            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    joinAction(_relationalCommandBuilder);
                }

                generationAction(items[i]);
            }
        }

        protected override Expression VisitCrossJoin(CrossJoinExpression crossJoinExpression)
        {
            Check.NotNull(crossJoinExpression, nameof(crossJoinExpression));

            _relationalCommandBuilder.Append("CROSS JOIN ");
            Visit(crossJoinExpression.Table);

            return crossJoinExpression;
        }

        protected override Expression VisitCrossApply(CrossApplyExpression crossApplyExpression)
        {
            Check.NotNull(crossApplyExpression, nameof(crossApplyExpression));

            _relationalCommandBuilder.Append("CROSS APPLY ");
            Visit(crossApplyExpression.Table);

            return crossApplyExpression;
        }

        protected override Expression VisitOuterApply(OuterApplyExpression outerApplyExpression)
        {
            Check.NotNull(outerApplyExpression, nameof(outerApplyExpression));

            _relationalCommandBuilder.Append("OUTER APPLY ");
            Visit(outerApplyExpression.Table);

            return outerApplyExpression;
        }

        protected override Expression VisitInnerJoin(InnerJoinExpression innerJoinExpression)
        {
            Check.NotNull(innerJoinExpression, nameof(innerJoinExpression));

            _relationalCommandBuilder.Append("INNER JOIN ");
            Visit(innerJoinExpression.Table);
            _relationalCommandBuilder.Append(" ON ");
            Visit(innerJoinExpression.JoinPredicate);

            return innerJoinExpression;
        }

        protected override Expression VisitLeftJoin(LeftJoinExpression leftJoinExpression)
        {
            Check.NotNull(leftJoinExpression, nameof(leftJoinExpression));

            _relationalCommandBuilder.Append("LEFT JOIN ");
            Visit(leftJoinExpression.Table);
            _relationalCommandBuilder.Append(" ON ");
            Visit(leftJoinExpression.JoinPredicate);

            return leftJoinExpression;
        }

        protected override Expression VisitScalarSubquery(ScalarSubqueryExpression scalarSubqueryExpression)
        {
            Check.NotNull(scalarSubqueryExpression, nameof(scalarSubqueryExpression));

            _relationalCommandBuilder.AppendLine("(");
            using (_relationalCommandBuilder.Indent())
            {
                Visit(scalarSubqueryExpression.Subquery);
            }

            _relationalCommandBuilder.Append(")");

            return scalarSubqueryExpression;
        }

        protected override Expression VisitRowNumber(RowNumberExpression rowNumberExpression)
        {
            Check.NotNull(rowNumberExpression, nameof(rowNumberExpression));

            _relationalCommandBuilder.Append("ROW_NUMBER() OVER(");
            if (rowNumberExpression.Partitions.Any())
            {
                _relationalCommandBuilder.Append("PARTITION BY ");
                GenerateList(rowNumberExpression.Partitions, e => Visit(e));
                _relationalCommandBuilder.Append(" ");
            }

            _relationalCommandBuilder.Append("ORDER BY ");
            GenerateList(rowNumberExpression.Orderings, e => Visit(e));
            _relationalCommandBuilder.Append(")");

            return rowNumberExpression;
        }

        protected virtual void GenerateSetOperation([NotNull] SetOperationBase setOperation)
        {
            Check.NotNull(setOperation, nameof(setOperation));

            string getSetOperation() => setOperation switch
            {
                ExceptExpression _ => "EXCEPT",
                IntersectExpression _ => "INTERSECT",
                UnionExpression _ => "UNION",
                _ => throw new InvalidOperationException(CoreStrings.UnknownEntity("SetOperationType")),
            };

            GenerateSetOperationOperand(setOperation, setOperation.Source1);
            _relationalCommandBuilder.AppendLine();
            _relationalCommandBuilder.AppendLine($"{getSetOperation()}{(setOperation.IsDistinct ? "" : " ALL")}");
            GenerateSetOperationOperand(setOperation, setOperation.Source2);
        }

        protected virtual void GenerateSetOperationOperand([NotNull] SetOperationBase setOperation, [NotNull] SelectExpression operand)
        {
            Check.NotNull(setOperation, nameof(setOperation));
            Check.NotNull(operand, nameof(operand));

            // INTERSECT has higher precedence over UNION and EXCEPT, but otherwise evaluation is left-to-right.
            // To preserve meaning, add parentheses whenever a set operation is nested within a different set operation.
            if (IsNonComposedSetOperation(operand)
                && operand.Tables[0].GetType() != setOperation.GetType())
            {
                _relationalCommandBuilder.AppendLine("(");
                using (_relationalCommandBuilder.Indent())
                {
                    Visit(operand);
                }

                _relationalCommandBuilder.AppendLine().Append(")");
            }
            else
            {
                Visit(operand);
            }
        }

        private void GenerateSetOperationHelper(SetOperationBase setOperation)
        {
            _relationalCommandBuilder.AppendLine("(");
            using (_relationalCommandBuilder.Indent())
            {
                GenerateSetOperation(setOperation);
            }

            _relationalCommandBuilder.AppendLine()
                .Append(")")
                .Append(AliasSeparator)
                .Append(_sqlGenerationHelper.DelimitIdentifier(setOperation.Alias));
        }

        protected override Expression VisitExcept(ExceptExpression exceptExpression)
        {
            Check.NotNull(exceptExpression, nameof(exceptExpression));

            GenerateSetOperationHelper(exceptExpression);

            return exceptExpression;
        }

        protected override Expression VisitIntersect(IntersectExpression intersectExpression)
        {
            Check.NotNull(intersectExpression, nameof(intersectExpression));

            GenerateSetOperationHelper(intersectExpression);

            return intersectExpression;
        }

        protected override Expression VisitUnion(UnionExpression unionExpression)
        {
            Check.NotNull(unionExpression, nameof(unionExpression));

            GenerateSetOperationHelper(unionExpression);

            return unionExpression;
        }
    }
}
