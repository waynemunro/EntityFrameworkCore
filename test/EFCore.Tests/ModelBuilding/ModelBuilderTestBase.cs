// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Diagnostics.Internal;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Xunit;

// ReSharper disable InconsistentNaming
namespace Microsoft.EntityFrameworkCore.ModelBuilding
{
    public abstract partial class ModelBuilderTest
    {
        public abstract class ModelBuilderTestBase
        {
            protected void AssertEqual(
                IEnumerable<string> expectedNames,
                IEnumerable<string> actualNames,
                StringComparer stringComparer = null)
            {
                stringComparer ??= StringComparer.Ordinal;
                Assert.Equal(
                    new SortedSet<string>(expectedNames, stringComparer),
                    new SortedSet<string>(actualNames, stringComparer),
                    stringComparer);
            }

            protected void AssertEqual(
                IEnumerable<IProperty> expectedProperties,
                IEnumerable<IProperty> actualProperties,
                PropertyComparer propertyComparer = null)
            {
                propertyComparer ??= new PropertyComparer(compareAnnotations: false);
                Assert.Equal(
                    new SortedSet<IProperty>(expectedProperties, propertyComparer),
                    new SortedSet<IProperty>(actualProperties, propertyComparer),
                    propertyComparer);
            }

            protected void AssertEqual(
                IEnumerable<INavigation> expectedNavigations,
                IEnumerable<INavigation> actualNavigations,
                NavigationComparer navigationComparer = null)
            {
                navigationComparer ??= new NavigationComparer(compareAnnotations: false);
                Assert.Equal(
                    new SortedSet<INavigation>(expectedNavigations, navigationComparer),
                    new SortedSet<INavigation>(actualNavigations, navigationComparer),
                    navigationComparer);
            }

            protected void AssertEqual(
                IEnumerable<IKey> expectedKeys,
                IEnumerable<IKey> actualKeys,
                TestKeyComparer testKeyComparer = null)
            {
                testKeyComparer ??= new TestKeyComparer(compareAnnotations: false);
                Assert.Equal(
                    new SortedSet<IKey>(expectedKeys, testKeyComparer),
                    new SortedSet<IKey>(actualKeys, testKeyComparer),
                    testKeyComparer);
            }

            protected void AssertEqual(
                IEnumerable<IForeignKey> expectedForeignKeys,
                IEnumerable<IForeignKey> actualForeignKeys,
                ForeignKeyStrictComparer foreignKeyComparer = null)
            {
                foreignKeyComparer ??= new ForeignKeyStrictComparer(compareAnnotations: false);
                Assert.Equal(
                    new SortedSet<IForeignKey>(expectedForeignKeys, foreignKeyComparer),
                    new SortedSet<IForeignKey>(actualForeignKeys, foreignKeyComparer),
                    foreignKeyComparer);
            }

            protected void AssertEqual(
                IEnumerable<IIndex> expectedIndexes,
                IEnumerable<IIndex> actualIndexes,
                TestIndexComparer testIndexComparer = null)
            {
                testIndexComparer ??= new TestIndexComparer(compareAnnotations: false);
                Assert.Equal(
                    new SortedSet<IIndex>(expectedIndexes, testIndexComparer),
                    new SortedSet<IIndex>(actualIndexes, testIndexComparer),
                    testIndexComparer);
            }

            protected virtual TestModelBuilder CreateModelBuilder()
                => CreateTestModelBuilder(InMemoryTestHelpers.Instance);

            protected TestModelBuilder HobNobBuilder()
            {
                var builder = CreateModelBuilder();

                builder.Entity<Hob>().HasKey(
                    e => new { e.Id1, e.Id2 });
                builder.Entity<Nob>().HasKey(
                    e => new { e.Id1, e.Id2 });

                return builder;
            }

            protected abstract TestModelBuilder CreateTestModelBuilder(TestHelpers testHelpers);
        }

