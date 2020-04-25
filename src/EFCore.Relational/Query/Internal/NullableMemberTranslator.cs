// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.Query.Internal
{
    public class NullableMemberTranslator : IMemberTranslator
    {
        private readonly ISqlExpressionFactory _sqlExpressionFactory;

        public NullableMemberTranslator([NotNull] ISqlExpressionFactory sqlExpressionFactory)
        {
            _sqlExpressionFactory = sqlExpressionFactory;
        }

        public virtual SqlExpression Translate(SqlExpression instance, MemberInfo member, Type returnType)
        {
            Check.NotNull(member, nameof(member));
            Check.NotNull(returnType, nameof(returnType));

            if (member.DeclaringType.IsNullableValueType())
            {
                switch (member.Name)
                {
                    case nameof(Nullable<int>.Value):
                        return instance;

                    case nameof(Nullable<int>.HasValue):
                        return _sqlExpressionFactory.IsNotNull(instance);
                }
            }

            return null;
        }
    }
}
