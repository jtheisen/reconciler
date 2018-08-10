using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

#if EF6
using System.Data.Entity;
#endif

#if EFCORE
using Microsoft.EntityFrameworkCore;
#endif

namespace MonkeyBusters.Reconciliation.Internal
{
    /// <summary>
    /// Reconcilers are implemented for scalar and collection navigational properties
    /// and are responsible for syncing them by inserting, deleting and updating
    /// the entities in one of such properties.
    /// </summary>
    abstract class Reconciler<E> where E : class
    {
        public abstract IQueryable<E> AugmentInclude(IQueryable<E> query);

        public abstract Task ReconcileAsync(DbContext db, E attachedEntity, E templateEntity);
    }

    class EntityReconciler<E, F> : Reconciler<E>
        where E : class
        where F : class
    {
        private readonly Expression<Func<E, F>> selectorExpression;
        private readonly Func<E, F> selector;
        private readonly Action<ExtentBuilder<F>> extent;
        private readonly Boolean removeOrphans;

        public EntityReconciler(Expression<Func<E, F>> selector, Action<ExtentBuilder<F>> mapping, Boolean removeOrphans)
        {
            this.selectorExpression = selector;
            this.selector = selector.Compile();
            this.extent = mapping;
            this.removeOrphans = removeOrphans;
        }

        public override IQueryable<E> AugmentInclude(IQueryable<E> query)
            => query.Include(selectorExpression);

        public override async Task ReconcileAsync(DbContext db, E attachedBaseEntity, E templateBaseEntity)
        {
            var attachedEntity = selector(attachedBaseEntity);
            var templateEntity = templateBaseEntity != null
                ? selector(templateBaseEntity)
                : null;

            var attachedEntityKey = db.GetEntityKey(attachedEntity);
            var templateEntityKey = db.GetEntityKey(templateEntity);

            if (templateEntityKey == attachedEntityKey)
            {
                if (templateEntityKey == null) return;

                await db.ReconcileAsync(templateEntity, extent);
            }
            else
            {
                if (attachedEntity != null && removeOrphans)
                {
                    await db.ReconcileAsync(attachedEntity, null, extent);

                    db.RemoveEntity(attachedEntity);
                }

                if (templateEntity != null)
                {
                    await db.ReconcileAsync(templateEntity, extent);
                }
            }
        }
    }

    class CollectionReconciler<E, F> : Reconciler<E>
        where E : class
        where F : class
    {
        private readonly Expression<Func<E, ICollection<F>>> selectorExpression;
        private readonly Func<E, ICollection<F>> selector;
        private readonly Action<ExtentBuilder<F>> extent;

        public CollectionReconciler(Expression<Func<E, ICollection<F>>> selector, Action<ExtentBuilder<F>> extent)
        {
            this.selectorExpression = selector;
            this.selector = selector.Compile();
            this.extent = extent;
        }

        public override IQueryable<E> AugmentInclude(IQueryable<E> query)
            => query.Include(this.selectorExpression);

        public override async Task ReconcileAsync(DbContext db, E attachedEntity, E templateEntity)
        {
            var attachedCollection = selector(attachedEntity).ToArray();
            var templateCollection = templateEntity != null
                ? selector(templateEntity).ToArray()
                : Enumerable.Empty<F>();

            var toRemove = (
                from o in attachedCollection
                join n in templateCollection on db.GetEntityKey(o) equals db.GetEntityKey(n) into news
                where news.Count() == 0
                select o
            ).ToArray();

            foreach (var e in toRemove)
            {
                await db.ReconcileAsync(e, null, extent);

                db.RemoveEntity(e);
            }

            var toAdd = (
                from n in templateCollection
                join o in attachedCollection on db.GetEntityKey(n) equals db.GetEntityKey(o) into olds
                where olds.Count() == 0
                select n
            ).ToArray();

            foreach (var e in toAdd)
            {
                await db.ReconcileAsync(e, extent);
            }

            var toUpdate = (
                from o in attachedCollection
                join n in templateCollection on db.GetEntityKey(o) equals db.GetEntityKey(n)
                select new
                {
                    AttachedEntity = o,
                    TemplateEntity = n
                }
            ).ToArray();

            foreach (var pair in toUpdate)
            {
                await db.ReconcileAsync(pair.AttachedEntity, pair.TemplateEntity, extent);
            }
        }
    }

    /// <summary>
    /// Builder class for building up tree extents.
    /// </summary>
    /// <typeparam name="E">The root node entity type of the tree.</typeparam>
    public class ExtentBuilder<E> where E : class
    {
        internal List<Reconciler<E>> reconcilers = new List<Reconciler<E>>();

        /// <summary>
        /// Include a scalar navigational property in the current extent as owned - the referenced
        /// entity will be inserted, updated and removed as appropriate.
        /// </summary>
        /// <typeparam name="F"></typeparam>
        /// <param name="selector">The navigational property to include.</param>
        /// <param name="extent">Optionally the nested extent if it's not trivial.</param>
        /// <returns>The same builder.</returns>
        public ExtentBuilder<E> WithOne<F>(Expression<Func<E, F>> selector, Action<ExtentBuilder<F>> extent = null)
            where F : class
        {
            reconcilers.Add(new EntityReconciler<E, F>(selector, extent, true));
            return this;
        }