        public abstract class TestModelBuilder
        {
            protected TestModelBuilder(TestHelpers testHelpers)
            {
                var options = new LoggingOptions();
                options.Initialize(new DbContextOptionsBuilder().EnableSensitiveDataLogging(false).Options);
                ValidationLoggerFactory = new ListLoggerFactory(l => l == DbLoggerCategory.Model.Validation.Name);
                var validationLogger = new DiagnosticsLogger<DbLoggerCategory.Model.Validation>(
                    ValidationLoggerFactory,
                    options,
                    new DiagnosticListener("Fake"),
                    testHelpers.LoggingDefinitions,
                    new NullDbContextLogger());

                ModelLoggerFactory = new ListLoggerFactory(l => l == DbLoggerCategory.Model.Name);
                var modelLogger = new DiagnosticsLogger<DbLoggerCategory.Model>(
                    ModelLoggerFactory,
                    options,
                    new DiagnosticListener("Fake"),
                    testHelpers.LoggingDefinitions,
                    new NullDbContextLogger());

                ModelBuilder = testHelpers.CreateConventionBuilder(modelLogger, validationLogger);
            }

            public virtual IMutableModel Model => ModelBuilder.Model;
            public ModelBuilder ModelBuilder { get; }
            public ListLoggerFactory ValidationLoggerFactory { get; }
            public ListLoggerFactory ModelLoggerFactory { get; }

            public TestModelBuilder HasAnnotation(string annotation, object value)
            {
                ModelBuilder.HasAnnotation(annotation, value);
                return this;
            }

            public abstract TestEntityTypeBuilder<TEntity> Entity<TEntity>()
                where TEntity : class;

            public abstract TestOwnedEntityTypeBuilder<TEntity> Owned<TEntity>()
                where TEntity : class;

            public abstract TestModelBuilder Entity<TEntity>(Action<TestEntityTypeBuilder<TEntity>> buildAction)
                where TEntity : class;

            public abstract TestModelBuilder Ignore<TEntity>()
                where TEntity : class;

            public virtual IModel FinalizeModel() => ModelBuilder.FinalizeModel();

            public virtual string GetDisplayName(Type entityType) => entityType.Name;

            public virtual TestModelBuilder UsePropertyAccessMode(PropertyAccessMode propertyAccessMode)
            {
                ModelBuilder.UsePropertyAccessMode(propertyAccessMode);

                return this;
            }
        }

