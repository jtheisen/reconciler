using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using System.Reflection;

#if EF6
using System.Data.Entity;
using System.Data.Entity.Core;
#endif

#if EFCORE
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore.ChangeTracking;
#endif

namespace MonkeyBusters.Reconciliation.Internal
{
    internal class KeyedEntity<E>
    {
        private readonly EntityKey key;
        private readonly E entity;

        public EntityKey Key => key;
        public E Entity => entity;

        public KeyedEntity(EntityKey key, E entity)
        {
            this.key = key;
            this.entity = entity;
        }
    }

    enum ReconcileStep
    {
        Load,
        Modify,
        Clone
    }

    class ReconcilingContext
    {
        public Boolean ReverseInteration { get; set; }

        public IEnumerable<T> Iterate<T>(IReadOnlyCollection<T> collection)
        {
            if (ReverseInteration)
            {
                return collection.Reverse();
            }
            else
            {
                return collection;
            }
        }
    }

    /// <summary>
    /// Reconcilers are implemented for scalar and collection navigational properties
    /// and are responsible for syncing them by inserting, deleting and updating
    /// the entities in one of such properties.
    /// </summary>
    abstract class Reconciler<E> where E : class
    {
        public abstract IQueryable<E> AugmentInclude(IQueryable<E> query);

        public abstract Task ReconcileAsync(DbContext db, ReconcilingContext rctx, ReconcileStep step, E attachedEntity, E templateEntity, Int32 nesting);

        public abstract Task LoadAsync(DbContext db, E attachedEntity);

        public abstract void ForEach(DbContext db, E entity, Action<DbContext, Object> action);

        public abstract void Normalize(DbContext db, E entity);

        public abstract void Reset();

        public abstract void RemoveOrphans(DbContext db);
    }

    class EntityReconciler<E, F> : Reconciler<E>
        where E : class
        where F : class
    {
        private readonly Expression<Func<E, F>> selectorExpression;
        private readonly PropertyInfo propertyInfo;
        private readonly Func<E, F> selector;
        private readonly IExtent<F> extent;
        private readonly Boolean removeOrphans;

        public EntityReconciler(Expression<Func<E, F>> selector, IExtent<F> extent, Boolean removeOrphans)
        {
            this.selectorExpression = selector;
            this.propertyInfo = Properties.GetPropertyInfo(selector);
            this.selector = selector.Compile();
            this.extent = extent;
            this.removeOrphans = removeOrphans;
        }

        public override String ToString() => $"-{propertyInfo.Name}";

        public override IQueryable<E> AugmentInclude(IQueryable<E> query)
            => query.Include(selectorExpression);

        public override async Task ReconcileAsync(DbContext db, ReconcilingContext rctx, ReconcileStep step, E attachedBaseEntity, E templateBaseEntity, Int32 nesting)
        {
            var attachedEntity = attachedBaseEntity != null ? selector(attachedBaseEntity) : null;
            var templateEntity = templateBaseEntity != null ? selector(templateBaseEntity) : null;

            var attachedEntityKey = db.GetEntityKey(attachedEntity);
            var templateEntityKey = db.GetEntityKey(templateEntity);

            if (!(templateEntityKey is null) && !(attachedEntityKey is null) && templateEntityKey == attachedEntityKey)
            {
                await db.ReconcileCoreAsync(rctx, step, null, attachedEntity, templateEntity, extent, nesting);
            }
            else
            {
                if (attachedEntity != null && removeOrphans)
                {
                    if (attachedEntityKey is null) throw new Exception("Found attached entity to delete without key");

                    // This is also just for the deletion and so does not need to execute if removeOrphans is false
                    await db.ReconcileCoreAsync(rctx, step, null, attachedEntity, null, extent, nesting);

                    if (step == ReconcileStep.Modify)
                    {
                        // "removeOrphans" means we always remove the entity - if the user wants to move it,
                        // he should use .WithShared.
                        db.RemoveEntity(attachedEntity);
                    }
                }

                if (templateEntity != null)
                {
                    void Link(F entityToLink)
                    {
                        propertyInfo.SetValue(attachedBaseEntity, entityToLink);

                        db.LogTrace(nesting, "after setting value");
                    }

                    // The attached entity isn't passed as, if it exists, is the one we deleted above.
                    await db.ReconcileCoreAsync(rctx, step, Link, null, templateEntity, extent, nesting);
                }
            }
        }

        public override async Task LoadAsync(DbContext db, E attachedBaseEntity)
        {
            if (extent == null) return;
            var attachedEntity = selector(attachedBaseEntity);
            await db.LoadExtentAsync(attachedEntity, extent);
        }

        public override void ForEach(DbContext db, E baseEntity, Action<DbContext, Object> action)
        {
            var entity = selector(baseEntity);

            db.ForEach(entity, action, extent);
        }

        public override void Normalize(DbContext db, E entity) => db.Normalize(selector(entity), extent);

        public override void Reset()
        {
            extent.Reset();
        }

        public override void RemoveOrphans(DbContext db)
        {
            extent.RemoveOrphans(db);
        }
    }

