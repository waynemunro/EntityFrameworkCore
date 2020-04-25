// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.Migrations.Design
{
    /// <summary>
    ///     Used to generate C# code for creating an <see cref="IModel" />.
    /// </summary>
    public class CSharpSnapshotGenerator : ICSharpSnapshotGenerator
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="CSharpSnapshotGenerator" /> class.
        /// </summary>
        /// <param name="dependencies"> The dependencies. </param>
        public CSharpSnapshotGenerator([NotNull] CSharpSnapshotGeneratorDependencies dependencies)
        {
            Check.NotNull(dependencies, nameof(dependencies));

            Dependencies = dependencies;
        }

        /// <summary>
        ///     Parameter object containing dependencies for this service.
        /// </summary>
        protected virtual CSharpSnapshotGeneratorDependencies Dependencies { get; }

        private ICSharpHelper Code => Dependencies.CSharpHelper;

        /// <summary>
        ///     Generates code for creating an <see cref="IModel" />.
        /// </summary>
        /// <param name="builderName"> The <see cref="ModelBuilder" /> variable name. </param>
        /// <param name="model"> The model. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        public virtual void Generate(string builderName, IModel model, IndentedStringBuilder stringBuilder)
        {
            Check.NotEmpty(builderName, nameof(builderName));
            Check.NotNull(model, nameof(model));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            var annotations = model.GetAnnotations().ToList();

            IgnoreAnnotations(
                annotations,
                ChangeDetector.SkipDetectChangesAnnotation,
                CoreAnnotationNames.ChangeTrackingStrategy,
                CoreAnnotationNames.OwnedTypes,
                RelationalAnnotationNames.RelationalModel,
                RelationalAnnotationNames.CheckConstraints,
                RelationalAnnotationNames.Sequences,
                RelationalAnnotationNames.DbFunctions);

            if (annotations.Count > 0)
            {
                stringBuilder.Append(builderName);

                using (stringBuilder.Indent())
                {
                    GenerateFluentApiForAnnotation(
                        ref annotations, RelationalAnnotationNames.DefaultSchema, nameof(RelationalModelBuilderExtensions.HasDefaultSchema),
                        stringBuilder);

                    GenerateAnnotations(annotations, stringBuilder);
                }

                stringBuilder.AppendLine(";");
            }

            foreach (var sequence in model.GetSequences())
            {
                GenerateSequence(builderName, sequence, stringBuilder);
            }

            GenerateEntityTypes(builderName, Sort(model.GetEntityTypes().Where(et => !et.IsIgnoredByMigrations()).ToList()), stringBuilder);
        }

        private static IReadOnlyList<IEntityType> Sort(IReadOnlyList<IEntityType> entityTypes)
        {
            var entityTypeGraph = new Multigraph<IEntityType, int>();
            entityTypeGraph.AddVertices(entityTypes);
            foreach (var entityType in entityTypes.Where(et => et.BaseType != null))
            {
                entityTypeGraph.AddEdge(entityType.BaseType, entityType, 0);
            }

            return entityTypeGraph.TopologicalSort();
        }

        /// <summary>
        ///     Generates code for <see cref="IEntityType" /> objects.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="entityTypes"> The entity types. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateEntityTypes(
            [NotNull] string builderName,
            [NotNull] IReadOnlyList<IEntityType> entityTypes,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotEmpty(builderName, nameof(builderName));
            Check.NotNull(entityTypes, nameof(entityTypes));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            foreach (var entityType in entityTypes.Where(
                e => !e.HasDefiningNavigation()
                     && e.FindOwnership() == null))
            {
                stringBuilder.AppendLine();

                GenerateEntityType(builderName, entityType, stringBuilder);
            }

            foreach (var entityType in entityTypes.Where(
                e => !e.HasDefiningNavigation()
                     && e.FindOwnership() == null
                     && (e.GetDeclaredForeignKeys().Any()
                         || e.GetDeclaredReferencingForeignKeys().Any(fk => fk.IsOwnership))))
            {
                stringBuilder.AppendLine();

                GenerateEntityTypeRelationships(builderName, entityType, stringBuilder);
            }
        }

        /// <summary>
        ///     Generates code for an <see cref="IEntityType" />.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="entityType"> The entity type. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateEntityType(
            [NotNull] string builderName,
            [NotNull] IEntityType entityType,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotEmpty(builderName, nameof(builderName));
            Check.NotNull(entityType, nameof(entityType));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            var ownership = entityType.FindOwnership();
            var ownerNavigation = ownership?.PrincipalToDependent.Name;

            stringBuilder
                .Append(builderName)
                .Append(
                    ownerNavigation != null
                        ? ownership.IsUnique ? ".OwnsOne(" : ".OwnsMany("
                        : ".Entity(")
                .Append(Code.Literal(entityType.Name));

            if (ownerNavigation != null)
            {
                stringBuilder
                    .Append(", ")
                    .Append(Code.Literal(ownerNavigation));
            }

            if (builderName.StartsWith("b", StringComparison.Ordinal))
            {
                // ReSharper disable once InlineOutVariableDeclaration
                var counter = 1;
                if (builderName.Length > 1
                    && int.TryParse(builderName[1..], out counter))
                {
                    counter++;
                }

                builderName = "b" + (counter == 0 ? "" : counter.ToString());
            }
            else
            {
                builderName = "b";
            }

            stringBuilder
                .Append(", ")
                .Append(builderName)
                .AppendLine(" =>");

            using (stringBuilder.Indent())
            {
                stringBuilder.Append("{");

                using (stringBuilder.Indent())
                {
                    GenerateBaseType(builderName, entityType.BaseType, stringBuilder);

                    GenerateProperties(builderName, entityType.GetDeclaredProperties(), stringBuilder);

                    GenerateKeys(builderName, entityType.GetDeclaredKeys(), entityType.FindDeclaredPrimaryKey(), stringBuilder);

                    GenerateIndexes(builderName, entityType.GetDeclaredIndexes(), stringBuilder);

                    GenerateEntityTypeAnnotations(builderName, entityType, stringBuilder);

                    GenerateCheckConstraints(builderName, entityType, stringBuilder);

                    if (ownerNavigation != null)
                    {
                        GenerateRelationships(builderName, entityType, stringBuilder);
                    }

                    GenerateData(builderName, entityType.GetProperties(), entityType.GetSeedData(providerValues: true), stringBuilder);
                }

                stringBuilder
                    .AppendLine("});");
            }
        }

        /// <summary>
        ///     Generates code for owned entity types.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="ownerships"> The foreign keys identifying each entity type. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateOwnedTypes(
            [NotNull] string builderName,
            [NotNull] IEnumerable<IForeignKey> ownerships,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(builderName, nameof(builderName));
            Check.NotNull(ownerships, nameof(ownerships));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            foreach (var ownership in ownerships)
            {
                stringBuilder.AppendLine();

                GenerateOwnedType(builderName, ownership, stringBuilder);
            }
        }

        /// <summary>
        ///     Generates code for an owned entity types.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="ownership"> The foreign key identifying the entity type. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateOwnedType(
            [NotNull] string builderName,
            [NotNull] IForeignKey ownership,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(builderName, nameof(builderName));
            Check.NotNull(ownership, nameof(ownership));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            GenerateEntityType(builderName, ownership.DeclaringEntityType, stringBuilder);
        }

        /// <summary>
        ///     Generates code for the relationships of an <see cref="IEntityType" />.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="entityType"> The entity type. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateEntityTypeRelationships(
            [NotNull] string builderName,
            [NotNull] IEntityType entityType,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotEmpty(builderName, nameof(builderName));
            Check.NotNull(entityType, nameof(entityType));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            stringBuilder
                .Append(builderName)
                .Append(".Entity(")
                .Append(Code.Literal(entityType.Name))
                .AppendLine(", b =>");

            using (stringBuilder.Indent())
            {
                stringBuilder.Append("{");

                using (stringBuilder.Indent())
                {
                    GenerateRelationships("b", entityType, stringBuilder);
                }

                stringBuilder.AppendLine("});");
            }
        }

        /// <summary>
        ///     Generates code for the relationships of an <see cref="IEntityType" />.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="entityType"> The entity type. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateRelationships(
            [NotNull] string builderName,
            [NotNull] IEntityType entityType,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotEmpty(builderName, nameof(builderName));
            Check.NotNull(entityType, nameof(entityType));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            GenerateForeignKeys(builderName, entityType.GetDeclaredForeignKeys(), stringBuilder);

            GenerateOwnedTypes(builderName, entityType.GetDeclaredReferencingForeignKeys().Where(fk => fk.IsOwnership), stringBuilder);
        }

        /// <summary>
        ///     Generates code for the base type of an <see cref="IEntityType" />.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="baseType"> The base entity type. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateBaseType(
            [NotNull] string builderName,
            [CanBeNull] IEntityType baseType,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(builderName, nameof(builderName));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            if (baseType != null)
            {
                stringBuilder
                    .AppendLine()
                    .Append(builderName)
                    .Append(".HasBaseType(")
                    .Append(Code.Literal(baseType.Name))
                    .AppendLine(");");
            }
        }

        /// <summary>
        ///     Generates code for an <see cref="ISequence" />.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="sequence"> The sequence. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateSequence(
            [NotNull] string builderName,
            [NotNull] ISequence sequence,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            stringBuilder
                .AppendLine()
                .Append(builderName)
                .Append(".HasSequence");

            if (sequence.ClrType != Sequence.DefaultClrType)
            {
                stringBuilder
                    .Append("<")
                    .Append(Code.Reference(sequence.ClrType))
                    .Append(">");
            }

            stringBuilder
                .Append("(")
                .Append(Code.Literal(sequence.Name));

            if (!string.IsNullOrEmpty(sequence.Schema)
                && sequence.Model.GetDefaultSchema() != sequence.Schema)
            {
                stringBuilder
                    .Append(", ")
                    .Append(Code.Literal(sequence.Schema));
            }

            stringBuilder.Append(")");

            using (stringBuilder.Indent())
            {
                if (sequence.StartValue != Sequence.DefaultStartValue)
                {
                    stringBuilder
                        .AppendLine()
                        .Append(".StartsAt(")
                        .Append(Code.Literal(sequence.StartValue))
                        .Append(")");
                }

                if (sequence.IncrementBy != Sequence.DefaultIncrementBy)
                {
                    stringBuilder
                        .AppendLine()
                        .Append(".IncrementsBy(")
                        .Append(Code.Literal(sequence.IncrementBy))
                        .Append(")");
                }

                if (sequence.MinValue != Sequence.DefaultMinValue)
                {
                    stringBuilder
                        .AppendLine()
                        .Append(".HasMin(")
                        .Append(Code.Literal(sequence.MinValue))
                        .Append(")");
                }

                if (sequence.MaxValue != Sequence.DefaultMaxValue)
                {
                    stringBuilder
                        .AppendLine()
                        .Append(".HasMax(")
                        .Append(Code.Literal(sequence.MaxValue))
                        .Append(")");
                }

                if (sequence.IsCyclic != Sequence.DefaultIsCyclic)
                {
                    stringBuilder
                        .AppendLine()
                        .Append(".IsCyclic()");
                }
            }

            stringBuilder.AppendLine(";");
        }

        /// <summary>
        ///     Generates code for <see cref="IProperty" /> objects.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="properties"> The properties. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateProperties(
            [NotNull] string builderName,
            [NotNull] IEnumerable<IProperty> properties,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(builderName, nameof(builderName));
            Check.NotNull(properties, nameof(properties));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            foreach (var property in properties)
            {
                GenerateProperty(builderName, property, stringBuilder);
            }
        }

        /// <summary>
        ///     Generates code for an <see cref="IProperty" />.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="property"> The property. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateProperty(
            [NotNull] string builderName,
            [NotNull] IProperty property,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(builderName, nameof(builderName));
            Check.NotNull(property, nameof(property));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            var clrType = FindValueConverter(property)?.ProviderClrType.MakeNullable(property.IsNullable)
                          ?? property.ClrType;

            stringBuilder
                .AppendLine()
                .Append(builderName)
                .Append(".Property<")
                .Append(Code.Reference(clrType))
                .Append(">(")
                .Append(Code.Literal(property.Name))
                .Append(")");

            using (stringBuilder.Indent())
            {
                if (property.IsConcurrencyToken)
                {
                    stringBuilder
                        .AppendLine()
                        .Append(".IsConcurrencyToken()");
                }

                if (property.IsNullable != (clrType.IsNullableType() && !property.IsPrimaryKey()))
                {
                    stringBuilder
                        .AppendLine()
                        .Append(".IsRequired()");
                }

                if (property.ValueGenerated != ValueGenerated.Never)
                {
                    stringBuilder
                        .AppendLine()
                        .Append(
                            property.ValueGenerated == ValueGenerated.OnAdd
                                ? ".ValueGeneratedOnAdd()"
                                : property.ValueGenerated == ValueGenerated.OnUpdate
                                    ? ".ValueGeneratedOnUpdate()"
                                    : ".ValueGeneratedOnAddOrUpdate()");
                }

                GeneratePropertyAnnotations(property, stringBuilder);
            }

            stringBuilder.AppendLine(";");
        }

        /// <summary>
        ///     Generates code for the annotations on an <see cref="IProperty" />.
        /// </summary>
        /// <param name="property"> The property. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GeneratePropertyAnnotations([NotNull] IProperty property, [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(property, nameof(property));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            var annotations = property.GetAnnotations().ToList();

            GenerateFluentApiForAnnotation(
                ref annotations,
                RelationalAnnotationNames.ColumnName,
                nameof(RelationalPropertyBuilderExtensions.HasColumnName),
                stringBuilder);

            GenerateFluentApiForAnnotation(
                ref annotations,
                RelationalAnnotationNames.ViewColumnName,
                nameof(RelationalPropertyBuilderExtensions.HasViewColumnName),
                stringBuilder);

            stringBuilder
                .AppendLine()
                .Append(".")
                .Append(nameof(RelationalPropertyBuilderExtensions.HasColumnType))
                .Append("(")
                .Append(
                    Code.Literal(
                        property.GetColumnType()
                            ?? Dependencies.RelationalTypeMappingSource.GetMapping(property).StoreType))
                .Append(")");

            GenerateFluentApiForAnnotation(
                ref annotations,
                RelationalAnnotationNames.DefaultValueSql,
                nameof(RelationalPropertyBuilderExtensions.HasDefaultValueSql),
                stringBuilder);

            GenerateFluentApiForComputedColumn(ref annotations, stringBuilder);

            GenerateFluentApiForAnnotation(
                ref annotations,
                RelationalAnnotationNames.IsFixedLength,
                nameof(RelationalPropertyBuilderExtensions.IsFixedLength),
                stringBuilder);

            GenerateFluentApiForAnnotation(
                ref annotations,
                RelationalAnnotationNames.Comment,
                nameof(RelationalPropertyBuilderExtensions.HasComment),
                stringBuilder);

            GenerateFluentApiForAnnotation(
                ref annotations,
                RelationalAnnotationNames.Collation,
                nameof(RelationalPropertyBuilderExtensions.UseCollation),
                stringBuilder);

            GenerateFluentApiForAnnotation(
                ref annotations,
                CoreAnnotationNames.MaxLength,
                nameof(PropertyBuilder.HasMaxLength),
                stringBuilder);

            GenerateFluentApiForPrecisionAndScale(ref annotations, stringBuilder);

            GenerateFluentApiForAnnotation(
                ref annotations,
                CoreAnnotationNames.Unicode,
                nameof(PropertyBuilder.IsUnicode),
                stringBuilder);

            var valueConverter = FindValueConverter(property);

            GenerateFluentApiForAnnotation(
                ref annotations,
                RelationalAnnotationNames.DefaultValue,
                a => valueConverter == null ? a?.Value : valueConverter.ConvertToProvider(a?.Value),
                nameof(RelationalPropertyBuilderExtensions.HasDefaultValue),
                stringBuilder);

            IgnoreAnnotations(
                annotations,
                RelationalAnnotationNames.ColumnType,
                RelationalAnnotationNames.TableColumnMappings,
                RelationalAnnotationNames.ViewColumnMappings,
                CoreAnnotationNames.ValueGeneratorFactory,
                CoreAnnotationNames.PropertyAccessMode,
                CoreAnnotationNames.ChangeTrackingStrategy,
                CoreAnnotationNames.BeforeSaveBehavior,
                CoreAnnotationNames.AfterSaveBehavior,
                CoreAnnotationNames.TypeMapping,
                CoreAnnotationNames.ValueComparer,
#pragma warning disable 618
                CoreAnnotationNames.KeyValueComparer,
                CoreAnnotationNames.StructuralValueComparer,
#pragma warning restore 618
                CoreAnnotationNames.ValueConverter,
                CoreAnnotationNames.ProviderClrType);

            GenerateAnnotations(annotations, stringBuilder);
        }

        private ValueConverter FindValueConverter(IProperty property)
            => (property.FindTypeMapping()
                ?? Dependencies.RelationalTypeMappingSource.FindMapping(property))?.Converter;

        /// <summary>
        ///     Generates code for <see cref="IKey" /> objects.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="keys"> The keys. </param>
        /// <param name="primaryKey"> The primary key. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateKeys(
            [NotNull] string builderName,
            [NotNull] IEnumerable<IKey> keys,
            [CanBeNull] IKey primaryKey,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(builderName, nameof(builderName));
            Check.NotNull(keys, nameof(keys));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            if (primaryKey != null)
            {
                GenerateKey(builderName, primaryKey, stringBuilder, primary: true);
            }

            if (primaryKey?.DeclaringEntityType.IsOwned() != true)
            {
                foreach (var key in keys.Where(
                    key => key != primaryKey
                           && (!key.GetReferencingForeignKeys().Any()
                               || key.GetAnnotations().Any(a => a.Name != RelationalAnnotationNames.UniqueConstraintMappings))))
                {
                    GenerateKey(builderName, key, stringBuilder);
                }
            }
        }

        /// <summary>
        ///     Generates code for an <see cref="IKey" />.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="key"> The key. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        /// <param name="primary">A value indicating whether the key is primary. </param>
        protected virtual void GenerateKey(
            [NotNull] string builderName,
            [NotNull] IKey key,
            [NotNull] IndentedStringBuilder stringBuilder,
            bool primary = false)
        {
            Check.NotNull(builderName, nameof(builderName));
            Check.NotNull(key, nameof(key));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            stringBuilder
                .AppendLine()
                .Append(builderName)
                .Append(primary ? ".HasKey(" : ".HasAlternateKey(")
                .Append(string.Join(", ", key.Properties.Select(p => Code.Literal(p.Name))))
                .Append(")");

            using (stringBuilder.Indent())
            {
                GenerateKeyAnnotations(key, stringBuilder);
            }

            stringBuilder.AppendLine(";");
        }

        /// <summary>
        ///     Generates code for the annotations on a key.
        /// </summary>
        /// <param name="key"> The key. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateKeyAnnotations([NotNull] IKey key, [NotNull] IndentedStringBuilder stringBuilder)
        {
            var annotations = key.GetAnnotations().ToList();

            IgnoreAnnotations(
                annotations,
                RelationalAnnotationNames.UniqueConstraintMappings);

            GenerateFluentApiForAnnotation(
                ref annotations, RelationalAnnotationNames.Name, nameof(RelationalKeyBuilderExtensions.HasName), stringBuilder);

            GenerateAnnotations(annotations, stringBuilder);
        }

        /// <summary>
        ///     Generates code for <see cref="IIndex" /> objects.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="indexes"> The indexes. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateIndexes(
            [NotNull] string builderName,
            [NotNull] IEnumerable<IIndex> indexes,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(builderName, nameof(builderName));
            Check.NotNull(indexes, nameof(indexes));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            foreach (var index in indexes)
            {
                GenerateIndex(builderName, index, stringBuilder);
            }
        }

        /// <summary>
        ///     Generates code an <see cref="IIndex" />.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="index"> The index. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateIndex(
            [NotNull] string builderName,
            [NotNull] IIndex index,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(builderName, nameof(builderName));
            Check.NotNull(index, nameof(index));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            stringBuilder
                .AppendLine()
                .Append(builderName)
                .Append(".HasIndex(")
                .Append(string.Join(", ", index.Properties.Select(p => Code.Literal(p.Name))))
                .Append(")");

            using (stringBuilder.Indent())
            {
                if (index.IsUnique)
                {
                    stringBuilder
                        .AppendLine()
                        .Append(".IsUnique()");
                }

                GenerateIndexAnnotations(index, stringBuilder);
            }

            stringBuilder.AppendLine(";");
        }

        /// <summary>
        ///     Generates code for the annotations on an index.
        /// </summary>
        /// <param name="index"> The index. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateIndexAnnotations(
            [NotNull] IIndex index, [NotNull] IndentedStringBuilder stringBuilder)
        {
            var annotations = index.GetAnnotations().ToList();

            IgnoreAnnotations(
                annotations,
                RelationalAnnotationNames.TableIndexMappings);

            GenerateFluentApiForAnnotation(
                ref annotations, RelationalAnnotationNames.Name, nameof(RelationalIndexBuilderExtensions.HasName), stringBuilder);
            GenerateFluentApiForAnnotation(
                ref annotations, RelationalAnnotationNames.Filter, nameof(RelationalIndexBuilderExtensions.HasFilter), stringBuilder);

            GenerateAnnotations(annotations, stringBuilder);
        }

        /// <summary>
        ///     Generates code for the annotations on an entity type.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="entityType"> The entity type. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateEntityTypeAnnotations(
            [NotNull] string builderName,
            [NotNull] IEntityType entityType,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(builderName, nameof(builderName));
            Check.NotNull(entityType, nameof(entityType));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            var annotations = entityType.GetAnnotations().ToList();
            var tableNameAnnotation = annotations.FirstOrDefault(a => a.Name == RelationalAnnotationNames.TableName);
            var schemaAnnotation = annotations.FirstOrDefault(a => a.Name == RelationalAnnotationNames.Schema);

            var nonDefaultName = false;
            if (tableNameAnnotation?.Value != null
                || entityType.BaseType == null)
            {
                stringBuilder
                    .AppendLine()
                    .Append(builderName)
                    .Append(".")
                    .Append(nameof(RelationalEntityTypeBuilderExtensions.ToTable))
                    .Append("(")
                    .Append(Code.Literal((string)tableNameAnnotation?.Value ?? entityType.GetTableName()));
                annotations.Remove(tableNameAnnotation);
                nonDefaultName = true;
            }

            if (schemaAnnotation?.Value != null)
            {
                stringBuilder
                    .Append(",")
                    .Append(Code.Literal((string)schemaAnnotation.Value));
                annotations.Remove(schemaAnnotation);
                nonDefaultName = true;
            }

            if (nonDefaultName)
            {
                stringBuilder.AppendLine(");");
            }

            var discriminatorPropertyAnnotation = annotations.FirstOrDefault(a => a.Name == CoreAnnotationNames.DiscriminatorProperty);
            var discriminatorMappingCompleteAnnotation = annotations.FirstOrDefault(a => a.Name == CoreAnnotationNames.DiscriminatorMappingComplete);
            var discriminatorValueAnnotation = annotations.FirstOrDefault(a => a.Name == CoreAnnotationNames.DiscriminatorValue);

            if ((discriminatorPropertyAnnotation ?? discriminatorMappingCompleteAnnotation ?? discriminatorValueAnnotation) != null)
            {
                stringBuilder
                    .AppendLine()
                    .Append(builderName)
                    .Append(".")
                    .Append("HasDiscriminator");

                if (discriminatorPropertyAnnotation?.Value != null)
                {
                    var discriminatorProperty = entityType.FindProperty((string)discriminatorPropertyAnnotation.Value);
                    var propertyClrType = FindValueConverter(discriminatorProperty)?.ProviderClrType
                                              .MakeNullable(discriminatorProperty.IsNullable)
                                          ?? discriminatorProperty.ClrType;
                    stringBuilder
                        .Append("<")
                        .Append(Code.Reference(propertyClrType))
                        .Append(">(")
                        .Append(Code.Literal((string)discriminatorPropertyAnnotation.Value))
                        .Append(")");
                }
                else
                {
                    stringBuilder
                        .Append("()");
                }

                if (discriminatorMappingCompleteAnnotation?.Value != null)
                {
                    var value = discriminatorMappingCompleteAnnotation.Value;

                    stringBuilder
                        .Append(".")
                        .Append("IsComplete")
                        .Append("(")
                        .Append(Code.UnknownLiteral(value))
                        .Append(")");
                }

                if (discriminatorValueAnnotation?.Value != null)
                {
                    var value = discriminatorValueAnnotation.Value;
                    var discriminatorProperty = entityType.GetRootType().GetDiscriminatorProperty();
                    if (discriminatorProperty != null)
                    {
                        var valueConverter = FindValueConverter(discriminatorProperty);
                        if (valueConverter != null)
                        {
                            value = valueConverter.ConvertToProvider(value);
                        }
                    }

                    stringBuilder
                        .Append(".")
                        .Append("HasValue")
                        .Append("(")
                        .Append(Code.UnknownLiteral(value))
                        .Append(")");
                }

                stringBuilder.AppendLine(";");

                annotations.Remove(discriminatorPropertyAnnotation);
                annotations.Remove(discriminatorMappingCompleteAnnotation);
                annotations.Remove(discriminatorValueAnnotation);
            }

            var commentAnnotation = annotations.FirstOrDefault(a => a.Name == RelationalAnnotationNames.Comment);

            if (commentAnnotation != null)
            {
                stringBuilder
                    .AppendLine()
                    .Append(builderName)
                    .Append(".")
                    .Append(nameof(RelationalPropertyBuilderExtensions.HasComment))
                    .Append("(")
                    .Append(Code.UnknownLiteral(commentAnnotation.Value))
                    .AppendLine(");");

                annotations.Remove(commentAnnotation);
            }

            IgnoreAnnotations(
                annotations,
                CoreAnnotationNames.NavigationCandidates,
                CoreAnnotationNames.AmbiguousNavigations,
                CoreAnnotationNames.InverseNavigations,
                CoreAnnotationNames.NavigationAccessMode,
                CoreAnnotationNames.PropertyAccessMode,
                CoreAnnotationNames.ChangeTrackingStrategy,
                CoreAnnotationNames.ConstructorBinding,
                CoreAnnotationNames.DefiningQuery,
                CoreAnnotationNames.QueryFilter,
                RelationalAnnotationNames.CheckConstraints,
                RelationalAnnotationNames.TableMappings,
                RelationalAnnotationNames.ViewMappings);

            if (annotations.Count > 0)
            {
                foreach (var annotation in annotations)
                {
                    if (annotation.Value == null)
                    {
                        continue;
                    }

                    stringBuilder
                        .AppendLine()
                        .Append(builderName);

                    GenerateAnnotation(annotation, stringBuilder);

                    stringBuilder
                        .AppendLine(";");
                }
            }
        }

        /// <summary>
        ///     Generates code for <see cref="ICheckConstraint" /> objects.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="entityType"> The entity type. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateCheckConstraints(
            [NotNull] string builderName,
            [NotNull] IEntityType entityType,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(builderName, nameof(builderName));
            Check.NotNull(entityType, nameof(entityType));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            var constraintsForEntity = entityType.GetCheckConstraints();

            foreach (var checkConstraint in constraintsForEntity)
            {
                stringBuilder.AppendLine();

                GenerateCheckConstraint(builderName, checkConstraint, stringBuilder);
            }
        }

        /// <summary>
        ///     Generates code for an <see cref="ICheckConstraint" />.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="checkConstraint"> The check constraint. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateCheckConstraint(
            [NotNull] string builderName,
            [NotNull] ICheckConstraint checkConstraint,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(builderName, nameof(builderName));
            Check.NotNull(checkConstraint, nameof(checkConstraint));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            stringBuilder
                .Append(builderName)
                .Append(".HasCheckConstraint(")
                .Append(Code.Literal(checkConstraint.Name))
                .Append(", ")
                .Append(Code.Literal(checkConstraint.Sql))
                .AppendLine(");");
        }

        /// <summary>
        ///     Generates code for <see cref="IForeignKey" /> objects.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="foreignKeys"> The foreign keys. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateForeignKeys(
            [NotNull] string builderName,
            [NotNull] IEnumerable<IForeignKey> foreignKeys,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(builderName, nameof(builderName));
            Check.NotNull(foreignKeys, nameof(foreignKeys));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            foreach (var foreignKey in foreignKeys)
            {
                stringBuilder.AppendLine();

                GenerateForeignKey(builderName, foreignKey, stringBuilder);
            }
        }

        /// <summary>
        ///     Generates code for an <see cref="IForeignKey" />.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="foreignKey"> The foreign key. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateForeignKey(
            [NotNull] string builderName,
            [NotNull] IForeignKey foreignKey,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(builderName, nameof(builderName));
            Check.NotNull(foreignKey, nameof(foreignKey));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            if (!foreignKey.IsOwnership)
            {
                stringBuilder
                    .Append(builderName)
                    .Append(".HasOne(")
                    .Append(Code.Literal(foreignKey.PrincipalEntityType.Name))
                    .Append(", ")
                    .Append(
                        foreignKey.DependentToPrincipal == null
                            ? Code.UnknownLiteral(null)
                            : Code.Literal(foreignKey.DependentToPrincipal.Name));
            }
            else
            {
                stringBuilder
                    .Append(builderName)
                    .Append(".WithOwner(");

                if (foreignKey.DependentToPrincipal != null)
                {
                    stringBuilder
                        .Append(Code.Literal(foreignKey.DependentToPrincipal.Name));
                }
            }

            stringBuilder
                .Append(")")
                .AppendLine();

            using (stringBuilder.Indent())
            {
                if (foreignKey.IsUnique
                    && !foreignKey.IsOwnership)
                {
                    stringBuilder
                        .Append(".WithOne(");

                    if (foreignKey.PrincipalToDependent != null)
                    {
                        stringBuilder
                            .Append(Code.Literal(foreignKey.PrincipalToDependent.Name));
                    }

                    stringBuilder
                        .AppendLine(")")
                        .Append(".HasForeignKey(")
                        .Append(Code.Literal(foreignKey.DeclaringEntityType.Name))
                        .Append(", ")
                        .Append(string.Join(", ", foreignKey.Properties.Select(p => Code.Literal(p.Name))))
                        .Append(")");

                    GenerateForeignKeyAnnotations(foreignKey, stringBuilder);

                    if (foreignKey.PrincipalKey != foreignKey.PrincipalEntityType.FindPrimaryKey())
                    {
                        stringBuilder
                            .AppendLine()
                            .Append(".HasPrincipalKey(")
                            .Append(Code.Literal(foreignKey.PrincipalEntityType.Name))
                            .Append(", ")
                            .Append(string.Join(", ", foreignKey.PrincipalKey.Properties.Select(p => Code.Literal(p.Name))))
                            .Append(")");
                    }
                }
                else
                {
                    if (!foreignKey.IsOwnership)
                    {
                        stringBuilder
                            .Append(".WithMany(");

                        if (foreignKey.PrincipalToDependent != null)
                        {
                            stringBuilder
                                .Append(Code.Literal(foreignKey.PrincipalToDependent.Name));
                        }

                        stringBuilder
                            .AppendLine(")");
                    }

                    stringBuilder
                        .Append(".HasForeignKey(")
                        .Append(string.Join(", ", foreignKey.Properties.Select(p => Code.Literal(p.Name))))
                        .Append(")");

                    GenerateForeignKeyAnnotations(foreignKey, stringBuilder);

                    if (foreignKey.PrincipalKey != foreignKey.PrincipalEntityType.FindPrimaryKey())
                    {
                        stringBuilder
                            .AppendLine()
                            .Append(".HasPrincipalKey(")
                            .Append(string.Join(", ", foreignKey.PrincipalKey.Properties.Select(p => Code.Literal(p.Name))))
                            .Append(")");
                    }
                }

                if (!foreignKey.IsOwnership)
                {
                    if (foreignKey.DeleteBehavior != DeleteBehavior.ClientSetNull)
                    {
                        stringBuilder
                            .AppendLine()
                            .Append(".OnDelete(")
                            .Append(Code.Literal(foreignKey.DeleteBehavior))
                            .Append(")");
                    }

                    if (foreignKey.IsRequired)
                    {
                        stringBuilder
                            .AppendLine()
                            .Append(".IsRequired()");
                    }
                }
            }

            stringBuilder.AppendLine(";");
        }

        /// <summary>
        ///     Generates code for the annotations on a foreign key.
        /// </summary>
        /// <param name="foreignKey"> The foreign key. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateForeignKeyAnnotations(
            [NotNull] IForeignKey foreignKey, [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(foreignKey, nameof(foreignKey));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            var annotations = foreignKey.GetAnnotations().ToList();

            IgnoreAnnotations(
                annotations,
                RelationalAnnotationNames.ForeignKeyMappings);

            GenerateFluentApiForAnnotation(
                ref annotations,
                RelationalAnnotationNames.Name,
                "HasConstraintName",
                stringBuilder);

            GenerateAnnotations(annotations, stringBuilder);
        }

        /// <summary>
        ///     Removes ignored annotations.
        /// </summary>
        /// <param name="annotations"> The annotations to remove from. </param>
        /// <param name="annotationNames"> The ignored annotation names. </param>
        protected virtual void IgnoreAnnotations(
            [NotNull] IList<IAnnotation> annotations, [NotNull] params string[] annotationNames)
        {
            Check.NotNull(annotations, nameof(annotations));
            Check.NotNull(annotationNames, nameof(annotationNames));

            foreach (var annotationName in annotationNames)
            {
                var annotation = annotations.FirstOrDefault(a => a.Name == annotationName);
                if (annotation != null)
                {
                    annotations.Remove(annotation);
                }
            }
        }

        /// <summary>
        ///     Removes ignored annotations.
        /// </summary>
        /// <param name="annotations"> The annotations to remove from. </param>
        /// <param name="annotationPrefixes"> The ignored annotation prefixes. </param>
        protected virtual void IgnoreAnnotationTypes(
            [NotNull] IList<IAnnotation> annotations, [NotNull] params string[] annotationPrefixes)
        {
            Check.NotNull(annotations, nameof(annotations));
            Check.NotNull(annotationPrefixes, nameof(annotationPrefixes));

            foreach (var ignoreAnnotation in annotations
                .Where(a => annotationPrefixes.Any(pre => a.Name.StartsWith(pre, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                annotations.Remove(ignoreAnnotation);
            }
        }

        /// <summary>
        ///     Generates code for annotations.
        /// </summary>
        /// <param name="annotations"> The annotations. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateAnnotations(
            [NotNull] IReadOnlyList<IAnnotation> annotations, [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(annotations, nameof(annotations));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            foreach (var annotation in annotations)
            {
                if (annotation.Value == null)
                {
                    continue;
                }

                stringBuilder.AppendLine();
                GenerateAnnotation(annotation, stringBuilder);
            }
        }

        /// <summary>
        ///     Generates a Fluent API calls for an annotation.
        /// </summary>
        /// <param name="annotations"> The list of annotations. </param>
        /// <param name="annotationName"> The name of the annotation to generate code for. </param>
        /// <param name="fluentApiMethodName"> The Fluent API method name. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateFluentApiForAnnotation(
            [NotNull] ref List<IAnnotation> annotations,
            [NotNull] string annotationName,
            [NotNull] string fluentApiMethodName,
            [NotNull] IndentedStringBuilder stringBuilder)
            => GenerateFluentApiForAnnotation(
                ref annotations,
                annotationName,
                a => a?.Value,
                fluentApiMethodName,
                stringBuilder);

        /// <summary>
        ///     Generates a Fluent API calls for an annotation.
        /// </summary>
        /// <param name="annotations"> The list of annotations. </param>
        /// <param name="annotationName"> The name of the annotation to generate code for. </param>
        /// <param name="annotationValueFunc"> A delegate to generate the value from the annotation. </param>
        /// <param name="fluentApiMethodName"> The Fluent API method name. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateFluentApiForAnnotation(
            [NotNull] ref List<IAnnotation> annotations,
            [NotNull] string annotationName,
            [CanBeNull] Func<IAnnotation, object> annotationValueFunc,
            [NotNull] string fluentApiMethodName,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            var annotation = annotations.FirstOrDefault(a => a.Name == annotationName);
            var annotationValue = annotationValueFunc?.Invoke(annotation);

            if (annotationValue != null)
            {
                stringBuilder
                    .AppendLine()
                    .Append(".")
                    .Append(fluentApiMethodName)
                    .Append("(")
                    .Append(Code.UnknownLiteral(annotationValue))
                    .Append(")");

                annotations.Remove(annotation);
            }
        }

        /// <summary>
        ///     Generates a Fluent API call for the Precision and Scale annotations.
        /// </summary>
        /// <param name="annotations"> The list of annotations. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateFluentApiForPrecisionAndScale(
            [NotNull] ref List<IAnnotation> annotations,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            var precisionAnnotation = annotations
                .FirstOrDefault(a => a.Name == CoreAnnotationNames.Precision);
            var precisionValue = precisionAnnotation?.Value;

            if (precisionValue != null)
            {
                stringBuilder
                    .AppendLine()
                    .Append(".")
                    .Append(nameof(PropertyBuilder.HasPrecision))
                    .Append("(")
                    .Append(Code.UnknownLiteral(precisionValue));

                var scaleAnnotation = annotations
                    .FirstOrDefault(a => a.Name == CoreAnnotationNames.Scale);
                var scaleValue = (int?)scaleAnnotation?.Value;

                if (scaleValue != null)
                {
                    if (scaleValue != 0)
                    {
                        stringBuilder
                            .Append(", ")
                            .Append(Code.UnknownLiteral(scaleValue));
                    }

                    annotations.Remove(scaleAnnotation);
                }

                stringBuilder.Append(")");

                annotations.Remove(precisionAnnotation);
            }
        }

        /// <summary>
        ///     Generates a Fluent API call for the computed column annotations.
        /// </summary>
        /// <param name="annotations"> The list of annotations. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateFluentApiForComputedColumn(
            [NotNull] ref List<IAnnotation> annotations,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            var sql = annotations
                .FirstOrDefault(a => a.Name == RelationalAnnotationNames.ComputedColumnSql);

            if (sql is null)
            {
                return;
            }

            stringBuilder
                .AppendLine()
                .Append(".")
                .Append(nameof(RelationalPropertyBuilderExtensions.HasComputedColumnSql))
                .Append("(")
                .Append(Code.UnknownLiteral(sql.Value));

            var isStored = annotations
                .FirstOrDefault(a => a.Name == RelationalAnnotationNames.ComputedColumnIsStored);

            if (isStored != null)
            {
                stringBuilder
                    .Append(", ")
                    .Append(Code.UnknownLiteral(isStored.Value));

                annotations.Remove(isStored);
            }

            stringBuilder.Append(")");

            annotations.Remove(sql);
        }

        /// <summary>
        ///     Generates code for an annotation.
        /// </summary>
        /// <param name="annotation"> The annotation. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateAnnotation(
            [NotNull] IAnnotation annotation, [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(annotation, nameof(annotation));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            stringBuilder
                .Append(".HasAnnotation(")
                .Append(Code.Literal(annotation.Name))
                .Append(", ")
                .Append(Code.UnknownLiteral(annotation.Value))
                .Append(")");
        }

        /// <summary>
        ///     Generates code for data seeding.
        /// </summary>
        /// <param name="builderName"> The name of the builder variable. </param>
        /// <param name="properties"> The properties to generate. </param>
        /// <param name="data"> The data to be seeded. </param>
        /// <param name="stringBuilder"> The builder code is added to. </param>
        protected virtual void GenerateData(
            [NotNull] string builderName,
            [NotNull] IEnumerable<IProperty> properties,
            [NotNull] IEnumerable<IDictionary<string, object>> data,
            [NotNull] IndentedStringBuilder stringBuilder)
        {
            Check.NotNull(properties, nameof(properties));
            Check.NotNull(data, nameof(data));
            Check.NotNull(stringBuilder, nameof(stringBuilder));

            var dataList = data.ToList();
            if (dataList.Count == 0)
            {
                return;
            }

            var propertiesToOutput = properties.ToList();

            stringBuilder
                .AppendLine()
                .Append(builderName)
                .Append(".")
                .Append(nameof(EntityTypeBuilder.HasData))
                .AppendLine("(");

            using (stringBuilder.Indent())
            {
                var firstDatum = true;
                foreach (var o in dataList)
                {
                    if (!firstDatum)
                    {
                        stringBuilder.AppendLine(",");
                    }
                    else
                    {
                        firstDatum = false;
                    }

                    stringBuilder
                        .AppendLine("new")
                        .AppendLine("{");

                    using (stringBuilder.Indent())
                    {
                        var firstProperty = true;
                        foreach (var property in propertiesToOutput)
                        {
                            if (o.TryGetValue(property.Name, out var value)
                                && value != null)
                            {
                                if (!firstProperty)
                                {
                                    stringBuilder.AppendLine(",");
                                }
                                else
                                {
                                    firstProperty = false;
                                }

                                stringBuilder
                                    .Append(Code.Identifier(property.Name))
                                    .Append(" = ")
                                    .Append(Code.UnknownLiteral(value));
                            }
                        }

                        stringBuilder.AppendLine();
                    }

                    stringBuilder.Append("}");
                }
            }

            stringBuilder
                .AppendLine(");");
        }
    }
}