        public abstract class TestEntityTypeBuilder<TEntity>
            where TEntity : class
        {
            public abstract IMutableEntityType Metadata { get; }
            public abstract TestEntityTypeBuilder<TEntity> HasAnnotation(string annotation, object value);

            public abstract TestEntityTypeBuilder<TEntity> HasBaseType<TBaseEntity>()
                where TBaseEntity : class;

            public abstract TestEntityTypeBuilder<TEntity> HasBaseType(string baseEntityTypeName);
            public abstract TestKeyBuilder<TEntity> HasKey(Expression<Func<TEntity, object>> keyExpression);
            public abstract TestKeyBuilder<TEntity> HasKey(params string[] propertyNames);
            public abstract TestKeyBuilder<TEntity> HasAlternateKey(Expression<Func<TEntity, object>> keyExpression);
            public abstract TestKeyBuilder<TEntity> HasAlternateKey(params string[] propertyNames);
            public abstract TestEntityTypeBuilder<TEntity> HasNoKey();

            public abstract TestPropertyBuilder<TProperty> Property<TProperty>(
                Expression<Func<TEntity, TProperty>> propertyExpression);

            public abstract TestPropertyBuilder<TProperty> Property<TProperty>(string propertyName);
            public abstract TestPropertyBuilder<TProperty> IndexerProperty<TProperty>(string propertyName);

            public abstract TestNavigationBuilder Navigation<TNavigation>(
                Expression<Func<TEntity, TNavigation>> propertyExpression);
            public abstract TestNavigationBuilder Navigation(string propertyName);

            public abstract TestEntityTypeBuilder<TEntity> Ignore(
                Expression<Func<TEntity, object>> propertyExpression);

            public abstract TestEntityTypeBuilder<TEntity> Ignore(string propertyName);

            public abstract TestIndexBuilder<TEntity> HasIndex(Expression<Func<TEntity, object>> indexExpression);
            public abstract TestIndexBuilder<TEntity> HasIndex(params string[] propertyNames);

            public abstract TestOwnedNavigationBuilder<TEntity, TRelatedEntity> OwnsOne<TRelatedEntity>(string navigationName)
                where TRelatedEntity : class;
            public abstract TestEntityTypeBuilder<TEntity> OwnsOne<TRelatedEntity>(
                string navigationName,
                Action<TestOwnedNavigationBuilder<TEntity, TRelatedEntity>> buildAction)
                where TRelatedEntity : class;

            public abstract TestOwnedNavigationBuilder<TEntity, TRelatedEntity> OwnsOne<TRelatedEntity>(
                Expression<Func<TEntity, TRelatedEntity>> navigationExpression)
                where TRelatedEntity : class;

            public abstract TestEntityTypeBuilder<TEntity> OwnsOne<TRelatedEntity>(
                Expression<Func<TEntity, TRelatedEntity>> navigationExpression,
                Action<TestOwnedNavigationBuilder<TEntity, TRelatedEntity>> buildAction)
                where TRelatedEntity : class;

            public abstract TestOwnedNavigationBuilder<TEntity, TRelatedEntity> OwnsMany<TRelatedEntity>(string navigationName)
                where TRelatedEntity : class;
            public abstract TestEntityTypeBuilder<TEntity> OwnsMany<TRelatedEntity>(
                string navigationName,
                Action<TestOwnedNavigationBuilder<TEntity, TRelatedEntity>> buildAction)
                where TRelatedEntity : class;

            public abstract TestOwnedNavigationBuilder<TEntity, TRelatedEntity> OwnsMany<TRelatedEntity>(
                Expression<Func<TEntity, IEnumerable<TRelatedEntity>>> navigationExpression)
                where TRelatedEntity : class;

            public abstract TestEntityTypeBuilder<TEntity> OwnsMany<TRelatedEntity>(
                Expression<Func<TEntity, IEnumerable<TRelatedEntity>>> navigationExpression,
                Action<TestOwnedNavigationBuilder<TEntity, TRelatedEntity>> buildAction)
                where TRelatedEntity : class;

            public abstract TestReferenceNavigationBuilder<TEntity, TRelatedEntity> HasOne<TRelatedEntity>(
                string navigationName)
                where TRelatedEntity : class;

            public abstract TestReferenceNavigationBuilder<TEntity, TRelatedEntity> HasOne<TRelatedEntity>(
                Expression<Func<TEntity, TRelatedEntity>> navigationExpression = null)
                where TRelatedEntity : class;

            public abstract TestCollectionNavigationBuilder<TEntity, TRelatedEntity> HasMany<TRelatedEntity>(
                string navigationName)
                where TRelatedEntity : class;

            public abstract TestCollectionNavigationBuilder<TEntity, TRelatedEntity> HasMany<TRelatedEntity>(
                Expression<Func<TEntity, IEnumerable<TRelatedEntity>>> navigationExpression = null)
                where TRelatedEntity : class;

            public abstract TestEntityTypeBuilder<TEntity> HasQueryFilter(Expression<Func<TEntity, bool>> filter);

            public abstract TestEntityTypeBuilder<TEntity> HasChangeTrackingStrategy(ChangeTrackingStrategy changeTrackingStrategy);

            public abstract TestEntityTypeBuilder<TEntity> UsePropertyAccessMode(PropertyAccessMode propertyAccessMode);

            public abstract DataBuilder<TEntity> HasData(params TEntity[] data);

            public abstract DataBuilder<TEntity> HasData(params object[] data);

            public abstract DataBuilder<TEntity> HasData(IEnumerable<TEntity> data);

            public abstract DataBuilder<TEntity> HasData(IEnumerable<object> data);

            public abstract TestDiscriminatorBuilder<TDiscriminator> HasDiscriminator<TDiscriminator>(
                Expression<Func<TEntity, TDiscriminator>> propertyExpression);

            public abstract TestDiscriminatorBuilder<TDiscriminator> HasDiscriminator<TDiscriminator>(string propertyName);

            public abstract TestEntityTypeBuilder<TEntity> HasNoDiscriminator();
        }