    class CollectionReconciler<E, F> : Reconciler<E>
        where E : class
        where F : class
    {
        private readonly Expression<Func<E, ICollection<F>>> selectorExpression;
        private readonly Func<E, ICollection<F>> selector;
        private readonly IExtent<F> extent;

        public CollectionReconciler(Expression<Func<E, ICollection<F>>> selector, IExtent<F> extent)
        {
            this.selectorExpression = selector;
            this.selector = selector.Compile();
            this.extent = extent;
        }

        public override String ToString() => $"*{Properties.GetPropertyInfo(selectorExpression).Name}";

        public override IQueryable<E> AugmentInclude(IQueryable<E> query)
            => query.Include(this.selectorExpression);

        public override async Task ReconcileAsync(DbContext db, ReconcilingContext rctx, ReconcileStep step, E attachedEntity, E templateEntity, Int32 nesting)
        {
            var attachedCollection = attachedEntity != null ?
                selector(attachedEntity)
                : new List<F>();
            var templateCollection = templateEntity != null
                ? selector(templateEntity).ToArray()
                : Enumerable.Empty<F>();

            var attachedKeyedEntities = attachedCollection
                .Select(e => new KeyedEntity<F>(db.GetEntityKey(e), e))
                .ToArray();
            
            var templateKeyedEntities = templateCollection
                .Select(e => new KeyedEntity<F>(db.GetEntityKey(e), e))
                .ToArray();

            if (step == ReconcileStep.Load)
            {
                foreach (var e in attachedKeyedEntities)
                {
                    var key = e.Key;

                    if (key is null) continue;

                    TrackOrphan(key, e.Entity, 0);
                }
            }

            var toRemove = (
                from o in attachedKeyedEntities
                where o.Key != null
                join n in templateKeyedEntities on o.Key equals n.Key into news
                where news.Count() == 0 && db.Entry(o.Entity)?.State != EntityState.Deleted
                select o
            ).ToArray();

            var toAdd = (
                from n in templateKeyedEntities
                join o in attachedKeyedEntities on n.Key equals o.Key into olds
                where olds.Count() == 0
                select n
            ).ToArray();

            var toUpdate = (
                from o in attachedKeyedEntities
                join n in templateKeyedEntities on o.Key equals n.Key
                select new
                {
                    AttachedEntity = o.Entity,
                    TemplateEntity = n.Entity
                }
            ).ToArray();

            foreach (var ke in rctx.Iterate(toRemove))
            {
                var e = ke.Entity;
                var key = ke.Key;

                if (db.Entry(e)?.State == EntityState.Deleted) continue;

                await db.ReconcileCoreAsync(rctx, step, null, e, null, extent, nesting);

                if (step == ReconcileStep.Modify)
                {
                    // EF Core can track whether the entity needs removal by virtue of being
                    // orphaned, hence it's enough to just remove it from the parent collection.
                    attachedCollection.Remove(e);

                    TrackOrphan(key, e, -1);
                }
            }

            foreach (var ke in rctx.Iterate(toAdd))
            {
                var e = ke.Entity;
                var key = ke.Key;

                void Link(F f)
                {
                    // Ususally, the entity was already put into the right collection by EF on
                    // adding the entity to the context with consistent foreign keys. The explicit
                    // adding to the collection only needs to happen when that is not the case.
                    if (!attachedCollection.Contains(f))
                    {
                        attachedCollection.Add(f);
                    }
                }

                F attachedE = null;

                if (step == ReconcileStep.Modify && !(key is null) && orphans.TryGetValue(key, out var entry))
                {
                    // In the modifying step, an entity with the same key must have already been loaded
                    // an can now be picked up. We need to do this because we must not re-add such an
                    // entity and hence need to set the attached entity.

                    // -- Note for the condition around the next line:
                    // We found an entity with the same key. We should use it as the attached entity.
                    // However, if the net count is positive, it means we already inserted one with
                    // the same key! This can only happen on EF6, where database-generated keys are
                    // non-null and compare equal to each other. In this case, we're adding multiple
                    // (different) entities which will get their own keys later. If the netCount is
                    // not positive, this means we've loaded an entity with this key and hence we can't
                    // be in the case of a to-be-auto-generated default key match. Quite the hack,
                    // but only relevant for EF6.

                    if (entry.netCount < 1)
                    {
                        attachedE = entry.entity;
                    }
                }

                await db.ReconcileCoreAsync(rctx, step, Link, attachedE, e, extent, nesting);

                if (step == ReconcileStep.Modify && !(key is null))
                {
                    TrackOrphan(key, e, +1);
                }
            }

            foreach (var pair in rctx.Iterate(toUpdate))
            {
                await db.ReconcileCoreAsync(rctx, step, null, pair.AttachedEntity, pair.TemplateEntity, extent, nesting);
            }
        }

        public override async Task LoadAsync(DbContext db, E attachedBaseEntity)
        {
            if (extent == null) return;
            var attachedCollection = selector(attachedBaseEntity);
            foreach (var attachedEntity in attachedCollection)
            {
                await db.LoadExtentAsync(attachedEntity, extent);
            }
        }

        public override void ForEach(DbContext db, E baseEntity, Action<DbContext, Object> action)
        {
            var collection = selector(baseEntity);

            foreach (var entity in collection)
            {
                db.ForEach(entity, action, extent);
            }
        }

        public class IdComparer : IComparer<Object>
        {
            static String GetId(Object o)
            {
                var result =  o.GetType().GetProperty("Id")?.GetValue(o)?.ToString();

                if (result == null) throw new Exception("No Id property found.");

                return result;
            }

            public Int32 Compare(Object x, Object y)
            {
                return Comparer<String>.Default.Compare(GetId(x), GetId(y));
            }
        }

        public override void Normalize(DbContext db, E entity)
        {
            var collection = selector(entity);

            if (!(collection is List<F> list))
            {
                throw new Exception("Only lists are normalizable.");
            }

            list.Sort(new IdComparer());

            foreach (var item in collection)
            {
                db.Normalize(item, extent);
            }
        }

        class OrphanTrackingEntry
        {
            public EntityKey entityKey;
            public F entity;
            public Int32 netCount;
        }

        private Dictionary<EntityKey, OrphanTrackingEntry> orphans;

        void TrackOrphan(EntityKey key, F orphan, Int32 netCount)
        {
            if (!orphans.TryGetValue(key, out var entry))
            {
                entry = orphans[key] = new OrphanTrackingEntry
                {
                    entityKey = key,
                    entity = orphan
                };
            }

            entry.netCount += netCount;
        }

        public override void Reset()
        {
            orphans = new Dictionary<EntityKey, OrphanTrackingEntry>();

            extent.Reset();
        }

        public override void RemoveOrphans(DbContext db)
        {
            foreach (var entry in orphans.Values)
            {
                if (entry.netCount < 0)
                {
                    db.RemoveEntity(entry.entity);
                }
            }

            extent.RemoveOrphans(db);
        }
    }

