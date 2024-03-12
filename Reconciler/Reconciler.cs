﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using System.Reflection;

#if EF6
using System.Data.Entity;
#endif

#if EFCORE
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Internal;
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

        public abstract Task ReconcileAsync(DbContext db, E attachedEntity, E templateEntity, Int32 nesting);

        public abstract Task LoadAsync(DbContext db, E attachedEntity);

        public abstract void ForEach(DbContext db, E entity, Action<DbContext, Object> action);

        public abstract void Normalize(DbContext db, E entity);
    }

    class EntityReconciler<E, F> : Reconciler<E>
        where E : class
        where F : class
    {
        private readonly Expression<Func<E, F>> selectorExpression;
        private readonly PropertyInfo propertyInfo;
        private readonly Func<E, F> selector;
        private readonly Action<ExtentBuilder<F>> extent;
        private readonly Boolean removeOrphans;

        public EntityReconciler(Expression<Func<E, F>> selector, Action<ExtentBuilder<F>> mapping, Boolean removeOrphans)
        {
            this.selectorExpression = selector;
            this.propertyInfo = Properties.GetPropertyInfo(selector);
            this.selector = selector.Compile();
            this.extent = mapping;
            this.removeOrphans = removeOrphans;
        }

        public override String ToString() => $"-{propertyInfo.Name}";

        public override IQueryable<E> AugmentInclude(IQueryable<E> query)
            => query.Include(selectorExpression);

        public override async Task ReconcileAsync(DbContext db, E attachedBaseEntity, E templateBaseEntity, Int32 nesting)
        {
            var attachedEntity = selector(attachedBaseEntity);
            var templateEntity = templateBaseEntity != null
                ? selector(templateBaseEntity)
                : null;

            var attachedEntityKey = db.GetEntityKey(attachedEntity);
            var templateEntityKey = db.GetEntityKey(templateEntity);

            if (templateEntityKey is not null && attachedEntityKey is not null && templateEntityKey == attachedEntityKey)
            {
                await db.ReconcileCoreAsync(null, attachedEntity, templateEntity, extent, nesting);
            }
            else
            {
                if (attachedEntity != null && removeOrphans)
                {
                    if (attachedEntityKey is null) throw new Exception("Found attached entity to delete without key");

                    await db.ReconcileCoreAsync(null, attachedEntity, null, extent, nesting);

                    db.RemoveEntity(attachedEntity);
                }

                if (templateEntity != null)
                {
                    void Link(F entityToLink)
                    {
                        propertyInfo.SetValue(attachedBaseEntity, entityToLink);

                        Console.WriteLine($"set to nav prop in {attachedBaseEntity} of state {db.Entry(attachedBaseEntity).State}");

                        db.LogTrace(nesting, "after setting value");
                    }

                    // The attached entity isn't passed as, if it exists, is the one we deleted above.
                    await db.ReconcileCoreAsync(Link, null, templateEntity, extent, nesting);
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

        public override String ToString() => $"*{Properties.GetPropertyInfo(selectorExpression).Name}";

        public override IQueryable<E> AugmentInclude(IQueryable<E> query)
            => query.Include(this.selectorExpression);

        public override async Task ReconcileAsync(DbContext db, E attachedEntity, E templateEntity, Int32 nesting)
        {
            // FIXME: Tests are failing as we're trying to remove entities with default keys

            var attachedCollection = selector(attachedEntity);
            var templateCollection = templateEntity != null
                ? selector(templateEntity).ToArray()
                : Enumerable.Empty<F>();

            var toRemove = (
                from o in attachedCollection
                where db.GetEntityKey(o) != null
                join n in templateCollection on db.GetEntityKey(o) equals db.GetEntityKey(n) into news
                where news.Count() == 0 && db.Entry(o)?.State != EntityState.Deleted
                select o
            ).ToArray();

            var toAdd = (
                from n in templateCollection
                join o in attachedCollection on db.GetEntityKey(n) equals db.GetEntityKey(o) into olds
                where olds.Count() == 0
                select n
            ).ToArray();

            var toUpdate = (
                from o in attachedCollection
                join n in templateCollection on db.GetEntityKey(o) equals db.GetEntityKey(n)
                select new
                {
                    AttachedEntity = o,
                    TemplateEntity = n
                }
            ).ToArray();

            foreach (var e in toRemove)
            {
                await db.ReconcileCoreAsync(null, e, null, extent, nesting);

                db.RemoveEntity(e);
            }

            foreach (var e in toAdd)
            {
                void Link(F f)
                {
                    attachedCollection.Add(f);
                }

                //db.Entry(e).State = EntityState.Added;
                //db.SetState(e, EntityState.Added, extent);

                await db.ReconcileCoreAsync(Link, e, e, extent, nesting);


                //db.ChangeTracker.DetectChanges();

                //var state = db.Entry(e).State;
            }

            foreach (var pair in toUpdate)
            {
                await db.ReconcileCoreAsync(null, pair.AttachedEntity, pair.TemplateEntity, extent, nesting);
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
    }

    abstract class AbstractModifier<E>
    {
        public abstract void Modify(DbContext db, E attachedEntity, E templateEntity);
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

        public override void Modify(DbContext db, E attachedEntity, E templateEntity)
        {
            var entry = db.Entry(attachedEntity);
            entry.CurrentValues[property] = entry.OriginalValues[property];
        }
    }

    class BlackenModifier<E, T> : AbstractPropertyModifier<E>
        where E : class
    {
        public BlackenModifier(String property) : base(property) { }

        public override void Modify(DbContext db, E attachedEntity, E templateEntity)
        {
            var entry = db.Entry(attachedEntity);
            entry.CurrentValues[property] = default(T);
        }
    }

    class ActionModifier<E> : AbstractPropertyModifier<E>
        where E : class
    {
        private readonly Action<E> action;
        private readonly Boolean onlyOnAdditions;

        public ActionModifier(String property, Action<E> action, Boolean onlyOnAdditions)
            : base(property)
        {
            this.action = action;
            this.onlyOnAdditions = onlyOnAdditions;
        }

        public override void Modify(DbContext db, E attachedEntity, E templateEntity)
        {
            var entry = db.Entry(attachedEntity);

            if (entry.State != EntityState.Added)
            {
                entry.CurrentValues[property] = entry.OriginalValues[property];
            }

            if (!onlyOnAdditions || entry.State == EntityState.Added)
            {
                action(attachedEntity);
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

        internal List<AbstractModifier<E>> properties = new List<AbstractModifier<E>>();

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
            reconcilers.Add(new EntityReconciler<E, F>(selector, extent, true));
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
            reconcilers.Add(new CollectionReconciler<E, F>(selector, extent));
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
            reconcilers.Add(new EntityReconciler<E, F>(selector, extent, false));
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
            properties.Add(new ActionModifier<E>(info.Property.Name, e => info.Property.SetValue(e, info.Definition(e)), onlyOnAdditions: true));
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
            properties.Add(new ActionModifier<E>(info.Property.Name, e => info.Property.SetValue(e, info.Definition(e)), onlyOnAdditions: false));
            return this;
        }
    }

    public static class InternalExtensionsForTesting
    {
        public static void Normalize<E>(this DbContext db, E entity, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            if (entity == null) throw new Exception("Entity shouldn't be null");

            var builder = new ExtentBuilder<E>();
            extent?.Invoke(builder);
            foreach (var reconciler in builder.reconcilers)
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
            return db.ReconcileWithPreparationAsync(null, null, templateEntity, extent, 0);
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
            var attachedEntity = await db.ReconcileWithPreparationAsync(null, null, templateEntity, extent, 0);
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

        internal static async Task<E> ReconcileWithPreparationAsync<E>(this DbContext db, Action<E> linkWithParent, E attachedEntity, E templateEntity, Action<ExtentBuilder<E>> extent, Int32 nesting)
            where E : class
        {
#if EFCORE
            var originalCascadeDeleteTiming = db.ChangeTracker.CascadeDeleteTiming;

            db.ChangeTracker.CascadeDeleteTiming = ChangeTracking.CascadeTiming.Never;

            // Not sure if we ever will need this one:
            //db.ChangeTracker.DeleteOrphansTiming = ChangeTracking.CascadeTiming.Never;

            try
            {
                return await db.ReconcileCoreAsync(linkWithParent, attachedEntity, templateEntity, extent, nesting);
            }
            finally
            {
                db.ChangeTracker.CascadeDeleteTiming = originalCascadeDeleteTiming;
            }
#endif

#if EF6
            return await db.ReconcileCoreAsync(linkWithParent, attachedEntity, templateEntity, extent, nesting);
#endif
        }

        internal static async Task<E> ReconcileCoreAsync<E>(this DbContext db, Action<E> linkWithParent, E attachedEntity, E templateEntity, Action<ExtentBuilder<E>> extent, Int32 nesting)
            where E : class
        {
            db.LogTrace(nesting, "> ReconcileAsync");

            var builder = new ExtentBuilder<E>();
            extent?.Invoke(builder);

            // Try to load an existing entity and their navigational properties unless there are none or we've added the entity
            // -- and added entity in this situation happens only in the case of database-generated keys where we can know
            // from the key that the entity must be added and can't already exist in storage. In that particular case, no
            // navigation properties need loading either.
            if (attachedEntity == null || (db.Entry(attachedEntity).State != EntityState.Added && builder.reconcilers.Count != 0))
            {
                // In case of removals, the templateEntity is null, and otherwise
                // attachedEntity is often null as it's not always preloaded by
                // the caller.
                var entityToTakeTheKeyFrom = templateEntity ?? attachedEntity;

                attachedEntity = await builder.reconcilers
                    .Aggregate(db.GetEntity(entityToTakeTheKeyFrom), (q, r) => r.AugmentInclude(q))
                    .FirstOrDefaultAsync();

                db.LogTrace(nesting, "  loaded entity");
            }

            var doesEntityNeedAdding = attachedEntity == null || (db.Entry(attachedEntity).State == EntityState.Deleted && templateEntity != null);
            
            if (doesEntityNeedAdding)
            {
                attachedEntity = db.AddEntity(templateEntity);

                db.LogTrace(nesting, "  added new entity");
            }

            if (linkWithParent != null)
            {
                linkWithParent(attachedEntity);
            }

            foreach (var reconciler in builder.reconcilers)
            {
                db.LogTrace(nesting, "  > reconciler {reconciler}", reconciler);

                await reconciler.ReconcileAsync(db, attachedEntity, templateEntity, nesting + 1);

                db.LogTrace(nesting, "  < reconciler {reconciler}", reconciler);
            }

            // An entity in the added state does not need updating and UpdateEntity fails with
            // database-generated keys.
            if (!doesEntityNeedAdding && templateEntity != null && db.Entry(attachedEntity).State != EntityState.Added)
            {
                db.UpdateEntity(attachedEntity, templateEntity);

                db.LogTrace(nesting, "  updated entity");
            }

            foreach (var property in builder.properties)
            {
                property.Modify(db, attachedEntity, templateEntity);
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
            var builder = new ExtentBuilder<E>();
            extent(builder);

            var attachedEntity = await builder.reconcilers
                .Aggregate(db.GetEntity(entityToLoad), (q, r) => r.AugmentInclude(q))
                .FirstOrDefaultAsync();

            foreach (var reconciler in builder.reconcilers)
            {
                await reconciler.LoadAsync(db, attachedEntity);
            }

            foreach (var property in builder.properties)
            {
                property.Modify(db, attachedEntity, null);
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
        /// Creates a detached shallow clone from the given entity
        /// </summary>
        /// <typeparam name="E">The entity to clone.</typeparam>
        /// <param name="arbitraryContext">A context providing the model - this doesn't have to be a context the entity is attached to</param>
        /// <param name="entity"></param>
        /// <returns></returns>
        public static E CreateDetachedShallowClone<E>(this DbContext arbitraryContext, E entity)
            where E : class
            => arbitraryContext.Entry(entity).CurrentValues.Clone().ToObject() as E;

        public static void SetState<E>(this DbContext db, E entity, EntityState state, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            ForEach(db, entity, (db2, e) => db2.Entry(e).State = state, extent);
        }

        public static void ForEach<E>(this DbContext db, E entity, Action<DbContext, Object> action, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            action(db, entity);

            if (extent is not null)
            {
                var builder = new ExtentBuilder<E>();
                extent(builder);

                foreach (var reconciler in builder.reconcilers)
                {
                    reconciler.ForEach(db, entity, action);
                }
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

        internal static E CloneEntity<E>(E entity)
            where E : class
        {
            var type = entity.GetType();

            var clone = (E)Activator.CreateInstance(type);

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                property.SetValue(clone, property.GetValue(entity));
            }

            return clone;
        }
    }
}