        public abstract class TestDiscriminatorBuilder<TDiscriminator>
        {
            public abstract TestDiscriminatorBuilder<TDiscriminator> IsComplete(bool complete);

            public abstract TestDiscriminatorBuilder<TDiscriminator> HasValue(TDiscriminator value);

            public abstract TestDiscriminatorBuilder<TDiscriminator> HasValue<TEntity>(TDiscriminator value);

            public abstract TestDiscriminatorBuilder<TDiscriminator> HasValue(Type entityType, TDiscriminator value);

            public abstract TestDiscriminatorBuilder<TDiscriminator> HasValue(string entityTypeName, TDiscriminator value);
        }

        public abstract class TestOwnedEntityTypeBuilder<TEntity>
            where TEntity : class
        {
        }

        public abstract class TestKeyBuilder<TEntity>
        {
            public abstract IMutableKey Metadata { get; }

            public abstract TestKeyBuilder<TEntity> HasAnnotation(string annotation, object value);
        }

        public abstract class TestIndexBuilder<TEntity>
        {
            public abstract IMutableIndex Metadata { get; }

            public abstract TestIndexBuilder<TEntity> HasAnnotation(string annotation, object value);
            public abstract TestIndexBuilder<TEntity> IsUnique(bool isUnique = true);
        }

        public abstract class TestPropertyBuilder<TProperty>
        {
            public abstract IMutableProperty Metadata { get; }
            public abstract TestPropertyBuilder<TProperty> HasAnnotation(string annotation, object value);
            public abstract TestPropertyBuilder<TProperty> IsRequired(bool isRequired = true);
            public abstract TestPropertyBuilder<TProperty> HasMaxLength(int maxLength);
            public abstract TestPropertyBuilder<TProperty> IsUnicode(bool unicode = true);
            public abstract TestPropertyBuilder<TProperty> IsRowVersion();
            public abstract TestPropertyBuilder<TProperty> IsConcurrencyToken(bool isConcurrencyToken = true);

            public abstract TestPropertyBuilder<TProperty> ValueGeneratedNever();
            public abstract TestPropertyBuilder<TProperty> ValueGeneratedOnAdd();
            public abstract TestPropertyBuilder<TProperty> ValueGeneratedOnAddOrUpdate();
            public abstract TestPropertyBuilder<TProperty> ValueGeneratedOnUpdate();

            public abstract TestPropertyBuilder<TProperty> HasValueGenerator<TGenerator>()
                where TGenerator : ValueGenerator;

            public abstract TestPropertyBuilder<TProperty> HasValueGenerator(Type valueGeneratorType);
            public abstract TestPropertyBuilder<TProperty> HasValueGenerator(Func<IProperty, IEntityType, ValueGenerator> factory);

            public abstract TestPropertyBuilder<TProperty> HasField(string fieldName);
            public abstract TestPropertyBuilder<TProperty> UsePropertyAccessMode(PropertyAccessMode propertyAccessMode);

            public abstract TestPropertyBuilder<TProperty> HasConversion<TProvider>();
            public abstract TestPropertyBuilder<TProperty> HasConversion(Type providerClrType);