    enum ModifierMode
    {
        Reconciling,
        Loading
    }

    abstract class AbstractModifier<E>
    {
        public abstract void Modify(DbContext db, ModifierMode mode, E attachedEntity, E templateEntity);
    }

    abstract class AbstractPropertyModifier<E> : AbstractModifier<E>
        where E : class
    {
        protected readonly String property;

        public AbstractPropertyModifier(String property)
        {
            this.property = property;
        }
    }

    class ReadOnlyModifier<E> : AbstractPropertyModifier<E>
        where E : class
    {
        public ReadOnlyModifier(String property) : base(property) { }

        public override void Modify(DbContext db, ModifierMode mode, E attachedEntity, E templateEntity)
        {
            if (mode != ModifierMode.Reconciling) return;

            var entry = db.Entry(attachedEntity);
            entry.CurrentValues[property] = entry.OriginalValues[property];
        }
    }

    class BlackenModifier<E, T> : AbstractPropertyModifier<E>
        where E : class
    {
        public BlackenModifier(String property) : base(property) { }

        public override void Modify(DbContext db, ModifierMode mode, E attachedEntity, E templateEntity)
        {
            if (mode == ModifierMode.Loading)
            {
                var entry = db.Entry(attachedEntity);
                entry.CurrentValues[property] = default(T);
            }
            else if (mode == ModifierMode.Reconciling)
            {
                // Blacken works like read-only on writing as don't want to remove the blackened data.

                var entry = db.Entry(attachedEntity);
                entry.CurrentValues[property] = entry.OriginalValues[property];
            }
        }
    }

