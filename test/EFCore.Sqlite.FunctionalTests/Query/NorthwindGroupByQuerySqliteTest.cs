﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.EntityFrameworkCore.Query
{
    public class NorthwindGroupByQuerySqliteTest : NorthwindGroupByQueryTestBase<NorthwindQuerySqliteFixture<NoopModelCustomizer>>
    {
        // ReSharper disable once UnusedParameter.Local
        public NorthwindGroupByQuerySqliteTest(NorthwindQuerySqliteFixture<NoopModelCustomizer> fixture, ITestOutputHelper testOutputHelper)
            : base(fixture)
        {
            Fixture.TestSqlLoggerFactory.Clear();
            //Fixture.TestSqlLoggerFactory.SetTestOutputHelper(testOutputHelper);
        }

        public override Task GroupBy_Property_Select_Count_with_predicate(bool async)
        {
            return Assert.ThrowsAsync<InvalidOperationException>(
                () => base.GroupBy_Property_Select_Count_with_predicate(async));
        }

        public override Task GroupBy_Property_Select_LongCount_with_predicate(bool async)
        {
            return Assert.ThrowsAsync<InvalidOperationException>(
                () => base.GroupBy_Property_Select_LongCount_with_predicate(async));
        }
    }
}