            public abstract TestPropertyBuilder<TProperty> HasConversion<TProvider>(
                Expression<Func<TProperty, TProvider>> convertToProviderExpression,
                Expression<Func<TProvider, TProperty>> convertFromProviderExpression);

            public abstract TestPropertyBuilder<TProperty> HasConversion<TProvider>(ValueConverter<TProperty, TProvider> converter);
            public abstract TestPropertyBuilder<TProperty> HasConversion(ValueConverter converter);
        }

        public abstract class TestNavigationBuilder
        {
            public abstract TestNavigationBuilder HasAnnotation(string annotation, object value);
            public abstract TestNavigationBuilder UsePropertyAccessMode(PropertyAccessMode propertyAccessMode);
        }

        public abstract class TestCollectionNavigationBuilder<TEntity, TRelatedEntity>
            where TEntity : class
            where TRelatedEntity : class
        {
            public abstract TestReferenceCollectionBuilder<TEntity, TRelatedEntity> WithOne(string navigationName);

            public abstract TestReferenceCollectionBuilder<TEntity, TRelatedEntity> WithOne(
                Expression<Func<TRelatedEntity, TEntity>> navigationExpression = null);

            public abstract TestCollectionCollectionBuilder<TRelatedEntity, TEntity> WithMany(string navigationName);

            public abstract TestCollectionCollectionBuilder<TRelatedEntity, TEntity> WithMany(
                Expression<Func<TRelatedEntity, IEnumerable<TEntity>>> navigationExpression);
        }

        public abstract class TestReferenceNavigationBuilder<TEntity, TRelatedEntity>
            where TEntity : class
            where TRelatedEntity : class
        {
            public abstract TestReferenceCollectionBuilder<TRelatedEntity, TEntity> WithMany(string navigationName);

            public abstract TestReferenceCollectionBuilder<TRelatedEntity, TEntity> WithMany(
                Expression<Func<TRelatedEntity, IEnumerable<TEntity>>> navigationExpression = null);

            public abstract TestReferenceReferenceBuilder<TEntity, TRelatedEntity> WithOne(string navigationName);

            public abstract TestReferenceReferenceBuilder<TEntity, TRelatedEntity> WithOne(
                Expression<Func<TRelatedEntity, TEntity>> navigationExpression = null);
        }

        public abstract class TestReferenceCollectionBuilder<TEntity, TRelatedEntity>
            where TEntity : class
            where TRelatedEntity : class
        {
            public abstract IMutableForeignKey Metadata { get; }

            public abstract TestReferenceCollectionBuilder<TEntity, TRelatedEntity> HasForeignKey(
                Expression<Func<TRelatedEntity, object>> foreignKeyExpression);

            public abstract TestReferenceCollectionBuilder<TEntity, TRelatedEntity> HasPrincipalKey(
                Expression<Func<TEntity, object>> keyExpression);

            public abstract TestReferenceCollectionBuilder<TEntity, TRelatedEntity> HasForeignKey(
                params string[] foreignKeyPropertyNames);

            public abstract TestReferenceCollectionBuilder<TEntity, TRelatedEntity> HasPrincipalKey(
                params string[] keyPropertyNames);

            public abstract TestReferenceCollectionBuilder<TEntity, TRelatedEntity> HasAnnotation(
                string annotation, object value);

            public abstract TestReferenceCollectionBuilder<TEntity, TRelatedEntity> IsRequired(bool isRequired = true);

            public abstract TestReferenceCollectionBuilder<TEntity, TRelatedEntity> OnDelete(DeleteBehavior deleteBehavior);
        }