    class ActionModifier<E> : AbstractPropertyModifier<E>
        where E : class
    {
        private readonly Action<E> action;
        private readonly Boolean onAdditions;

        public ActionModifier(String property, Action<E> action, Boolean onAdditions)
            : base(property)
        {
            this.action = action;
            this.onAdditions = onAdditions;
        }

        public override void Modify(DbContext db, ModifierMode mode, E attachedEntity, E templateEntity)
        {
            if (mode != ModifierMode.Reconciling) return;

            var entry = db.Entry(attachedEntity);

            if (entry.State != EntityState.Added)
            {
                entry.CurrentValues[property] = entry.OriginalValues[property];
            }

            if (entry.State == EntityState.Added)
            {
                if (onAdditions)
                {
                    action(attachedEntity);
                }
            }
            else if (entry.State != EntityState.Deleted)
            {
                if (!onAdditions)
                {
                    // In the update case this action must override the value that comes from the template
                    entry.CurrentValues[property] = entry.OriginalValues[property];

                    action(attachedEntity);
                }
            }
        }
    }

    internal interface IExtent<E> where E : class
    {
        IReadOnlyCollection<Reconciler<E>> Reconcilers { get; }

        IReadOnlyCollection<AbstractModifier<E>> Properties { get; }
    }

    static class ExtentBuilder
    {
        internal static IExtent<E> Build<E>(this Action<ExtentBuilder<E>> extent)
            where E : class
        {
            var builder = new ExtentBuilder<E>();
            extent?.Invoke(builder);
            return builder;
        }
    }

    /// <summary>
    /// Builder class for building up tree extents.
    /// </summary>
    /// <typeparam name="E">The root node entity type of the tree.</typeparam>
    public class ExtentBuilder<E> : IExtent<E> where E : class
    {
        internal List<Reconciler<E>> reconcilers = new List<Reconciler<E>>();

        internal List<AbstractModifier<E>> properties = new List<AbstractModifier<E>>();

        IReadOnlyCollection<Reconciler<E>> IExtent<E>.Reconcilers => reconcilers;

        IReadOnlyCollection<AbstractModifier<E>> IExtent<E>.Properties => properties;

        public static Action<ExtentBuilder<E>> CastAction(Action<ExtentBuilder<E>> extent) => extent;

        /// <summary>
        /// Include a scalar navigational property in the current extent as owned - the referenced
        /// entity will be inserted, updated and removed as appropriate.
        /// </summary>
        /// <param name="selector">The navigational property to include.</param>
        /// <param name="extent">Optionally the nested extent if it's not trivial.</param>
        /// <returns>The same builder.</returns>
        public ExtentBuilder<E> WithOne<F>(Expression<Func<E, F>> selector, Action<ExtentBuilder<F>> extent = null)
            where F : class
        {
            reconcilers.Add(new EntityReconciler<E, F>(selector, extent.Build(), true));
            return this;
        }

        /// <summary>
        /// Include a scalar navigational property in the current extent as owned - the referenced
        /// entities will be inserted, updated and removed as appropriate.
        /// </summary>
        /// <param name="selector">The navigational property to include.</param>
        /// <param name="extent">Optionally the nested extent if it's not trivial.</param>
        /// <returns>The same builder.</returns>
        public ExtentBuilder<E> WithMany<F>(Expression<Func<E, ICollection<F>>> selector, Action<ExtentBuilder<F>> extent = null)
            where F : class
        {
            reconcilers.Add(new CollectionReconciler<E, F>(selector, extent.Build()));
            return this;
        }

        /// <summary>
        /// Include a scalar navigational property in the current extent as shared - the referenced
        /// entity will be inserted, updated as appropriate, but never removed.
        /// </summary>
        /// <param name="selector">The navigational property to include.</param>
        /// <param name="extent">Optionally the nested extent if it's not trivial.</param>
        /// <returns>The same builder.</returns>
        public ExtentBuilder<E> WithShared<F>(Expression<Func<E, F>> selector, Action<ExtentBuilder<F>> extent = null)
            where F : class
        {
            reconcilers.Add(new EntityReconciler<E, F>(selector, extent.Build(), false));
            return this;
        }

        /// <summary>
        /// Declares that differing template values of the given column property should be ignored on reconciliation.
        /// </summary>
        /// <param name="selector">The column to ignore on reconciliation.</param>
        /// <returns>The same builder.</returns>
        public ExtentBuilder<E> WithReadOnly<T>(Expression<Func<E, T>> selector)
        {
            properties.Add(new ReadOnlyModifier<E>(Properties.GetPropertyName(selector)));
            return this;
        }

