using System;
using System.Data.Entity;
using System.Data.Entity.Core;
using System.Data.Entity.Core.Objects;
using System.Data.Entity.Infrastructure;
using System.Linq;

namespace MonkeyBusters.Reconciliation.Internal
{
    static class InternalExtensions
    {
        /// <summary>
        /// Gets the key for the given entity.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="entity">The entity.</param>
        /// <returns>The key of the given entity.</returns>
        public static EntityKey GetEntityKey<E>(this DbContext db, E entity)
            where E : class
        {
            if (entity == null) return null;
            var context = ((IObjectContextAdapter)db).ObjectContext;
            var set = context.CreateObjectSet<E>();
            var key = context.CreateEntityKey(set.EntitySet.Name, entity);
            return key;
        }

        /// <summary>
        /// Gets a query to fetch the entity that has the same key as the given detached one.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="entity">The entity the key of which will be used.</param>
        /// <returns>The query that is filtered to match only an entity with the key of the given entity.</returns>
        public static IQueryable<E> GetEntity<E>(this DbContext db, E entity)
            where E : class
        {
            var context = ((IObjectContextAdapter)db).ObjectContext;
            var set = context.CreateObjectSet<E>();
            var key = context.CreateEntityKey(set.EntitySet.Name, entity);

            var where = String.Join(" AND ", key.EntityKeyValues.Select((kv, i) => $"it.{kv.Key}=@p{i}"));
            var parms = key.EntityKeyValues.Select((kv, i) => new ObjectParameter($"p{i}", kv.Value)).ToArray();

            var query = set.Where(where, parms);

            return query;
        }

        /// <summary>
        /// Schedules the entity that has the same key as the given detached one for removal in the context.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="entity">The entity the key of which will be used.</param>
        public static void RemoveEntity<E>(this DbContext db, E entity)
            where E : class
        {
            var key = db.GetEntityKey(entity);
            var existingEntity = db.Set<E>().Local
                .Where(e => db.GetEntityKey(e) == key)
                .FirstOrDefault();
            if (existingEntity == null) throw new Exception("No such entity in context");
            db.Entry(existingEntity).State = EntityState.Deleted;
        }

        /// <summary>
        /// Schedules a detached entity with the same values as the given one for addition in the context.
        /// Related entities are not added as well.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="templateEntity">The entity to take the values from.</param>
        /// <returns>The attached entity.</returns>
        public static E AddEntity<E>(this DbContext db, E templateEntity)
            where E : class
        {
            var newEntity = db.Set<E>().Create<E>();
            db.Set<E>().Add(newEntity);
            db.Entry(newEntity).CurrentValues.SetValues(templateEntity);
            return newEntity;
        }

        /// <summary>
        /// Updates an attached entity from the values of a detached one.
        /// </summary>
        /// <param name="db">The context.</param>
        /// <param name="attachedEntity">The attached entity.</param>
        /// <param name="templateEntity">The entity to take the values from.</param>
        public static void UpdateEntity<E>(this DbContext db, E attachedEntity, E templateEntity)
            where E : class
        {
            var entry = db.Entry(attachedEntity);
            if (entry.State != EntityState.Deleted)
            {
                entry.CurrentValues.SetValues(templateEntity);
            }
        }

        /// <summary>
        /// Formats the entity key in a readable manner.
        /// </summary>
        /// <param name="key">The key to be formatted.</param>
        /// <returns>The formatted key.</returns>
        public static String ToPrettyString(this EntityKey key)
        {
            return $"[{String.Join(",", key.EntityKeyValues.Select(v => $"{v.Key}={v.Value}"))}]";
        }
    }
}