        public abstract class TestReferenceReferenceBuilder<TEntity, TRelatedEntity>
            where TEntity : class
            where TRelatedEntity : class
        {
            public abstract IMutableForeignKey Metadata { get; }

            public abstract TestReferenceReferenceBuilder<TEntity, TRelatedEntity> HasAnnotation(
                string annotation, object value);

            public abstract TestReferenceReferenceBuilder<TEntity, TRelatedEntity> HasForeignKey<TDependentEntity>(
                Expression<Func<TDependentEntity, object>> foreignKeyExpression)
                where TDependentEntity : class;

            public abstract TestReferenceReferenceBuilder<TEntity, TRelatedEntity> HasPrincipalKey<TPrincipalEntity>(
                Expression<Func<TPrincipalEntity, object>> keyExpression)
                where TPrincipalEntity : class;

            public abstract TestReferenceReferenceBuilder<TEntity, TRelatedEntity> HasForeignKey<TDependentEntity>(
                params string[] foreignKeyPropertyNames)
                where TDependentEntity : class;

            public abstract TestReferenceReferenceBuilder<TEntity, TRelatedEntity> HasPrincipalKey<TPrincipalEntity>(
                params string[] keyPropertyNames)
                where TPrincipalEntity : class;

            public abstract TestReferenceReferenceBuilder<TEntity, TRelatedEntity> IsRequired(bool isRequired = true);

            public abstract TestReferenceReferenceBuilder<TEntity, TRelatedEntity> OnDelete(DeleteBehavior deleteBehavior);
        }

        public abstract class TestCollectionCollectionBuilder<TLeftEntity, TRightEntity>
            where TLeftEntity : class
            where TRightEntity : class
        {
            public abstract TestEntityTypeBuilder<TAssociationEntity> UsingEntity<TAssociationEntity>(
                Func<TestEntityTypeBuilder<TAssociationEntity>,
                    TestReferenceCollectionBuilder<TLeftEntity, TAssociationEntity>> configureRight,
                Func<TestEntityTypeBuilder<TAssociationEntity>,
                    TestReferenceCollectionBuilder<TRightEntity, TAssociationEntity>> configureLeft)
                where TAssociationEntity : class;

            public abstract TestEntityTypeBuilder<TLeftEntity> UsingEntity<TAssociationEntity>(
                Func<TestEntityTypeBuilder<TAssociationEntity>,
                    TestReferenceCollectionBuilder<TLeftEntity, TAssociationEntity>> configureRight,
                Func<TestEntityTypeBuilder<TAssociationEntity>,
                    TestReferenceCollectionBuilder<TRightEntity, TAssociationEntity>> configureLeft,
                Action<TestEntityTypeBuilder<TAssociationEntity>> configureAssociation)
                where TAssociationEntity : class;
        }

        public abstract class TestOwnershipBuilder<TEntity, TDependentEntity>
            where TEntity : class
            where TDependentEntity : class
        {
            public abstract IMutableForeignKey Metadata { get; }

            public abstract TestOwnershipBuilder<TEntity, TDependentEntity> HasAnnotation(
                string annotation, object value);

            public abstract TestOwnershipBuilder<TEntity, TDependentEntity> HasForeignKey(
                params string[] foreignKeyPropertyNames);

            public abstract TestOwnershipBuilder<TEntity, TDependentEntity> HasForeignKey(
                Expression<Func<TDependentEntity, object>> foreignKeyExpression);

            public abstract TestOwnershipBuilder<TEntity, TDependentEntity> HasPrincipalKey(
                params string[] keyPropertyNames);

            public abstract TestOwnershipBuilder<TEntity, TDependentEntity> HasPrincipalKey(
                Expression<Func<TEntity, object>> keyExpression);
        }

