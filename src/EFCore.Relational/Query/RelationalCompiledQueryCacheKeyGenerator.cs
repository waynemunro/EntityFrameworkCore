// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Utilities;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.EntityFrameworkCore.Query
{
    /// <summary>
    ///     <para>
    ///         Creates keys that uniquely identifies a query. This is used to store and lookup
    ///         compiled versions of a query in a cache.
    ///     </para>
    ///     <para>
    ///         This type is typically used by database providers (and other extensions). It is generally
    ///         not used in application code.
    ///     </para>
    ///     <para>
    ///         The service lifetime is <see cref="ServiceLifetime.Scoped" />. This means that each
    ///         <see cref="DbContext" /> instance will use its own instance of this service.
    ///         The implementation may depend on other services registered with any lifetime.
    ///         The implementation does not need to be thread-safe.
    ///     </para>
    /// </summary>
    public class RelationalCompiledQueryCacheKeyGenerator : CompiledQueryCacheKeyGenerator
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="RelationalCompiledQueryCacheKeyGenerator" /> class.
        /// </summary>
        /// <param name="dependencies"> Parameter object containing dependencies for this service. </param>
        /// <param name="relationalDependencies"> Parameter object containing relational dependencies for this service. </param>
        public RelationalCompiledQueryCacheKeyGenerator(
            [NotNull] CompiledQueryCacheKeyGeneratorDependencies dependencies,
            [NotNull] RelationalCompiledQueryCacheKeyGeneratorDependencies relationalDependencies)
            : base(dependencies)
        {
            Check.NotNull(relationalDependencies, nameof(relationalDependencies));

            RelationalDependencies = relationalDependencies;
        }

        /// <summary>
        ///     Dependencies used to create a <see cref="RelationalCompiledQueryCacheKeyGenerator" />
        /// </summary>
        protected virtual RelationalCompiledQueryCacheKeyGeneratorDependencies RelationalDependencies { get; }

        /// <summary>
        ///     Generates the cache key for the given query.
        /// </summary>
        /// <param name="query"> The query to get the cache key for. </param>
        /// <param name="async"> A value indicating whether the query will be executed asynchronously. </param>
        /// <returns> The cache key. </returns>
        public override object GenerateCacheKey(Expression query, bool async)
            => GenerateCacheKeyCore(query, async);

        /// <summary>
        ///     Generates the cache key for the given query.
        /// </summary>
        /// <param name="query"> The query to get the cache key for. </param>
        /// <param name="async"> A value indicating whether the query will be executed asynchronously. </param>
        /// <returns> The cache key. </returns>
        protected new RelationalCompiledQueryCacheKey GenerateCacheKeyCore([NotNull] Expression query, bool async) // Intentionally non-virtual
            => new RelationalCompiledQueryCacheKey(
                base.GenerateCacheKeyCore(query, async),
                RelationalOptionsExtension.Extract(RelationalDependencies.ContextOptions).UseRelationalNulls,
                shouldBuffer: Dependencies.IsRetryingExecutionStrategy);

        /// <summary>
        ///     <para>
        ///         A key that uniquely identifies a query. This is used to store and lookup
        ///         compiled versions of a query in a cache.
        ///     </para>
        ///     <para>
        ///         This type is typically used by database providers (and other extensions). It is generally
        ///         not used in application code.
        ///     </para>
        /// </summary>
        protected readonly struct RelationalCompiledQueryCacheKey
        {
            private readonly CompiledQueryCacheKey _compiledQueryCacheKey;
            private readonly bool _useRelationalNulls;
            private readonly bool _shouldBuffer;

            /// <summary>
            ///     Initializes a new instance of the <see cref="RelationalCompiledQueryCacheKey" /> class.
            /// </summary>
            /// <param name="compiledQueryCacheKey"> The non-relational cache key. </param>
            /// <param name="useRelationalNulls"> True to use relational null logic. </param>
            /// <param name="shouldBuffer"> True if the query should be buffered. </param>
            public RelationalCompiledQueryCacheKey(
                CompiledQueryCacheKey compiledQueryCacheKey, bool useRelationalNulls, bool shouldBuffer)
            {
                _compiledQueryCacheKey = compiledQueryCacheKey;
                _useRelationalNulls = useRelationalNulls;
                _shouldBuffer = shouldBuffer;
            }

            /// <summary>
            ///     Determines if this key is equivalent to a given object (i.e. if they are keys for the same query).
            /// </summary>
            /// <param name="obj">
            ///     The object to compare this key to.
            /// </param>
            /// <returns>
            ///     True if the object is a <see cref="RelationalCompiledQueryCacheKey" /> and is for the same query, otherwise false.
            /// </returns>
            public override bool Equals(object obj)
                => !(obj is null)
                    && obj is RelationalCompiledQueryCacheKey key
                    && Equals(key);

            private bool Equals(RelationalCompiledQueryCacheKey other)
                => _compiledQueryCacheKey.Equals(other._compiledQueryCacheKey)
                    && _useRelationalNulls == other._useRelationalNulls
                    && _shouldBuffer == other._shouldBuffer;

            /// <summary>
            ///     Gets the hash code for the key.
            /// </summary>
            /// <returns>
            ///     The hash code for the key.
            /// </returns>
            public override int GetHashCode() => HashCode.Combine(_compiledQueryCacheKey, _useRelationalNulls, _shouldBuffer);
        }
    }
}
