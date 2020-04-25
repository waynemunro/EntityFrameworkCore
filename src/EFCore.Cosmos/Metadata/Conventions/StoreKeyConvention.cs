// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Linq;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Cosmos.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Cosmos.ValueGeneration.Internal;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Utilities;
using Newtonsoft.Json.Linq;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore.Metadata.Conventions
{
    /// <summary>
    ///     <para>
    ///         A convention that adds the 'id' property - a key required by Azure Cosmos.
    ///     </para>
    ///     <para>
    ///         This convention also adds the '__jObject' containing the JSON object returned by the store.
    ///     </para>
    /// </summary>
    public class StoreKeyConvention :
        IEntityTypeAddedConvention,
        IForeignKeyOwnershipChangedConvention,
        IForeignKeyRemovedConvention,
        IEntityTypeAnnotationChangedConvention,
        IEntityTypeBaseTypeChangedConvention
    {
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        public static readonly string IdPropertyName = "id";
        public static readonly string JObjectPropertyName = "__jObject";
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member

        /// <summary>
        ///     Creates a new instance of <see cref="StoreKeyConvention" />.
        /// </summary>
        /// <param name="dependencies"> Parameter object containing dependencies for this convention. </param>
        public StoreKeyConvention([NotNull] ProviderConventionSetBuilderDependencies dependencies)
        {
            Dependencies = dependencies;
        }

        /// <summary>
        ///     Parameter object containing service dependencies.
        /// </summary>
        protected virtual ProviderConventionSetBuilderDependencies Dependencies { get; }

        private static void Process(IConventionEntityTypeBuilder entityTypeBuilder)
        {
            IConventionKey newKey = null;
            IConventionProperty idProperty = null;
            var entityType = entityTypeBuilder.Metadata;
            if (entityType.BaseType == null
                && entityType.IsDocumentRoot()
                && !entityType.IsKeyless)
            {
                idProperty = entityTypeBuilder.Property(typeof(string), IdPropertyName, setTypeConfigurationSource: false)
                    ?.Metadata;

                if (idProperty != null)
                {
                    if (idProperty.ClrType == typeof(string))
                    {
                        idProperty.Builder.HasValueGenerator((_, __) => new IdValueGenerator());
                    }

                    var partitionKey = entityType.GetPartitionKeyPropertyName();
                    if (partitionKey != null)
                    {
                        var partitionKeyProperty = entityType.FindProperty(partitionKey);
                        if (partitionKeyProperty != null)
                        {
                            newKey = entityTypeBuilder.HasKey(new[] { idProperty, partitionKeyProperty })?.Metadata;
                        }
                    }
                    else
                    {
                        newKey = entityTypeBuilder.HasKey(new[] { idProperty })?.Metadata;
                    }
                }
            }
            else
            {
                idProperty = entityType.FindDeclaredProperty(IdPropertyName);
            }

            if (idProperty != null)
            {
                foreach (var key in idProperty.GetContainingKeys().ToList())
                {
                    if (key != newKey)
                    {
                        key.DeclaringEntityType.Builder.HasNoKey(key);
                    }
                }
            }

            if (entityType.BaseType == null
                && !entityType.IsKeyless)
            {
                var jObjectProperty = entityTypeBuilder.Property(typeof(JObject), JObjectPropertyName);
                jObjectProperty.ToJsonProperty("");
                jObjectProperty.ValueGenerated(ValueGenerated.OnAddOrUpdate);
            }
            else
            {
                var jObjectProperty = entityType.FindDeclaredProperty(JObjectPropertyName);
                if (jObjectProperty != null)
                {
                    entityType.Builder.HasNoUnusedShadowProperties(new[] { jObjectProperty });
                }
            }
        }

        /// <summary>
        ///     Called after an entity type is added to the model.
        /// </summary>
        /// <param name="entityTypeBuilder"> The builder for the entity type. </param>
        /// <param name="context"> Additional information associated with convention execution. </param>
        public virtual void ProcessEntityTypeAdded(
            IConventionEntityTypeBuilder entityTypeBuilder,
            IConventionContext<IConventionEntityTypeBuilder> context)
        {
            Check.NotNull(entityTypeBuilder, nameof(entityTypeBuilder));

            Process(entityTypeBuilder);
        }

        /// <summary>
        ///     Called after the ownership value for a foreign key is changed.
        /// </summary>
        /// <param name="relationshipBuilder"> The builder for the foreign key. </param>
        /// <param name="context"> Additional information associated with convention execution. </param>
        public virtual void ProcessForeignKeyOwnershipChanged(
            IConventionForeignKeyBuilder relationshipBuilder,
            IConventionContext<bool?> context)
        {
            Check.NotNull(relationshipBuilder, nameof(relationshipBuilder));
            Check.NotNull(context, nameof(context));

            Process(relationshipBuilder.Metadata.DeclaringEntityType.Builder);
        }

        /// <summary>
        ///     Called after a foreign key is removed.
        /// </summary>
        /// <param name="entityTypeBuilder"> The builder for the entity type. </param>
        /// <param name="foreignKey"> The removed foreign key. </param>
        /// <param name="context"> Additional information associated with convention execution. </param>
        public virtual void ProcessForeignKeyRemoved(
            IConventionEntityTypeBuilder entityTypeBuilder,
            IConventionForeignKey foreignKey,
            IConventionContext<IConventionForeignKey> context)
        {
            if (foreignKey.IsOwnership)
            {
                Process(foreignKey.DeclaringEntityType.Builder);
            }
        }

        /// <summary>
        ///     Called after an annotation is changed on an entity type.
        /// </summary>
        /// <param name="entityTypeBuilder"> The builder for the entity type. </param>
        /// <param name="name"> The annotation name. </param>
        /// <param name="annotation"> The new annotation. </param>
        /// <param name="oldAnnotation"> The old annotation.  </param>
        /// <param name="context"> Additional information associated with convention execution. </param>
        public virtual void ProcessEntityTypeAnnotationChanged(
            IConventionEntityTypeBuilder entityTypeBuilder,
            string name,
            IConventionAnnotation annotation,
            IConventionAnnotation oldAnnotation,
            IConventionContext<IConventionAnnotation> context)
        {
            Check.NotNull(entityTypeBuilder, nameof(entityTypeBuilder));
            Check.NotEmpty(name, nameof(name));
            Check.NotNull(context, nameof(context));

            if (name == CosmosAnnotationNames.ContainerName
                || name == CosmosAnnotationNames.PartitionKeyName)
            {
                Process(entityTypeBuilder);
            }
        }

        /// <summary>
        ///     Called after the base type of an entity type changes.
        /// </summary>
        /// <param name="entityTypeBuilder"> The builder for the entity type. </param>
        /// <param name="newBaseType"> The new base entity type. </param>
        /// <param name="oldBaseType"> The old base entity type. </param>
        /// <param name="context"> Additional information associated with convention execution. </param>
        public virtual void ProcessEntityTypeBaseTypeChanged(
            IConventionEntityTypeBuilder entityTypeBuilder,
            IConventionEntityType newBaseType,
            IConventionEntityType oldBaseType,
            IConventionContext<IConventionEntityType> context)
        {
            Check.NotNull(entityTypeBuilder, nameof(entityTypeBuilder));
            Check.NotNull(context, nameof(context));

            if (entityTypeBuilder.Metadata.BaseType != newBaseType)
            {
                return;
            }

            Process(entityTypeBuilder);
        }
    }
}