        /// <summary>
        /// Include a scalar navigational property in the current extent as owned - the referenced
        /// entities will be inserted, updated and removed as appropriate.
        /// </summary>
        /// <typeparam name="F"></typeparam>
        /// <param name="selector">The navigational property to include.</param>
        /// <param name="extent">Optionally the nested extent if it's not trivial.</param>
        /// <returns>The same builder.</returns>
        public ExtentBuilder<E> WithMany<F>(Expression<Func<E, ICollection<F>>> selector, Action<ExtentBuilder<F>> extent = null)
            where F : class
        {
            reconcilers.Add(new CollectionReconciler<E, F>(selector, extent));
            return this;
        }

        /// <summary>
        /// Include a scalar navigational property in the current extent as shared - the referenced
        /// entity will be inserted, updated as appropriate, but never removed.
        /// </summary>
        /// <typeparam name="F"></typeparam>
        /// <param name="selector">The navigational property to include.</param>
        /// <param name="extent">Optionally the nested extent if it's not trivial.</param>
        /// <returns>The same builder.</returns>
        public ExtentBuilder<E> WithShared<F>(Expression<Func<E, F>> selector, Action<ExtentBuilder<F>> extent = null)
            where F : class
        {
            reconcilers.Add(new EntityReconciler<E, F>(selector, extent, false));
            return this;
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
    using MonkeyBusters.Reconciliation.Internal;

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
        public static Task<E> ReconcileAsync<E>(this DbContext db, E entity, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            return db.ReconcileAsync(null, entity, extent);
        }

        /// <summary>
        /// Reconciles the stored entity graph extending as far as described by the given extent with the one given by entity.
        /// It makes a number of load requests from the store, but all modifications are merely scheduled in the context.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="entity">The detached graph to reconcile with.</param>
        /// <param name="extent">The extent of the subgraph to reconcile.</param>
        /// <returns>The attached entity.</returns>
        public static E Reconcile<E>(this DbContext db, E entity, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            var task = db.ReconcileAsync(entity, extent);
            task.Wait();
            return task.Result;
        }

        /// <summary>
        /// Reconciles the stored entity graph extending as far as described by the given extent with the one given by entity.
        /// It makes a number of load requests from the store, but all modifications are merely scheduled in the context.
        /// This overload take a prefetched attached entity that can allow the skipping of the load request in case of a
        /// trivial extent.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="entity">The detached graph to reconcile with.</param>
        /// <param name="extent">The extent of the subgraph to reconcile.</param>
        /// <returns>The attached entity.</returns>
        internal static async Task<E> ReconcileAsync<E>(this DbContext db, E attachedEntity, E templateEntity, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            // FIXME: We want to be able to deal with unset foreign keys, perhaps this helps:
            // https://stackoverflow.com/questions/4384081/read-foreign-key-metadata-programatically-with-entity-framework-4

            var builder = new ExtentBuilder<E>();
            extent?.Invoke(builder);

            if (attachedEntity == null || builder.reconcilers.Count != 0)
            {
                // In case of removals, the templateEntity is null, and otherwise
                // attachedEntity is often null as it's not always preloaded by
                // the caller.
                var entityToTakeTheKeyFrom = templateEntity ?? attachedEntity;

                attachedEntity = builder.reconcilers
                    .Aggregate(db.GetEntity(entityToTakeTheKeyFrom), (q, r) => r.AugmentInclude(q))
                    .FirstOrDefault();
            }

            var isNewEntity = attachedEntity == null;

            if (isNewEntity)
            {
                attachedEntity = db.AddEntity(templateEntity);
            }

            foreach (var reconciler in builder.reconcilers)
            {
                await reconciler.ReconcileAsync(db, attachedEntity, templateEntity);
            }

            if (!isNewEntity && templateEntity != null)
            {
                db.UpdateEntity(attachedEntity, templateEntity);
            }

            return attachedEntity;
        }

        /// <summary>
        /// Loads the entity given by the given entity's key to the given extent.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="entity">The detached entity the key of which defines what entity to load.</param>
        /// <param name="extent">The extent to which to load the entity.</param>
        /// <returns>The attached entity.</returns>
        public static async Task<E> LoadExtentAsync<E>(this DbContext db, E entity, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            var builder = new ExtentBuilder<E>();
            extent(builder);

            var oldEntity = await builder.reconcilers
                .Aggregate(db.GetEntity(entity), (q, r) => r.AugmentInclude(q))
                .FirstOrDefaultAsync();

            return oldEntity;
        }

        /// <summary>
        /// Loads the entity given by the given entity's key to the given extent.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="entity">The detached entity the key of which defines what entity to load.</param>
        /// <param name="extent">The extent to which to load the entity.</param>
        /// <returns>The attached entity.</returns>
        public static E LoadExtent<E>(this DbContext db, E entity, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            var task = db.LoadExtentAsync(entity, extent);
            task.Wait();
            return task.Result;
        }
    }
}
