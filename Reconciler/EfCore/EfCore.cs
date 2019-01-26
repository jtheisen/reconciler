using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace MonkeyBusters.Reconciliation.Internal
{
    /// <summary>
    /// EF Core doesn't have EF6's EntityKey class, so we need to
    /// replicate it here.
    /// </summary>
    class EntityKey
    {
        private readonly KeyValuePair<String, Object>[] pairs;
        private readonly Int32 hash;

        internal EntityKey(KeyValuePair<String, Object>[] pairs)
        {
            this.pairs = pairs;

            foreach (var pair in pairs)
            {
                hash ^= pair.Value.GetHashCode();
            }
        }

        public KeyValuePair<String, Object>[] EntityKeyValues => pairs;

        public override Boolean Equals(Object obj)
            => obj is EntityKey other
                && other.pairs.Length == pairs.Length
                && other.pairs.Zip(pairs, (l, r) => l.Value.Equals(r.Value)).All(b => b);

        public override Int32 GetHashCode() => hash;

        public static Boolean operator ==(EntityKey lhs, EntityKey rhs) => lhs?.Equals(rhs) == true;
        public static Boolean operator !=(EntityKey lhs, EntityKey rhs) => lhs?.Equals(rhs) == false;
    }

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

            var keyDefinition = db.Model.FindEntityType(typeof(E)).FindPrimaryKey();

            var pairs = keyDefinition.Properties
                .Select(p => new KeyValuePair<String, Object>(p.PropertyInfo?.Name, p.PropertyInfo?.GetValue(entity)))
                .ToArray();

            if (pairs.Any(v => v.Key == null)) throw new Exception("The entity has a primary key that isn't mapped to proper properties.");
            if (pairs.Any(v => v.Value == null)) throw new Exception("The entity has at least partially a null primary key.");

            return new EntityKey(pairs);
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
            var keyDefinition = db.Model.FindEntityType(typeof(E)).FindPrimaryKey();
            var properties = keyDefinition.Properties;

            var parameter = Expression.Parameter(typeof(E));
            var expression = CreateEqualsExpression(entity, properties[0].PropertyInfo, parameter);
            for (int i = 1; i < properties.Count; i++)
                expression = Expression.And(
                    expression,
                    CreateEqualsExpression(entity, properties[i].PropertyInfo, parameter));

            var where = Expression.Lambda<Func<E, Boolean>>(expression, parameter);

            var query = db.Set<E>().Where(where);

            return query;
        }

        private static Expression CreateEqualsExpression(object entity, PropertyInfo keyProperty, Expression parameter)
        {
            return Expression.Equal(
                Expression.Property(parameter, keyProperty),
                Expression.Constant(keyProperty.GetValue(entity, null), keyProperty.PropertyType)
            );
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
            db.Set<E>().Remove(existingEntity);
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
            var newEntity = Activator.CreateInstance(templateEntity.GetType()) as E;

            db.Entry(newEntity).CurrentValues.SetValues(templateEntity);
            db.Set<E>().Add(newEntity);

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
            entry.CurrentValues.SetValues(templateEntity);
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