        /// <summary>
        /// Declares that the given column property should not be returned from either loads and reconciliations (the
        /// value returned will be default(T)). This also implies `WithReadOnly`.
        /// </summary>
        /// <param name="selector">The column to blacken on load and reconciliation.</param>
        /// <returns>The same builder.</returns>
        public ExtentBuilder<E> WithBlacked<T>(Expression<Func<E, T>> selector)
        {
            properties.Add(new BlackenModifier<E, T>(Properties.GetPropertyName(selector)));
            return this;
        }

        /// <summary>
        /// Define an assignment that will be executed on all insertions. The assignment must be defined with an expression
        /// of the form `e => e.&lt;Property> == &lt;value>`.
        /// </summary>
        /// <param name="definition">An equality expression of the form `e => e.&lt;Property> == &lt;value>`.</param>
        /// <returns>The same builder.</returns>
        public ExtentBuilder<E> OnInsertion(Expression<Func<E, Boolean>> definition)
        {
            var info = Properties.GetEqualityExpressionInfo(definition);
            properties.Add(new ActionModifier<E>(info.Property.Name, e => info.Property.SetValue(e, info.Definition(e)), onAdditions: true));
            return this;
        }

        /// <summary>
        /// Define an assignment that will be executed on all updates, including insertions. The assignment must be defined with an expression
        /// of the form `e => e.&lt;Property> == &lt;value>`.
        /// </summary>
        /// <param name="definition">An equality expression of the form `e => e.&lt;Property> == &lt;value>`.</param>
        /// <returns>The same builder.</returns>
        public ExtentBuilder<E> OnUpdate(Expression<Func<E, Boolean>> definition)
        {
            var info = Properties.GetEqualityExpressionInfo(definition);
            properties.Add(new ActionModifier<E>(info.Property.Name, e => info.Property.SetValue(e, info.Definition(e)), onAdditions: false));
            return this;
        }
    }

    /// <summary>
    /// This internal API is only exposed for testing purposes and should not be used
    /// </summary>
    public static class InternalExtensionsForTesting
    {
        /// <summary>
        /// This internal API is only exposed for testing purposes and should not be used
        /// </summary>
        public static void Normalize<E>(this DbContext db, E entity, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            db.Normalize(entity, extent.Build());
        }

        internal static void Normalize<E>(this DbContext db, E entity, IExtent<E> extent)
            where E : class
        {
            if (entity == null) throw new Exception("Entity shouldn't be null");

            foreach (var reconciler in extent.Reconcilers)
            {
                reconciler.Normalize(db, entity);
            }
        }
    }
}