        public abstract class TestOwnedNavigationBuilder<TEntity, TDependentEntity>
            where TEntity : class
            where TDependentEntity : class
        {
            public abstract IMutableForeignKey Metadata { get; }
            public abstract IMutableEntityType OwnedEntityType { get; }

            public abstract TestOwnedNavigationBuilder<TEntity, TDependentEntity> HasAnnotation(
                string annotation, object value);

            public abstract TestKeyBuilder<TDependentEntity> HasKey(Expression<Func<TDependentEntity, object>> keyExpression);
            public abstract TestKeyBuilder<TDependentEntity> HasKey(params string[] propertyNames);

            public abstract TestPropertyBuilder<TProperty> Property<TProperty>(string propertyName);
            public abstract TestPropertyBuilder<TProperty> IndexerProperty<TProperty>(string propertyName);

            public abstract TestPropertyBuilder<TProperty> Property<TProperty>(
                Expression<Func<TDependentEntity, TProperty>> propertyExpression);

            public abstract TestNavigationBuilder Navigation<TNavigation>(string navigationName);
            public abstract TestNavigationBuilder Navigation<TNavigation>(
                Expression<Func<TDependentEntity, TNavigation>> navigationExpression);

            public abstract TestOwnedNavigationBuilder<TEntity, TDependentEntity> Ignore(string propertyName);

            public abstract TestOwnedNavigationBuilder<TEntity, TDependentEntity> Ignore(
                Expression<Func<TDependentEntity, object>> propertyExpression);

            public abstract TestIndexBuilder<TEntity> HasIndex(params string[] propertyNames);
            public abstract TestIndexBuilder<TEntity> HasIndex(Expression<Func<TDependentEntity, object>> indexExpression);

            public abstract TestOwnershipBuilder<TEntity, TDependentEntity> WithOwner(string ownerReference);
            public abstract TestOwnershipBuilder<TEntity, TDependentEntity> WithOwner(
                Expression<Func<TDependentEntity, TEntity>> referenceExpression = null);

            public abstract TestOwnedNavigationBuilder<TDependentEntity, TNewRelatedEntity> OwnsOne<TNewRelatedEntity>(
                Expression<Func<TDependentEntity, TNewRelatedEntity>> navigationExpression)
                where TNewRelatedEntity : class;

            public abstract TestOwnedNavigationBuilder<TEntity, TDependentEntity> OwnsOne<TNewRelatedEntity>(
                Expression<Func<TDependentEntity, TNewRelatedEntity>> navigationExpression,
                Action<TestOwnedNavigationBuilder<TDependentEntity, TNewRelatedEntity>> buildAction)
                where TNewRelatedEntity : class;

            public abstract TestOwnedNavigationBuilder<TDependentEntity, TNewDependentEntity> OwnsMany<TNewDependentEntity>(
                Expression<Func<TDependentEntity, IEnumerable<TNewDependentEntity>>> navigationExpression)
                where TNewDependentEntity : class;

            public abstract TestOwnedNavigationBuilder<TEntity, TDependentEntity> OwnsMany<TNewDependentEntity>(
                Expression<Func<TDependentEntity, IEnumerable<TNewDependentEntity>>> navigationExpression,
                Action<TestOwnedNavigationBuilder<TDependentEntity, TNewDependentEntity>> buildAction)
                where TNewDependentEntity : class;

            public abstract TestReferenceNavigationBuilder<TDependentEntity, TRelatedEntity> HasOne<TRelatedEntity>(
                Expression<Func<TDependentEntity, TRelatedEntity>> navigationExpression = null)
                where TRelatedEntity : class;

            public abstract TestOwnedNavigationBuilder<TEntity, TDependentEntity> HasChangeTrackingStrategy(
                ChangeTrackingStrategy changeTrackingStrategy);

            public abstract TestOwnedNavigationBuilder<TEntity, TDependentEntity> UsePropertyAccessMode(
                PropertyAccessMode propertyAccessMode);

            public abstract DataBuilder<TDependentEntity> HasData(params TDependentEntity[] data);

            public abstract DataBuilder<TDependentEntity> HasData(params object[] data);

            public abstract DataBuilder<TDependentEntity> HasData(IEnumerable<TDependentEntity> data);

            public abstract DataBuilder<TDependentEntity> HasData(IEnumerable<object> data);
        }
    }
}
