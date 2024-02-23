using System;
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
                if (templateEntity != null)
                {
                    await db.ReconcileAsync(templateEntity, extent);
                }

                if (attachedEntity != null && removeOrphans)
                {
                    await db.ReconcileAsync(attachedEntity, null, extent);

                    db.RemoveEntity(attachedEntity);
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

        public override IQueryable<E> AugmentInclude(IQueryable<E> query)
            => query.Include(this.selectorExpression);

        public override async Task ReconcileAsync(DbContext db, E attachedEntity, E templateEntity)
        {
            var attachedCollection = selector(attachedEntity);
            var templateCollection = templateEntity != null
                ? selector(templateEntity).ToArray()
                : Enumerable.Empty<F>();

            var toRemove = (
                from o in attachedCollection
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
                await db.ReconcileAsync(e, null, extent);

                db.RemoveEntity(e);
            }

            foreach (var e in toAdd)
            {
                attachedCollection.Add(e);

                db.SetState(e, EntityState.Added, extent);

                //await db.ReconcileAsync(e, extent);

                //db.Entry(e).State = EntityState.Added;

                //db.ChangeTracker.DetectChanges();

                //var state = db.Entry(e).State;
            }

            foreach (var pair in toUpdate)
            {
                await db.ReconcileAsync(pair.AttachedEntity, pair.TemplateEntity, extent);
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
    using MonkeyBusters.Reconciliation.Internal;

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
            return db.ReconcileAsync(null, templateEntity, extent);
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
            var attachedEntity = await db.ReconcileAsync(null, templateEntity, extent);
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

        /// <summary>
        /// Reconciles the stored entity graph extending as far as described by the given extent with the one given by entity.
        /// It makes a number of load requests from the store, but all modifications are merely scheduled in the context.
        /// This overload takes a prefetched attached entity that can allow the skipping of the load request in case of a
        /// trivial extent.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="attachedEntity">The prefetched graph to reconcile.</param>
        /// <param name="templateEntity">The detached graph to reconcile with.</param>
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

            var isNewEntity = attachedEntity == null || (db.Entry(attachedEntity).State == EntityState.Deleted && templateEntity != null);
            
            if (isNewEntity)
            {
                //attachedEntity = db.Add(templateEntity).Entity;
                //return attachedEntity;

                attachedEntity = db.AddEntity(templateEntity);
            }

            // Newer versions of EF Core need the new key values of a replaced related entity
            // set before the old one is deleted (if it's deleted in the same change set); however,
            // we need to preserve the old nav props for the recursive reconciliation work we now have
            // do do after.
            var cloneOfAttachedEntity = Properties.CloneEntity(attachedEntity);

            if (!isNewEntity && templateEntity != null)
            {
                db.UpdateEntity(attachedEntity, templateEntity);
            }

            foreach (var property in builder.properties)
            {
                property.Modify(db, attachedEntity, templateEntity);
            }

            foreach (var reconciler in builder.reconcilers)
            {
                await reconciler.ReconcileAsync(db, cloneOfAttachedEntity, templateEntity);
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
