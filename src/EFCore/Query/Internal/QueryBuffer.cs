﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.EntityFrameworkCore.Query.Internal
{
    /// <summary>
    ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class QueryBuffer : IQueryBuffer
    {
        private readonly QueryContextDependencies _dependencies;

        private IWeakReferenceIdentityMap _identityMap0;
        private IWeakReferenceIdentityMap _identityMap1;
        private Dictionary<IKey, IWeakReferenceIdentityMap> _identityMaps;

        private readonly ConditionalWeakTable<object, object> _valueBuffers
            = new ConditionalWeakTable<object, object>();

        private readonly Dictionary<int, IDisposable> _includedCollections
            = new Dictionary<int, IDisposable>(); // IDisposable as IEnumerable/IAsyncEnumerable

        private readonly Dictionary<int, (IDisposable Enumerator, MaterializedAnonymousObject PreviousOriginKey)>
            _correlatedCollectionMetadata
                = new Dictionary<int, (IDisposable, MaterializedAnonymousObject)>();

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public QueryBuffer([NotNull] QueryContextDependencies dependencies)
            => _dependencies = dependencies;

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public virtual object GetEntity(
            IKey key,
            EntityLoadInfo entityLoadInfo,
            bool queryStateManager,
            bool throwOnNullKey)
        {
            if (queryStateManager)
            {
                var entry = _dependencies.StateManager.TryGetEntry(key, entityLoadInfo.ValueBuffer, throwOnNullKey);

                if (entry != null)
                {
                    return entry.Entity;
                }
            }

            var identityMap = GetOrCreateIdentityMap(key);

            var weakReference = identityMap.TryGetEntity(entityLoadInfo.ValueBuffer, throwOnNullKey, out var hasNullKey);

            if (hasNullKey)
            {
                return null;
            }

            if (weakReference == null
                || !weakReference.TryGetTarget(out var entity))
            {
                entity = entityLoadInfo.Materialize();

                if (weakReference != null)
                {
                    weakReference.SetTarget(entity);
                }
                else
                {
                    identityMap.CollectGarbage();
                    identityMap.Add(entityLoadInfo.ValueBuffer, entity);
                }

                _valueBuffers.Add(entity, entityLoadInfo.ForType(entity.GetType()));
            }

            return entity;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public virtual object GetPropertyValue(object entity, IProperty property)
        {
            var entry = _dependencies.StateManager.TryGetEntry(entity);

            if (entry != null
                && entry.EntityState != EntityState.Detached)
            {
                return entry[property];
            }

            _valueBuffers.TryGetValue(entity, out var boxedValueBuffer);

            var valueBuffer = (ValueBuffer)boxedValueBuffer;

            return valueBuffer[property.GetIndex()];
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public virtual void StartTracking(object entity, EntityTrackingInfo entityTrackingInfo)
        {
            if (!_valueBuffers.TryGetValue(entity, out var boxedValueBuffer))
            {
                boxedValueBuffer = ValueBuffer.Empty;
            }

            entityTrackingInfo.StartTracking(_dependencies.StateManager, entity, (ValueBuffer)boxedValueBuffer);
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public virtual void StartTracking(object entity, IEntityType entityType)
        {
            if (!_valueBuffers.TryGetValue(entity, out var boxedValueBuffer))
            {
                boxedValueBuffer = ValueBuffer.Empty;
            }

            _dependencies.StateManager
                .StartTrackingFromQuery(
                    entityType,
                    entity,
                    (ValueBuffer)boxedValueBuffer,
                    handledForeignKeys: null);
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public virtual void IncludeCollection<TEntity, TRelated, TElement>(
            int includeId,
            INavigation navigation,
            INavigation inverseNavigation,
            IEntityType targetEntityType,
            IClrCollectionAccessor clrCollectionAccessor,
            IClrPropertySetter inverseClrPropertySetter,
            bool tracking,
            TEntity entity,
            Func<IEnumerable<TRelated>> relatedEntitiesFactory,
            Func<TEntity, TRelated, bool> joinPredicate)
            where TRelated : TElement
        {
            IDisposable untypedEnumerator = null;
            IEnumerator<TRelated> enumerator = null;

            if (includeId == -1
                || !_includedCollections.TryGetValue(includeId, out untypedEnumerator))
            {
                enumerator = relatedEntitiesFactory().GetEnumerator();

                if (!enumerator.MoveNext())
                {
                    enumerator.Dispose();
                    enumerator = null;
                }

                if (includeId != -1)
                {
                    _includedCollections.Add(includeId, enumerator);
                }
            }

            var collection = (ICollection<TElement>)clrCollectionAccessor.GetOrCreate(entity);

            if (enumerator == null)
            {
                if (untypedEnumerator == null)
                {
                    SetLoaded(tracking, entity, navigation);

                    return;
                }

                enumerator = (IEnumerator<TRelated>)untypedEnumerator;
            }

            IIncludeKeyComparer keyComparer = null;

            if (joinPredicate == null)
            {
                keyComparer = CreateIncludeKeyComparer(entity, navigation);
            }

            while (true)
            {
                bool shouldInclude;

                var current = enumerator.Current;

                if (joinPredicate == null)
                {
                    if (_valueBuffers.TryGetValue(current, out var relatedValueBuffer))
                    {
                        shouldInclude = keyComparer.ShouldInclude((ValueBuffer)relatedValueBuffer);
                    }
                    else
                    {
                        var entry = _dependencies.StateManager.TryGetEntry(current);

                        Debug.Assert(entry != null);

                        shouldInclude = keyComparer.ShouldInclude(entry);
                    }
                }
                else
                {
                    shouldInclude = joinPredicate(entity, current);
                }

                if (shouldInclude)
                {
                    if (tracking)
                    {
                        StartTracking(current, targetEntityType);
                    }
                    else if (!collection.Contains(current))
                    {
                        collection.Add(current);
                    }

                    if (inverseNavigation != null)
                    {
                        Debug.Assert(inverseClrPropertySetter != null);

                        inverseClrPropertySetter.SetClrValue(current, entity);

                        SetLoaded(tracking, entity, current, inverseNavigation);
                    }

                    if (!enumerator.MoveNext())
                    {
                        enumerator.Dispose();

                        if (includeId != -1)
                        {
                            _includedCollections[includeId] = null;
                        }

                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            if (tracking)
            {
                var internalEntityEntry
                    = _dependencies
                        .StateManager
                        .TryGetEntry(entity, navigation.DeclaringEntityType);

                internalEntityEntry.AddRangeToCollectionSnapshot(navigation, (IEnumerable<object>)collection);
                internalEntityEntry.SetIsLoaded(navigation);
            }
            else
            {
                SetIsLoadedNoTracking(entity, navigation);
            }
        }

        private void SetLoaded(bool tracking, object entity, object related, INavigation inverseNavigation)
        {
            var inverseIsCollection = inverseNavigation.IsCollection();

            if (tracking)
            {
                var inverseEntry
                    = _dependencies
                        .StateManager
                        .TryGetEntry(related, inverseNavigation.DeclaringEntityType);

                inverseEntry.SetRelationshipSnapshotValue(inverseNavigation, entity);

                if (!inverseIsCollection)
                {
                    inverseEntry.SetIsLoaded(inverseNavigation);
                }
            }
            else if (!inverseIsCollection)
            {
                SetIsLoadedNoTracking(related, inverseNavigation);
            }
        }

        private void SetLoaded(bool tracking, object entity, INavigation navigation)
        {
            if (tracking)
            {
                _dependencies
                    .StateManager
                    .TryGetEntry(entity, navigation.DeclaringEntityType)
                    .SetIsLoaded(navigation);
            }
            else
            {
                SetIsLoadedNoTracking(entity, navigation);
            }
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public static void SetIsLoadedNoTracking([NotNull] object entity, [NotNull] INavigation navigation)
            => ((ILazyLoader)((PropertyBase)navigation
                        .DeclaringEntityType
                        .GetServiceProperties()
                        .FirstOrDefault(p => p.ClrType == typeof(ILazyLoader)))
                    ?.Getter.GetClrValue(entity))
                ?.SetLoaded(entity, navigation.Name);

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public virtual async Task IncludeCollectionAsync<TEntity, TRelated, TElement>(
            int includeId,
            INavigation navigation,
            INavigation inverseNavigation,
            IEntityType targetEntityType,
            IClrCollectionAccessor clrCollectionAccessor,
            IClrPropertySetter inverseClrPropertySetter,
            bool tracking,
            TEntity entity,
            Func<IAsyncEnumerable<TRelated>> relatedEntitiesFactory,
            Func<TEntity, TRelated, bool> joinPredicate,
            CancellationToken cancellationToken)
            where TRelated : TElement
        {
            IDisposable untypedAsyncEnumerator = null;
            IAsyncEnumerator<TRelated> asyncEnumerator = null;

            if (includeId == -1
                || !_includedCollections.TryGetValue(includeId, out untypedAsyncEnumerator))
            {
                asyncEnumerator = relatedEntitiesFactory().GetEnumerator();

                if (!await asyncEnumerator.MoveNext(cancellationToken))
                {
                    asyncEnumerator.Dispose();
                    asyncEnumerator = null;
                }

                if (includeId != -1)
                {
                    _includedCollections.Add(includeId, asyncEnumerator);
                }
            }

            var collection = (ICollection<TElement>)clrCollectionAccessor.GetOrCreate(entity);

            if (asyncEnumerator == null)
            {
                if (untypedAsyncEnumerator == null)
                {
                    SetLoaded(tracking, entity, navigation);

                    return;
                }

                asyncEnumerator = (IAsyncEnumerator<TRelated>)untypedAsyncEnumerator;
            }

            IIncludeKeyComparer keyComparer = null;

            if (joinPredicate == null)
            {
                keyComparer = CreateIncludeKeyComparer(entity, navigation);
            }

            while (true)
            {
                bool shouldInclude;

                var current = asyncEnumerator.Current;

                if (joinPredicate == null)
                {
                    if (_valueBuffers.TryGetValue(current, out var relatedValueBuffer))
                    {
                        shouldInclude = keyComparer.ShouldInclude((ValueBuffer)relatedValueBuffer);
                    }
                    else
                    {
                        var entry = _dependencies.StateManager.TryGetEntry(current);

                        Debug.Assert(entry != null);

                        shouldInclude = keyComparer.ShouldInclude(entry);
                    }
                }
                else
                {
                    shouldInclude = joinPredicate(entity, current);
                }

                if (shouldInclude)
                {
                    if (tracking)
                    {
                        StartTracking(current, targetEntityType);
                    }
                    else if (!collection.Contains(current))
                    {
                        collection.Add(current);
                    }

                    if (inverseNavigation != null)
                    {
                        Debug.Assert(inverseClrPropertySetter != null);

                        inverseClrPropertySetter.SetClrValue(current, entity);

                        SetLoaded(tracking, entity, current, inverseNavigation);
                    }

                    if (!await asyncEnumerator.MoveNext(cancellationToken))
                    {
                        asyncEnumerator.Dispose();

                        _includedCollections[includeId] = null;

                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            if (tracking)
            {
                var internalEntityEntry
                    = _dependencies
                        .StateManager
                        .TryGetEntry(entity, navigation.DeclaringEntityType);

                internalEntityEntry.AddRangeToCollectionSnapshot(navigation, (IEnumerable<object>)collection);
                internalEntityEntry.SetIsLoaded(navigation);
            }
            else
            {
                SetIsLoadedNoTracking(entity, navigation);
            }
        }

        private IIncludeKeyComparer CreateIncludeKeyComparer(
            object entity,
            INavigation navigation)
        {
            var identityMap = GetOrCreateIdentityMap(navigation.ForeignKey.PrincipalKey);

            if (!_valueBuffers.TryGetValue(entity, out var boxedValueBuffer))
            {
                var entry = _dependencies.StateManager.TryGetEntry(entity);

                Debug.Assert(entry != null);

                return identityMap.CreateIncludeKeyComparer(navigation, entry);
            }

            return identityMap.CreateIncludeKeyComparer(navigation, (ValueBuffer)boxedValueBuffer);
        }

        private IWeakReferenceIdentityMap GetOrCreateIdentityMap(IKey key)
        {
            if (_identityMap0 == null)
            {
                _identityMap0 = key.GetWeakReferenceIdentityMapFactory()();

                return _identityMap0;
            }

            if (_identityMap0.Key == key)
            {
                return _identityMap0;
            }

            if (_identityMap1 == null)
            {
                _identityMap1 = key.GetWeakReferenceIdentityMapFactory()();

                return _identityMap1;
            }

            if (_identityMap1.Key == key)
            {
                return _identityMap1;
            }

            if (_identityMaps == null)
            {
                _identityMaps = new Dictionary<IKey, IWeakReferenceIdentityMap>();
            }

            if (!_identityMaps.TryGetValue(key, out var identityMap))
            {
                identityMap = key.GetWeakReferenceIdentityMapFactory()();

                _identityMaps[key] = identityMap;
            }

            return identityMap;
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public virtual TCollection CorrelateSubquery<TInner, TOut, TCollection>(
            int correlatedCollectionId,
            INavigation navigation,
            Func<INavigation, TCollection> resultCollectionFactory,
            in MaterializedAnonymousObject outerKey,
            bool tracking,
            Func<IEnumerable<Tuple<TInner, MaterializedAnonymousObject, MaterializedAnonymousObject>>> correlatedCollectionFactory,
            Func<MaterializedAnonymousObject, MaterializedAnonymousObject, bool> correlationPredicate)
            where TCollection : ICollection<TOut>
            where TInner : TOut
        {
            IDisposable untypedEnumerator = null;
            IEnumerator<Tuple<TInner, MaterializedAnonymousObject, MaterializedAnonymousObject>> enumerator = null;

            if (!_correlatedCollectionMetadata.TryGetValue(correlatedCollectionId, out var correlatedCollectionMetadataElement))
            {
                enumerator = correlatedCollectionFactory().GetEnumerator();

                if (!enumerator.MoveNext())
                {
                    enumerator.Dispose();
                    enumerator = null;
                }

                correlatedCollectionMetadataElement = (enumerator, default);
                _correlatedCollectionMetadata[correlatedCollectionId] = correlatedCollectionMetadataElement;
            }
            else
            {
                untypedEnumerator = correlatedCollectionMetadataElement.Enumerator;
            }

            var resultCollection = resultCollectionFactory(navigation);

            if (enumerator == null)
            {
                if (untypedEnumerator == null)
                {
                    return resultCollection;
                }

                enumerator = (IEnumerator<Tuple<TInner, MaterializedAnonymousObject, MaterializedAnonymousObject>>)untypedEnumerator;
            }

            while (true)
            {
                if (enumerator == null)
                {
                    return resultCollection;
                }

                var shouldCorrelate = correlationPredicate(outerKey, enumerator.Current.Item2);
                if (shouldCorrelate)
                {
                    // if origin key changed, we got all child elements for a given parent, even if the correlation predicate matches
                    // e.g. orders.Select(o => o.Customer.Addresses) - if there are 10 orders but only 5 customers, we still need 10 collections of addresses, even though some of the addresses belong to same customer
                    if (!correlatedCollectionMetadataElement.PreviousOriginKey.IsDefault()
                        && !enumerator.Current.Item3.Equals(correlatedCollectionMetadataElement.PreviousOriginKey))
                    {
                        correlatedCollectionMetadataElement.PreviousOriginKey = default;
                        _correlatedCollectionMetadata[correlatedCollectionId] = correlatedCollectionMetadataElement;

                        return resultCollection;
                    }

                    var result = enumerator.Current.Item1;

                    correlatedCollectionMetadataElement.PreviousOriginKey = enumerator.Current.Item3;
                    _correlatedCollectionMetadata[correlatedCollectionId] = correlatedCollectionMetadataElement;

                    if (!enumerator.MoveNext())
                    {
                        enumerator.Dispose();
                        enumerator = null;
                        _correlatedCollectionMetadata[correlatedCollectionId] = default;
                    }

                    resultCollection.Add(result);

                    if (tracking)
                    {
                        StartTracking(result, navigation.ForeignKey.DeclaringEntityType);
                    }
                }
                else
                {
                    correlatedCollectionMetadataElement.PreviousOriginKey = default;
                    _correlatedCollectionMetadata[correlatedCollectionId] = correlatedCollectionMetadataElement;

                    return resultCollection;
                }
            }
        }

        /// <summary>
        ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
        ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
        ///     any release. You should only use it directly in your code with extreme caution and knowing that
        ///     doing so can result in application failures when updating to a new Entity Framework Core release.
        /// </summary>
        public virtual async Task<TCollection> CorrelateSubqueryAsync<TInner, TOut, TCollection>(
            int correlatedCollectionId,
            INavigation navigation,
            Func<INavigation, TCollection> resultCollectionFactory,
            MaterializedAnonymousObject outerKey,
            bool tracking,
            Func<IAsyncEnumerable<Tuple<TInner, MaterializedAnonymousObject, MaterializedAnonymousObject>>> correlatedCollectionFactory,
            Func<MaterializedAnonymousObject, MaterializedAnonymousObject, bool> correlationPredicate,
            CancellationToken cancellationToken)
            where TCollection : ICollection<TOut>
            where TInner : TOut
        {
            IDisposable untypedEnumerator = null;
            IAsyncEnumerator<Tuple<TInner, MaterializedAnonymousObject, MaterializedAnonymousObject>> enumerator = null;

            if (!_correlatedCollectionMetadata.TryGetValue(correlatedCollectionId, out var correlatedCollectionMetadataElement))
            {
                enumerator = correlatedCollectionFactory().GetEnumerator();

                if (!await enumerator.MoveNext(cancellationToken))
                {
                    enumerator.Dispose();
                    enumerator = null;
                }

                correlatedCollectionMetadataElement = (enumerator, default);
                _correlatedCollectionMetadata[correlatedCollectionId] = correlatedCollectionMetadataElement;
            }
            else
            {
                untypedEnumerator = correlatedCollectionMetadataElement.Enumerator;
            }

            var resultCollection = resultCollectionFactory(navigation);

            if (enumerator == null)
            {
                if (untypedEnumerator == null)
                {
                    return resultCollection;
                }

                enumerator = (IAsyncEnumerator<Tuple<TInner, MaterializedAnonymousObject, MaterializedAnonymousObject>>)untypedEnumerator;
            }

            while (true)
            {
                if (enumerator == null)
                {
                    return resultCollection;
                }

                var shouldCorrelate = correlationPredicate(outerKey, enumerator.Current.Item2);
                if (shouldCorrelate)
                {
                    // if origin key changed, we got all child elements for a given parent, even if the correlation predicate matches
                    // e.g. orders.Select(o => o.Customer.Addresses) - if there are 10 orders but only 5 customers, we still need 10 collections of addresses, even though some of the addresses belong to same customer
                    if (!correlatedCollectionMetadataElement.PreviousOriginKey.IsDefault()
                        && !enumerator.Current.Item3.Equals(correlatedCollectionMetadataElement.PreviousOriginKey))
                    {
                        correlatedCollectionMetadataElement.PreviousOriginKey = default;
                        _correlatedCollectionMetadata[correlatedCollectionId] = correlatedCollectionMetadataElement;

                        return resultCollection;
                    }

                    var result = enumerator.Current.Item1;

                    correlatedCollectionMetadataElement.PreviousOriginKey = enumerator.Current.Item3;
                    _correlatedCollectionMetadata[correlatedCollectionId] = correlatedCollectionMetadataElement;

                    if (!await enumerator.MoveNext(cancellationToken))
                    {
                        enumerator.Dispose();
                        enumerator = null;
                        _correlatedCollectionMetadata[correlatedCollectionId] = default;
                    }

                    resultCollection.Add(result);

                    if (tracking)
                    {
                        StartTracking(result, navigation.ForeignKey.DeclaringEntityType);
                    }
                }
                else
                {
                    correlatedCollectionMetadataElement.PreviousOriginKey = default;
                    _correlatedCollectionMetadata[correlatedCollectionId] = correlatedCollectionMetadataElement;

                    return resultCollection;
                }
            }
        }

        void IDisposable.Dispose()
        {
            foreach (var kv in _includedCollections)
            {
                kv.Value?.Dispose();
            }

            foreach (var kv in _correlatedCollectionMetadata)
            {
                kv.Value.Enumerator?.Dispose();
            }
        }
    }
}
