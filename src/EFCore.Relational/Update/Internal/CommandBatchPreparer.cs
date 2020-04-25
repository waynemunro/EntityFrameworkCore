// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Utilities;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.EntityFrameworkCore.Update.Internal
{
    /// <summary>
    ///     <para>
    ///         This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///         the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///         any release. You should only use it directly in your code with extreme caution and knowing that
    ///         doing so can result in application failures when updating to a new Entity Framework Core release.
    ///     </para>
    ///     <para>
    ///         The service lifetime is <see cref="ServiceLifetime.Scoped" />. This means that each
    ///         <see cref="DbContext" /> instance will use its own instance of this service.
    ///         The implementation may depend on other services registered with any lifetime.
    ///         The implementation does not need to be thread-safe.
    ///     </para>
    /// </summary>
    public class CommandBatchPreparer : ICommandBatchPreparer
    {
        private readonly IModificationCommandBatchFactory _modificationCommandBatchFactory;
        private readonly IParameterNameGeneratorFactory _parameterNameGeneratorFactory;
        private readonly IComparer<ModificationCommand> _modificationCommandComparer;
        private readonly IKeyValueIndexFactorySource _keyValueIndexFactorySource;
        private readonly int _minBatchSize;
        private readonly bool _sensitiveLoggingEnabled;

        private IReadOnlyDictionary<(string, string), SharedTableEntryMapFactory<ModificationCommand>>
            _sharedTableEntryMapFactories;

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public CommandBatchPreparer([NotNull] CommandBatchPreparerDependencies dependencies)
        {
            _modificationCommandBatchFactory = dependencies.ModificationCommandBatchFactory;
            _parameterNameGeneratorFactory = dependencies.ParameterNameGeneratorFactory;
            _modificationCommandComparer = dependencies.ModificationCommandComparer;
            _keyValueIndexFactorySource = dependencies.KeyValueIndexFactorySource;
            _minBatchSize =
                dependencies.Options.Extensions.OfType<RelationalOptionsExtension>().FirstOrDefault()?.MinBatchSize
                ?? 4;
            Dependencies = dependencies;

            if (dependencies.LoggingOptions.IsSensitiveDataLoggingEnabled)
            {
                _sensitiveLoggingEnabled = true;
            }
        }

        private CommandBatchPreparerDependencies Dependencies { get; }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public virtual IEnumerable<ModificationCommandBatch> BatchCommands(
            IList<IUpdateEntry> entries,
            IUpdateAdapter updateAdapter)
        {
            var parameterNameGenerator = _parameterNameGeneratorFactory.Create();
            var commands = CreateModificationCommands(entries, updateAdapter, parameterNameGenerator.GenerateNext);
            var sortedCommandSets = TopologicalSort(commands);

            // TODO: Enable batching of dependent commands by passing through the dependency graph
            foreach (var independentCommandSet in sortedCommandSets)
            {
                independentCommandSet.Sort(_modificationCommandComparer);

                var batch = _modificationCommandBatchFactory.Create();
                foreach (var modificationCommand in independentCommandSet)
                {
                    if (!batch.AddCommand(modificationCommand))
                    {
                        if (batch.ModificationCommands.Count == 1
                            || batch.ModificationCommands.Count >= _minBatchSize)
                        {
                            if (batch.ModificationCommands.Count > 1)
                            {
                                Dependencies.UpdateLogger.BatchReadyForExecution(
                                    batch.ModificationCommands.SelectMany(c => c.Entries), batch.ModificationCommands.Count);
                            }

                            yield return batch;
                        }
                        else
                        {
                            Dependencies.UpdateLogger.BatchSmallerThanMinBatchSize(
                                batch.ModificationCommands.SelectMany(c => c.Entries), batch.ModificationCommands.Count, _minBatchSize);

                            foreach (var command in batch.ModificationCommands)
                            {
                                yield return StartNewBatch(parameterNameGenerator, command);
                            }
                        }

                        batch = StartNewBatch(parameterNameGenerator, modificationCommand);
                    }
                }

                if (batch.ModificationCommands.Count == 1
                    || batch.ModificationCommands.Count >= _minBatchSize)
                {
                    if (batch.ModificationCommands.Count > 1)
                    {
                        Dependencies.UpdateLogger.BatchReadyForExecution(
                            batch.ModificationCommands.SelectMany(c => c.Entries), batch.ModificationCommands.Count);
                    }

                    yield return batch;
                }
                else
                {
                    Dependencies.UpdateLogger.BatchSmallerThanMinBatchSize(
                        batch.ModificationCommands.SelectMany(c => c.Entries), batch.ModificationCommands.Count, _minBatchSize);

                    foreach (var command in batch.ModificationCommands)
                    {
                        yield return StartNewBatch(parameterNameGenerator, command);
                    }
                }
            }
        }

        private ModificationCommandBatch StartNewBatch(
            ParameterNameGenerator parameterNameGenerator, ModificationCommand modificationCommand)
        {
            parameterNameGenerator.Reset();
            var batch = _modificationCommandBatchFactory.Create();
            batch.AddCommand(modificationCommand);
            return batch;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected virtual IEnumerable<ModificationCommand> CreateModificationCommands(
            [NotNull] IList<IUpdateEntry> entries,
            [NotNull] IUpdateAdapter updateAdapter,
            [NotNull] Func<string> generateParameterName)
        {
            var commands = new List<ModificationCommand>();
            if (_sharedTableEntryMapFactories == null)
            {
                _sharedTableEntryMapFactories = SharedTableEntryMap<ModificationCommand>
                    .CreateSharedTableEntryMapFactories(updateAdapter.Model, updateAdapter);
            }

            Dictionary<(string Name, string Schema), SharedTableEntryMap<ModificationCommand>> sharedTablesCommandsMap =
                null;
            foreach (var entry in entries)
            {
                if (entry.SharedIdentityEntry != null
                    && entry.EntityState == EntityState.Deleted)
                {
                    continue;
                }

                var mappingFound = false;
                foreach (var mapping in entry.EntityType.GetTableMappings())
                {
                    mappingFound = true;
                    var table = mapping.Table;
                    var tableKey = (table.Name, table.Schema);

                    ModificationCommand command;
                    var isMainEntry = true;
                    if (_sharedTableEntryMapFactories.TryGetValue(tableKey, out var commandIdentityMapFactory))
                    {
                        if (sharedTablesCommandsMap == null)
                        {
                            sharedTablesCommandsMap =
                                new Dictionary<(string, string), SharedTableEntryMap<ModificationCommand>>();
                        }

                        if (!sharedTablesCommandsMap.TryGetValue(tableKey, out var sharedCommandsMap))
                        {
                            sharedCommandsMap = commandIdentityMapFactory(
                                (n, s, c) => new ModificationCommand(
                                    n, s, generateParameterName, _sensitiveLoggingEnabled, c));
                            sharedTablesCommandsMap.Add(tableKey, sharedCommandsMap);
                        }

                        command = sharedCommandsMap.GetOrAddValue(entry);
                        isMainEntry = sharedCommandsMap.IsMainEntry(entry);
                    }
                    else
                    {
                        command = new ModificationCommand(
                            table.Name, table.Schema, generateParameterName, _sensitiveLoggingEnabled, comparer: null);
                    }

                    command.AddEntry(entry, isMainEntry);
                    commands.Add(command);
                }

                if (!mappingFound)
                {
                    throw new InvalidOperationException(RelationalStrings.ReadonlyEntitySaved(entry.EntityType.DisplayName()));
                }
            }

            if (sharedTablesCommandsMap != null)
            {
                AddUnchangedSharingEntries(sharedTablesCommandsMap.Values, entries);
            }

            return commands.Where(
                c => c.EntityState != EntityState.Modified
                    || c.ColumnModifications.Any(m => m.IsWrite));
        }

        private void AddUnchangedSharingEntries(
            IEnumerable<SharedTableEntryMap<ModificationCommand>> sharedTablesCommands,
            IList<IUpdateEntry> entries)
        {
            foreach (var sharedCommandsMap in sharedTablesCommands)
            {
                foreach (var command in sharedCommandsMap.Values)
                {
                    if (command.EntityState != EntityState.Modified)
                    {
                        continue;
                    }

                    foreach (var entry in sharedCommandsMap.GetAllEntries(command.Entries[0]))
                    {
                        if (entry.EntityState != EntityState.Unchanged)
                        {
                            continue;
                        }

                        entry.EntityState = EntityState.Modified;

                        command.AddEntry(entry, sharedCommandsMap.IsMainEntry(entry));
                        entries.Add(entry);
                    }
                }
            }
        }

        // To avoid violating store constraints the modification commands must be sorted
        // according to these rules:
        //
        // 1. Commands adding rows or modifying the candidate key values (when supported) must precede
        //     commands adding or modifying rows that will be referencing the former
        // 2. Commands deleting rows or modifying the foreign key values must precede
        //     commands deleting rows or modifying the candidate key values (when supported) of rows
        //     that are currently being referenced by the former
        // 3. Commands deleting rows or modifying the foreign key values must precede
        //     commands adding or modifying the foreign key values to the same values
        //     if foreign key is unique
        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        protected virtual IReadOnlyList<List<ModificationCommand>> TopologicalSort([NotNull] IEnumerable<ModificationCommand> commands)
        {
            var modificationCommandGraph = new Multigraph<ModificationCommand, IAnnotatable>();
            modificationCommandGraph.AddVertices(commands);

            // The predecessors map allows to populate the graph in linear time
            var predecessorsMap = CreateKeyValuePredecessorMap(modificationCommandGraph);
            AddForeignKeyEdges(modificationCommandGraph, predecessorsMap);

            AddUniqueValueEdges(modificationCommandGraph);

            return modificationCommandGraph.BatchingTopologicalSort(FormatCycle);
        }

        private string FormatCycle(IReadOnlyList<Tuple<ModificationCommand, ModificationCommand, IEnumerable<IAnnotatable>>> data)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < data.Count; i++)
            {
                var edge = data[i];
                Format(edge.Item1, builder);

                switch (edge.Item3.First())
                {
                    case IForeignKey foreignKey:
                        Format(foreignKey, edge.Item1, edge.Item2, builder);
                        break;
                    case IIndex index:
                        Format(index, edge.Item1, edge.Item2, builder);
                        break;
                }

                if (i == data.Count - 1)
                {
                    Format(edge.Item2, builder);
                }
            }

            return builder.ToString();
        }

        private void Format(ModificationCommand command, StringBuilder builder)
        {
            var entry = command.Entries.First();
            var entityType = entry.EntityType;
            builder.Append(entityType.DisplayName());
            if (_sensitiveLoggingEnabled)
            {
                builder.Append(" { ");
                var properties = entityType.FindPrimaryKey().Properties;
                for (var i = 0; i < properties.Count; i++)
                {
                    var keyProperty = properties[i];
                    builder.Append("'");
                    builder.Append(keyProperty.Name);
                    builder.Append("': ");
                    builder.Append(entry.GetCurrentValue(keyProperty));

                    if (i != properties.Count - 1)
                    {
                        builder.Append(", ");
                    }
                }

                builder.Append(" } ");
            }
            else
            {
                builder.Append(" ");
            }

            builder.Append("[");
            builder.Append(entry.EntityState);
            builder.Append("]");
        }

        private void Format(IForeignKey foreignKey, ModificationCommand source, ModificationCommand target, StringBuilder builder)
        {
            var reverseDependency = !source.Entries.Any(e => foreignKey.DeclaringEntityType.IsAssignableFrom(e.EntityType));
            if (reverseDependency)
            {
                builder.Append(" <-");
            }

            builder.Append(" ");
            if (foreignKey.DependentToPrincipal != null
                || foreignKey.PrincipalToDependent != null)
            {
                if (!reverseDependency
                    && foreignKey.DependentToPrincipal != null)
                {
                    builder.Append(foreignKey.DependentToPrincipal.Name);
                    builder.Append(" ");
                }

                if (foreignKey.PrincipalToDependent != null)
                {
                    builder.Append(foreignKey.PrincipalToDependent.Name);
                    builder.Append(" ");
                }

                if (reverseDependency
                    && foreignKey.DependentToPrincipal != null)
                {
                    builder.Append(foreignKey.DependentToPrincipal.Name);
                    builder.Append(" ");
                }
            }
            else
            {
                builder.Append("ForeignKey ");
            }

            var dependentCommand = reverseDependency ? target : source;
            var dependentEntry = dependentCommand.Entries.First(e => foreignKey.DeclaringEntityType.IsAssignableFrom(e.EntityType));
            builder.Append("{ ");
            for (var i = 0; i < foreignKey.Properties.Count; i++)
            {
                var property = foreignKey.Properties[i];
                builder.Append("'");
                builder.Append(property.Name);
                builder.Append("'");
                if (_sensitiveLoggingEnabled)
                {
                    builder.Append(": ");
                    builder.Append(dependentEntry.GetCurrentValue(property));
                }

                if (i != foreignKey.Properties.Count - 1)
                {
                    builder.Append(", ");
                }
            }

            builder.Append(" } ");

            if (!reverseDependency)
            {
                builder.Append("<- ");
            }
        }

        private void Format(IIndex index, ModificationCommand source, ModificationCommand target, StringBuilder builder)
        {
            var reverseDependency = source.EntityState != EntityState.Deleted;
            if (reverseDependency)
            {
                builder.Append(" <-");
            }

            builder.Append(" Index ");

            var dependentCommand = reverseDependency ? target : source;
            var dependentEntry = dependentCommand.Entries.First(e => index.DeclaringEntityType.IsAssignableFrom(e.EntityType));
            builder.Append("{ ");
            for (var i = 0; i < index.Properties.Count; i++)
            {
                var property = index.Properties[i];
                builder.Append("'");
                builder.Append(property.Name);
                builder.Append("'");
                if (_sensitiveLoggingEnabled)
                {
                    builder.Append(": ");
                    builder.Append(dependentEntry.GetCurrentValue(property));
                }

                if (i != index.Properties.Count - 1)
                {
                    builder.Append(", ");
                }
            }

            builder.Append(" } ");

            if (!reverseDependency)
            {
                builder.Append("<- ");
            }
        }

        // Builds a map from foreign key values to list of modification commands, with an entry for every command
        // that may need to precede some other command involving that foreign key value.
        private Dictionary<IKeyValueIndex, List<ModificationCommand>> CreateKeyValuePredecessorMap(
            Multigraph<ModificationCommand, IAnnotatable> commandGraph)
        {
            var predecessorsMap = new Dictionary<IKeyValueIndex, List<ModificationCommand>>();
            foreach (var command in commandGraph.Vertices)
            {
                var columnModifications = command.ColumnModifications;
                if (command.EntityState == EntityState.Modified
                    || command.EntityState == EntityState.Added)
                {
                    // ReSharper disable once ForCanBeConvertedToForeach
                    for (var i = 0; i < command.Entries.Count; i++)
                    {
                        var entry = command.Entries[i];
                        foreach (var foreignKey in entry.EntityType.GetReferencingForeignKeys())
                        {
                            var constraints = foreignKey.GetMappedConstraints()
                                .Where(c => c.PrincipalTable.Name == command.TableName && c.PrincipalTable.Schema == command.Schema);
                            var candidateKeyValueColumnModifications = columnModifications.Where(
                                cm => (cm.IsWrite || cm.IsRead) && foreignKey.PrincipalKey.Properties.Contains(cm.Property));

                            if (!constraints.Any()
                                || (command.EntityState == EntityState.Modified
                                    && !candidateKeyValueColumnModifications.Any()))
                            {
                                continue;
                            }

                            var principalKeyValue = _keyValueIndexFactorySource
                                .GetKeyValueIndexFactory(foreignKey.PrincipalKey)
                                .CreatePrincipalKeyValue((InternalEntityEntry)entry, foreignKey);

                            if (principalKeyValue != null)
                            {
                                if (!predecessorsMap.TryGetValue(principalKeyValue, out var predecessorCommands))
                                {
                                    predecessorCommands = new List<ModificationCommand>();
                                    predecessorsMap.Add(principalKeyValue, predecessorCommands);
                                }

                                predecessorCommands.Add(command);
                            }
                        }
                    }
                }

                if (command.EntityState == EntityState.Modified
                    || command.EntityState == EntityState.Deleted)
                {
                    foreach (var entry in command.Entries)
                    {
                        foreach (var foreignKey in entry.EntityType.GetForeignKeys())
                        {
                            var constraints = foreignKey.GetMappedConstraints();
                            var foreignKeyValueColumnModifications = columnModifications.Where(
                                cm => (cm.IsWrite || cm.IsRead) && foreignKey.Properties.Contains(cm.Property));

                            if (!constraints.Any()
                                || (command.EntityState == EntityState.Modified
                                    && !foreignKeyValueColumnModifications.Any()))
                            {
                                continue;
                            }

                            var dependentKeyValue = _keyValueIndexFactorySource
                                .GetKeyValueIndexFactory(foreignKey.PrincipalKey)
                                .CreateDependentKeyValueFromOriginalValues((InternalEntityEntry)entry, foreignKey);

                            if (dependentKeyValue != null)
                            {
                                if (!predecessorsMap.TryGetValue(dependentKeyValue, out var predecessorCommands))
                                {
                                    predecessorCommands = new List<ModificationCommand>();
                                    predecessorsMap.Add(dependentKeyValue, predecessorCommands);
                                }

                                predecessorCommands.Add(command);
                            }
                        }
                    }
                }
            }

            return predecessorsMap;
        }

        private void AddForeignKeyEdges(
            Multigraph<ModificationCommand, IAnnotatable> commandGraph,
            Dictionary<IKeyValueIndex, List<ModificationCommand>> predecessorsMap)
        {
            foreach (var command in commandGraph.Vertices)
            {
                switch (command.EntityState)
                {
                    case EntityState.Modified:
                    case EntityState.Added:
                        // ReSharper disable once ForCanBeConvertedToForeach
                        for (var entryIndex = 0; entryIndex < command.Entries.Count; entryIndex++)
                        {
                            var entry = command.Entries[entryIndex];
                            foreach (var foreignKey in entry.EntityType.GetForeignKeys())
                            {
                                var constraints = foreignKey.GetMappedConstraints();

                                var foreignKeyValueColumnModifications = command.ColumnModifications.Where(
                                    cm => (cm.IsWrite || cm.IsRead) && foreignKey.Properties.Contains(cm.Property));

                                if (!constraints.Any()
                                    || (command.EntityState == EntityState.Modified
                                        && !foreignKeyValueColumnModifications.Any()))
                                {
                                    continue;
                                }

                                var dependentKeyValue = _keyValueIndexFactorySource
                                    .GetKeyValueIndexFactory(foreignKey.PrincipalKey)
                                    .CreateDependentKeyValue((InternalEntityEntry)entry, foreignKey);
                                if (dependentKeyValue == null)
                                {
                                    continue;
                                }

                                AddMatchingPredecessorEdge(
                                    predecessorsMap, dependentKeyValue, commandGraph, command, foreignKey);
                            }
                        }

                        break;
                    case EntityState.Deleted:
                        // ReSharper disable once ForCanBeConvertedToForeach
                        for (var entryIndex = 0; entryIndex < command.Entries.Count; entryIndex++)
                        {
                            var entry = command.Entries[entryIndex];
                            foreach (var foreignKey in entry.EntityType.GetReferencingForeignKeys())
                            {
                                var constraints = foreignKey.GetMappedConstraints()
                                    .Where(c => c.PrincipalTable.Name == command.TableName && c.PrincipalTable.Schema == command.Schema);
                                if (!constraints.Any())
                                {
                                    continue;
                                }

                                var principalKeyValue = _keyValueIndexFactorySource
                                    .GetKeyValueIndexFactory(foreignKey.PrincipalKey)
                                    .CreatePrincipalKeyValueFromOriginalValues((InternalEntityEntry)entry, foreignKey);
                                if (principalKeyValue != null)
                                {
                                    AddMatchingPredecessorEdge(
                                        predecessorsMap, principalKeyValue, commandGraph, command, foreignKey);
                                }
                            }
                        }

                        break;
                }
            }
        }

        private static void AddMatchingPredecessorEdge(
            Dictionary<IKeyValueIndex, List<ModificationCommand>> predecessorsMap,
            IKeyValueIndex dependentKeyValue,
            Multigraph<ModificationCommand, IAnnotatable> commandGraph,
            ModificationCommand command,
            IForeignKey foreignKey)
        {
            if (predecessorsMap.TryGetValue(dependentKeyValue, out var predecessorCommands))
            {
                foreach (var predecessor in predecessorCommands)
                {
                    if (predecessor != command)
                    {
                        commandGraph.AddEdge(predecessor, command, foreignKey);
                    }
                }
            }
        }

        private void AddUniqueValueEdges(Multigraph<ModificationCommand, IAnnotatable> commandGraph)
        {
            Dictionary<IIndex, Dictionary<object[], ModificationCommand>> predecessorsMap = null;
            foreach (var command in commandGraph.Vertices)
            {
                if (command.EntityState != EntityState.Modified
                    && command.EntityState != EntityState.Deleted)
                {
                    continue;
                }

                for (var entryIndex = 0; entryIndex < command.Entries.Count; entryIndex++)
                {
                    var entry = command.Entries[entryIndex];
                    foreach (var index in entry.EntityType.GetIndexes().Where(i => i.IsUnique && i.GetMappedTableIndexes().Any()))
                    {
                        if (command.EntityState != EntityState.Deleted)
                        {
                            var anyIndexColumnModifications = false;
                            // ReSharper disable once ForCanBeConvertedToForeach
                            // ReSharper disable once LoopCanBeConvertedToQuery
                            for (var indexIndex = 0; indexIndex < command.ColumnModifications.Count; indexIndex++)
                            {
                                var cm = command.ColumnModifications[indexIndex];
                                if (index.Properties.Contains(cm.Property)
                                    && (cm.IsWrite || cm.IsRead))
                                {
                                    anyIndexColumnModifications = true;
                                    break;
                                }
                            }

                            if (!anyIndexColumnModifications)
                            {
                                continue;
                            }
                        }

                        var valueFactory = index.GetNullableValueFactory<object[]>();
                        if (valueFactory.TryCreateFromOriginalValues(entry, out var indexValue))
                        {
                            predecessorsMap ??= new Dictionary<IIndex, Dictionary<object[], ModificationCommand>>();
                            if (!predecessorsMap.TryGetValue(index, out var predecessorCommands))
                            {
                                predecessorCommands = new Dictionary<object[], ModificationCommand>(valueFactory.EqualityComparer);
                                predecessorsMap.Add(index, predecessorCommands);
                            }

                            if (!predecessorCommands.ContainsKey(indexValue))
                            {
                                predecessorCommands.Add(indexValue, command);
                            }
                        }
                    }
                }
            }

            if (predecessorsMap == null)
            {
                return;
            }

            foreach (var command in commandGraph.Vertices)
            {
                if (command.EntityState == EntityState.Modified
                    || command.EntityState == EntityState.Added)
                {
                    foreach (var entry in command.Entries)
                    {
                        foreach (var index in entry.EntityType.GetIndexes().Where(i => i.IsUnique && i.GetMappedTableIndexes().Any()))
                        {
                            var indexColumnModifications = command.ColumnModifications
                                .Where(cm => index.Properties.Contains(cm.Property) && cm.IsWrite);

                            if (command.EntityState != EntityState.Added
                                && !indexColumnModifications.Any())
                            {
                                continue;
                            }

                            var valueFactory = index.GetNullableValueFactory<object[]>();
                            if (valueFactory.TryCreateFromCurrentValues(entry, out var indexValue)
                                && predecessorsMap.TryGetValue(index, out var predecessorCommands)
                                && predecessorCommands.TryGetValue(indexValue, out var predecessor)
                                && predecessor != command)
                            {
                                commandGraph.AddEdge(predecessor, command, index);
                            }
                        }
                    }
                }
            }
        }
    }
}
