using System.Reflection;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections;
using Microsoft.EntityFrameworkCore;

#nullable enable

namespace MonkeyBusters.EntityFramework
{
    internal abstract class CollectionHelper
    {
        protected abstract void AddOrRemove(IEnumerable collection, Object entity, Boolean actuallyAdd);

        public abstract Boolean IsCollectionT(IEnumerable? collection);

        public static void AddOrRemove(IEnumerable? collection, Object entity, Type type, Boolean actuallyAdd, Boolean throwIfTypeMismatch = false)
        {
            var helper = Get(type);

            if (collection is null)
            {
                throw new ArgumentNullException(nameof(collection));
            }
            else if (helper.IsCollectionT(collection))
            {
                helper.AddOrRemove(collection, entity, actuallyAdd);
            }
            else if (throwIfTypeMismatch)
            {
                throw new ArgumentException($"Enumerable type {collection.GetType()} was expected to be a ICollection<{type}>");
            }
        }

        static CollectionHelper Create(Type type)
        {
            var genericType = typeof(CollectionHelper<>).MakeGenericType(type);

            return Activator.CreateInstance(genericType) as CollectionHelper ?? throw new Exception("Activation failed");
        }

        static ConcurrentDictionary<Type, CollectionHelper> helpers = new ConcurrentDictionary<Type, CollectionHelper>();

        static CollectionHelper Get(Type type) => helpers.GetOrAdd(type, Create);
    }

    internal class CollectionHelper<T> : CollectionHelper
    {
        public override Boolean IsCollectionT(IEnumerable? collection) => collection is ICollection<T>;

        protected override void AddOrRemove(IEnumerable collection, Object entity, Boolean actuallyAdd)
        {
            if (collection is not ICollection<T> target) throw new ArgumentException($"Enumerable type {collection.GetType()} was expected to be a ICollection<{typeof(T)}>");

            if (entity is not T item) throw new ArgumentException($"Entity of type {entity.GetType()} was expected to be a {typeof(T)}");

            if (actuallyAdd)
            {
                target.Add(item);
            }
            else
            {
                target.Remove(item);
            }
        }
    }

    /// <summary>
    /// This exception is thrown when the only-nav-props-test fails. Set it's static IsEnabled
    /// property to false to prevent it from being thrown automatically on a call to `RevertChanges`.
    /// </summary>
    public class DbContextHasOnlyNavPropsAdvisoryException : Exception
    {
        /// <summary>
        /// If set to false, `RevertChanges` will no longer throw an exception if it
        /// deems the context type problematic.
        /// </summary>
        public static Boolean IsEnabled { get; set; } = true;

        const String message = @"
Your DbContext has entity types with collection navigation properties
that don't have a corresponding scalar navigation property on the related
entity. This is required for RevertChanges to work properly.

This is an advisory exception and can be disabled by setting
`DbContextHasOnlyNavPropsAdvisoryException.IsEnabled` to false in
your application setup code.

For the respective entity types, RevertChanges will not remove added
entities from these collections.

An implementation without this restriction is possible but either
very complex or requiring Entity Framework's internal APIs.

Alternatively, you can upgrade to EF Core 7, for which the implementation
is trivial.

The respective entity types and their properties are:

";

        internal DbContextHasOnlyNavPropsAdvisoryException(String report)
            : base(message + report)
        {
        }
    }

    public static class Extensions
    {
        static Int32 efMajorVersion = 0;

        public static Int32 GetEfMajorVersion()
        {
            if (efMajorVersion > 0) return efMajorVersion;
            
            var version = typeof(DbContext).Assembly.GetName().Version;

            efMajorVersion = version?.Major ?? 6;

            return efMajorVersion;
        }

        /// <summary>
        /// Check whether the given context has the corresponding scalar nav prop
        /// to each collection nav prop. This is required by `RevertChanges`.
        /// </summary>
        /// <param name="context">The context to check.</param>
        /// <param name="throwOnIssue">Throw if the check fails.</param>
        /// <returns>A string containing the "only" collection nav props.</returns>
        /// <exception cref="DbContextHasOnlyNavPropsAdvisoryException"></exception>
        public static String CheckContextForOnlyNavProps(this DbContext context, Boolean throwOnIssue = false)
        {
            var onlyNavProps =
                from t in context.Model.GetEntityTypes()
                from n in t.GetNavigations()
                where n.IsCollection && n.Inverse is null
                select $"{t.ShortName()}.{n.Name}";

            var report = String.Join("\n", onlyNavProps);

            if (throwOnIssue)
            {
                throw new DbContextHasOnlyNavPropsAdvisoryException(report);
            }

            return report;
        }

        static Boolean oneContextChecked = false;

        static void CheckContextForOnlyNavPropsOnce(this DbContext context)
        {
            // For efficiency reasons, we only check for the first context RevertChanges is
            // called on and only if a debugger is attached. This should create enough attention
            // without causing issues.

            if (!oneContextChecked && DbContextHasOnlyNavPropsAdvisoryException.IsEnabled && Debugger.IsAttached)
            {
                context.CheckContextForOnlyNavProps(throwOnIssue: true);
            }

            oneContextChecked = true;
        }

        /// <summary>
        /// Reverts properties of all tracked entities to their unmodified state.
        /// </summary>
        /// <param name="context">The context to revert.</param>
        public static void RevertChanges(this DbContext context)
        {
            var v = GetEfMajorVersion();

            if (v >= 7)
            {
                context.RevertChangesEf7();
            }
            else
            {
                context.RevertChangesEf6();
            }
        }

        static void RevertChangesEf7(this DbContext context)
        {
            foreach (var entry in context.ChangeTracker.Entries().ToArray())
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        entry.State = EntityState.Detached;
                        break;
                    case EntityState.Deleted:
                    case EntityState.Modified:
                        entry.State = EntityState.Modified;
                        entry.State = EntityState.Unchanged;
                        break;
                    default:
                        break;
                }
            }
        }

        static void RevertChangesEf6(this DbContext context)
        {
            context.CheckContextForOnlyNavPropsOnce();

            foreach (var entry in context.ChangeTracker.Entries().ToArray())
            {
                switch (entry.State)
                {
                    case EntityState.Deleted:
                        // This way, EF reverts the changes even for deleted entities.
                        entry.State = EntityState.Modified;
                        entry.State = EntityState.Unchanged;

                        // No collections need to change as that is done by EF only on successful deletion at SaveChanges.

                        break;
                    case EntityState.Modified:
                        entry.State = EntityState.Unchanged;

                        break;
                    case EntityState.Added:
                        // Only in this case do collection need changing: EF modifies collections prior to SaveChanges'
                        // successful completion and those changes need reverting. The following will only work if
                        // all collection nav props have a corresponding scalar nav prop on the other end.

                        // Bizarrely, in the case of a missing scalar nav prop, entry.Navigations just as much missing
                        // the relationship entry as entry.References is. There's no easy way to check for this case.

                        foreach (var reference in entry.References)
                        {
                            var nav = reference.Metadata;

                            if (nav.Inverse?.PropertyInfo is not PropertyInfo relatedProperty) continue;

                            if (reference.CurrentValue is not Object relatedObject) continue;

                            var collectionObject = relatedProperty.GetValue(relatedObject);

                            CollectionHelper.AddOrRemove(collectionObject as IEnumerable, entry.Entity, entry.Metadata.ClrType, false, true);
                        }

                        entry.State = EntityState.Detached;

                        break;
                    case EntityState.Detached:
                    case EntityState.Unchanged:
                    default:
                        break;
                }
            }
        }
    }
}