#if EF6
namespace System.Data.Entity
#endif
#if EFCORE
namespace Microsoft.EntityFrameworkCore
#endif
{
#if EFCORE
    using Microsoft.Extensions.Logging;
#endif
    using MonkeyBusters.Reconciliation.Internal;
    using System.Text;
    using System.Xml.Linq;

    /// <summary>
    /// This internal API is only exposed for testing purposes and should not be used
    /// </summary>
    public interface IReconcilerDiagnosticsSettings
    {
        /// <summary>
        /// This internal API is only exposed for testing purposes and should not be used
        /// </summary>
        Boolean ReverseInteration { get; }
    }

#if EFCORE
    public interface IReconcilerLoggerProvider
    {
        ILogger ReconcilerLogger { get; }

        Boolean LogDebugView { get; }
    }
#endif

    /// <summary>
    /// The public interface to the Reconciler.
    /// </summary>
    public static class ReconciliationExtensions
    {
        /// <summary>
        /// Reconciles the stored entity graph extending as far as described by the given extent with the one given by entity.
        /// It makes a number of load requests from the store, but all modifications are merely scheduled in the context.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="templateEntity">The detached graph to reconcile with.</param>
        /// <param name="extent">The extent of the subgraph to reconcile.</param>
        /// <returns>The attached entity.</returns>
        public static Task<E> ReconcileAsync<E>(this DbContext db, E templateEntity, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            return db.ReconcileWithPreparationAsync(templateEntity, extent, 0);
        }

        /// <summary>
        /// Reconciles the stored entity graph extending as far as described by the given extent with the one given by entity.
        /// It makes a number of load requests from the store, but all modifications are merely scheduled in the context.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="templateEntity">The detached graph to reconcile with.</param>
        /// <param name="extent">The extent of the subgraph to reconcile.</param>
        /// <returns>The attached entity.</returns>
        public static async Task<E> ReconcileAndSaveChangesAsync<E>(this DbContext db, E templateEntity, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            var attachedEntity = await db.ReconcileWithPreparationAsync(templateEntity, extent, 0);
            await db.SaveChangesAsync();
            return attachedEntity;
        }

        /// <summary>
        /// Reconciles the stored entity graph extending as far as described by the given extent with the one given by entity.
        /// It makes a number of load requests from the store, but all modifications are merely scheduled in the context.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="templateEntity">The detached graph to reconcile with.</param>
        /// <param name="extent">The extent of the subgraph to reconcile.</param>
        /// <returns>The attached entity.</returns>
        public static E Reconcile<E>(this DbContext db, E templateEntity, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            var task = db.ReconcileAsync(templateEntity, extent);
            task.Wait();
            return task.Result;
        }

        /// <summary>
        /// Reconciles the stored entity graph extending as far as described by the given extent with the one given by entity.
        /// It makes a number of load requests from the store, but all modifications are merely scheduled in the context.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="templateEntity">The detached graph to reconcile with.</param>
        /// <param name="extent">The extent of the subgraph to reconcile.</param>
        /// <returns>The attached entity.</returns>
        public static E ReconcileAndSaveChanges<E>(this DbContext db, E templateEntity, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            var task = db.ReconcileAndSaveChangesAsync(templateEntity, extent);
            task.Wait();
            return task.Result;
        }

        internal static async Task<E> ReconcileWithPreparationAsync<E>(this DbContext db, E templateEntity, Action<ExtentBuilder<E>> extent, Int32 nesting)
            where E : class
        {
            async Task<E> Reconcile()
            {
                var settings = db as IReconcilerDiagnosticsSettings;

                var builtExtent = extent.Build();

                var rctx = new ReconcilingContext
                {
                    ReverseInteration = settings?.ReverseInteration ?? false
                };

                builtExtent.Reset();

#if EFCORE
                // This fixes key consistency and allows to remove the respective requirement in EF Core
                templateEntity = db.CreateDetachedDeepClone(templateEntity, extent);
#endif

                var attachedEntity = await db.ReconcileCoreAsync(rctx, ReconcileStep.Load, null, null, templateEntity, builtExtent, nesting);

                attachedEntity = await db.ReconcileCoreAsync(rctx, ReconcileStep.Modify, null, attachedEntity, templateEntity, builtExtent, nesting);

                builtExtent.RemoveOrphans(db);

                return attachedEntity;
            }

#if EFCORE
            var originalCascadeDeleteTiming = db.ChangeTracker.CascadeDeleteTiming;
            var originalDeleteOrphansTiming = db.ChangeTracker.DeleteOrphansTiming;

            db.ChangeTracker.CascadeDeleteTiming = ChangeTracking.CascadeTiming.Never;
            db.ChangeTracker.DeleteOrphansTiming = ChangeTracking.CascadeTiming.Never;

            try
            {
                return await Reconcile();
            }
            finally
            {
                db.ChangeTracker.CascadeDeleteTiming = originalCascadeDeleteTiming;
                db.ChangeTracker.DeleteOrphansTiming = originalDeleteOrphansTiming;
            }
#endif

#if EF6
            return await Reconcile();
#endif
        }

        internal static async Task<E> ReconcileCoreAsync<E>(this DbContext db, ReconcilingContext rctx, ReconcileStep step, Action<E> linkWithParent, E attachedEntity, E templateEntity, IExtent<E> extent, Int32 nesting)
            where E : class
        {
            db.LogTrace(nesting, "> ReconcileAsync {step}", step);

            if (step == ReconcileStep.Load)
            {
                // Try to load an existing entity and their navigational properties unless there are none or we've added the entity
                // -- and added entity in this situation happens only in the case of database-generated keys where we can know
                // from the key that the entity must be added and can't already exist in storage. In that particular case, no
                // navigation properties need loading either.
                if (attachedEntity == null || (db.Entry(attachedEntity).State != EntityState.Added && extent.Reconcilers.Count != 0))
                {
                    // In case of removals, the templateEntity is null, and otherwise
                    // attachedEntity is often null as it's not always preloaded by
                    // the caller.
                    var entityToTakeTheKeyFrom = templateEntity ?? attachedEntity;

                    attachedEntity = await extent.Reconcilers
                        .Aggregate(db.GetEntity(entityToTakeTheKeyFrom), (q, r) => r.AugmentInclude(q))
                        .FirstOrDefaultAsync();

                    db.LogTrace(nesting, "  loaded entity");
                }
            }

            var doesEntityNeedAdding = attachedEntity == null || (db.Entry(attachedEntity).State == EntityState.Deleted && templateEntity != null);

            if (doesEntityNeedAdding)
            {
                if (step == ReconcileStep.Modify || step == ReconcileStep.Clone)
                {
                    attachedEntity = db.AddEntity(templateEntity);

                    if (linkWithParent != null)
                    {
                        linkWithParent(attachedEntity);
                    }

                    db.LogTrace(nesting, "  added new entity");
                }
            }

            foreach (var reconciler in extent.Reconcilers)
            {
                db.LogTrace(nesting, "  > reconciler {reconciler}", reconciler);

                await reconciler.ReconcileAsync(db, rctx, step, attachedEntity, templateEntity, nesting + 1);

                db.LogTrace(nesting, "  < reconciler {reconciler}", reconciler);
            }

            if (step == ReconcileStep.Modify)
            {
                if (!doesEntityNeedAdding && templateEntity != null)
                {
                    var state = db.Entry(attachedEntity).State;

                    // An entity in the added state does not need updating and UpdateEntity fails with
                    // database-generated keys.
                    if (state != EntityState.Added
#if EF6
                        // In EF6, the entity remains detached when added merely to a principal's collection
                        && state != EntityState.Detached
#endif
                        )
                    {
                        db.UpdateEntity(attachedEntity, templateEntity);

                        db.LogTrace(nesting, "  updated entity");
                    }
                }

                foreach (var property in extent.Properties)
                {
                    property.Modify(db, ModifierMode.Reconciling, attachedEntity, templateEntity);
                }
            }

            db.LogTrace(nesting, "< ReconcileAsync");

            return attachedEntity;
        }

        /// <summary>
        /// Loads the entity given by the given entity's key to the given extent.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="entityToLoad">The detached entity the key of which defines what entity to load.</param>
        /// <param name="extent">The extent to which to load the entity.</param>
        /// <returns>The attached entity.</returns>
        public static async Task<E> LoadExtentAsync<E>(this DbContext db, E entityToLoad, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            return await LoadExtentAsync(db, entityToLoad, extent.Build());
        }

        internal static async Task<E> LoadExtentAsync<E>(this DbContext db, E entityToLoad, IExtent<E> extent)
            where E : class
        {
            var attachedEntity = await extent.Reconcilers
                .Aggregate(db.GetEntity(entityToLoad), (q, r) => r.AugmentInclude(q))
                .FirstOrDefaultAsync();

            foreach (var reconciler in extent.Reconcilers)
            {
                await reconciler.LoadAsync(db, attachedEntity);
            }

            foreach (var property in extent.Properties)
            {
                property.Modify(db, ModifierMode.Loading, attachedEntity, null);
            }

            return attachedEntity;
        }

        /// <summary>
        /// Loads the entity given by the given entity's key to the given extent.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="entityToLoad">The detached entity the key of which defines what entity to load.</param>
        /// <param name="extent">The extent to which to load the entity.</param>
        /// <returns>The attached entity.</returns>
        public static E LoadExtent<E>(this DbContext db, E entityToLoad, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            var task = db.LoadExtentAsync(entityToLoad, extent);
            task.Wait();
            return task.Result;
        }

        /// <summary>
        /// Creates a detached deep clone from the given entity
        /// </summary>
        /// <typeparam name="E"></typeparam>
        /// <param name="db">An empty context to use for creating the deep clone</param>
        /// <param name="entity">The entity to clone</param>
        /// <param name="extent">The extent in which to clone</param>
        /// <returns>A detached clone of the entity</returns>
        public static E CreateDetachedDeepClone<E>(this DbContext db, E entity, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            if (db.ChangeTracker.Entries().FirstOrDefault() != null)
            {
                throw new Exception($"You need to call this method on a fresh context");
            }

            var task = db.ReconcileCoreAsync(new ReconcilingContext(), ReconcileStep.Clone, null, null, entity, extent.Build(), 0);
            task.Wait();

            db.ChangeTracker.DetectChanges();

#if EFCORE
            db.ChangeTracker.Clear();
#endif

#if EF6
            foreach (var entry in db.ChangeTracker.Entries())
            {
                entry.State = EntityState.Detached;
            }

            if (db.ChangeTracker.Entries().FirstOrDefault() != null)
            {
                throw new Exception($"Unexpected attached entry in context");
            }
#endif

            return task.Result;
        }

        static DbContext CreateContext(this DbContext db)
        {
            var newDb = Activator.CreateInstance(db.GetType()) as DbContext;

            if (newDb is null) throw new Exception($"Could not create a new context of type {db.GetType()}.");

            return newDb;
        }

        /// <summary>
        /// Creates a detached shallow clone from the given entity
        /// </summary>
        /// <typeparam name="E">The entity to clone.</typeparam>
        /// <param name="arbitraryContext">A context providing the model - this doesn't have to be a context the entity is attached to</param>
        /// <param name="entity">The entity to clone</param>
        /// <returns>The detached shallow clone</returns>
        public static E CreateDetachedShallowClone<E>(this DbContext arbitraryContext, E entity)
            where E : class
            // FIXME: This is also done in a different way by the AddEntity extension method,
            // this should probably be unified.
            => arbitraryContext.Entry(entity).CurrentValues.Clone().ToObject() as E;

        /// <summary>
        /// Executes the given action on all entities in the given extent
        /// </summary>
        /// <param name="db">A context providing the model - this doesn't have to be a context the entity is attached to</param>
        /// <param name="entity">The extent root</param>
        /// <param name="action">The action to execute</param>
        /// <param name="extent">The extent to reach into</param>
        public static void ForEach<E>(this DbContext db, E entity, Action<DbContext, Object> action, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            db.ForEach(entity, action, extent.Build());
        }
        
        internal static void ForEach<E>(this DbContext db, E entity, Action<DbContext, Object> action, IExtent<E> extent)
            where E : class
        {
            action(db, entity);

            if (extent != null)
            {
                foreach (var reconciler in extent.Reconcilers)
                {
                    reconciler.ForEach(db, entity, action);
                }
            }
        }

        internal static void Reset<E>(this IExtent<E> extent)
            where E : class
        {
            foreach (var reconciler in extent.Reconcilers)
            {
                reconciler.Reset();
            }
        }

        internal static void RemoveOrphans<E>(this IExtent<E> extent, DbContext db)
            where E : class
        {
            foreach (var reconciler in extent.Reconcilers)
            {
                reconciler.RemoveOrphans(db);
            }
        }

        internal static void LogTrace(this DbContext db, Int32 nesting, String message, params Object[] args)
        {
#if EFCORE
            if (db is IReconcilerLoggerProvider provider && provider.ReconcilerLogger is ILogger logger)
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    var b = new StringBuilder();
                    for (var i = 0; i < nesting; ++i)
                    {
                        b.Append(".   ");
                    }
                    b.Append(message);
                    if (provider.LogDebugView)
                    {
                        b.AppendLine();
                        b.AppendLine(db.ChangeTracker.DebugView.ShortView
                            .Replace("{", "{{")
                            .Replace("}", "}}")
                        );
                    }

                    //db.ChangeTracker.DetectChanges();

                    logger.LogTrace(b.ToString(), args);
                }
            }
#endif
        }
    }

    static class Properties
    {
        internal static String GetPropertyName<E, F>(Expression<Func<E, F>> selector)
        {
            return GetPropertyInfo(selector).Name;
        }

        internal static PropertyInfo GetPropertyInfo<E, T>(Expression<Func<E, T>> selector)
        {
            MemberExpression exp = null;

            //this line is necessary, because sometimes the expression comes in as Convert(originalExpression)
            if (selector.Body is UnaryExpression)
            {
                var UnExp = (UnaryExpression)selector.Body;
                if (UnExp.Operand is MemberExpression)
                {
                    exp = (MemberExpression)UnExp.Operand;
                }
                else
                    throw new ArgumentException();
            }
            else if (selector.Body is MemberExpression)
            {
                exp = (MemberExpression)selector.Body;
            }
            else
            {
                throw new ArgumentException();
            }

            return (PropertyInfo)exp.Member;
        }

        internal class EqualityExpressionDefinition<E>
        {
            public PropertyInfo Property { get; set; }
            public Func<E, Object> Definition { get; set; }
        }

        internal static EqualityExpressionDefinition<E> GetEqualityExpressionInfo<E>(Expression<Func<E, Boolean>> selector)
        {
            if (
                selector.Body is BinaryExpression binaryExpression &&
                binaryExpression.NodeType == ExpressionType.Equal &&
                binaryExpression.Left is MemberExpression memberExpression2 &&
                memberExpression2.Expression is ParameterExpression targetParameterExpression
            )
            {
                var lambda = Expression.Lambda(binaryExpression.Right, targetParameterExpression);
                var compiled = lambda.Compile();

                return new EqualityExpressionDefinition<E>
                {
                    Property = (PropertyInfo)memberExpression2.Member,
                    Definition = e => compiled.DynamicInvoke(e)
                };
            }
            else
            {
                throw new ArgumentException("Binary expression is supposed to be of the form 'e => e.<Property> == <expr>");
            }
        }
    }
}
