using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonkeyBusters.Reconciliation.Internal;
using Newtonsoft.Json;

#if EF6
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
#endif

#if EFCORE
using Microsoft.EntityFrameworkCore;
using DbEntityEntry = Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry;
#endif

namespace Reconciler.Tests
{
    [DebuggerDisplay("{Entry.State} {Entry.Entity}")]
    public class EntityWithState
    {
        public DbEntityEntry Entry { get; set; }
    }

    class IdProvider
    {
        static List<Guid> staticIds = new List<Guid>();

        Int32 currentI = 0;

        public Guid GetNext()
        {
            if (currentI >= staticIds.Count)
            {
                staticIds.Add(Guid.NewGuid());
            }
            return staticIds[currentI++];
        }

        public Guid GetNext(Guid? replacement)
        {
            var own = GetNext();
            return replacement ?? own;
        }
    }

    [TestClass]
    public class UnitTest1
    {
        public UnitTest1()
        {
        }

        [TestInitialize]
        public void Initialize()
        {
#if EFCORE
            new Context().Database.EnsureCreated();
#endif
        }

        void ClearDbSet<T>(DbSet<T> set) where T : class
        {
            set.RemoveRange(set.ToArray());
        }

        void PrepareDbWithGraph<E>(E entity)
            where E : class
        {
            var db = new Context();
            ClearDbSet(db.People);
            ClearDbSet(db.Addresses);
            ClearDbSet(db.AddressImages);
            ClearDbSet(db.PersonTags);
            ClearDbSet(db.Tags);
            db.SaveChanges();

            db.Set<E>().Add(entity);

            db.SaveChanges();
        }

        class GraphOptions
        {
            public Guid? AddressId { get; set; }

            public Boolean IncludeAddressImage { get; set; }
            public Boolean IncldueTagPayload { get; set; }

            public Predicate<Tag> IncludeTag { get; set; }
        }

        Person MakeGraph(GraphOptions options = null)
        {
            var idProvider = new IdProvider();

            var personId = idProvider.GetNext();

            var addressId = idProvider.GetNext(options?.AddressId);

            var tags = Enumerable.Range(0, 10)
                .Select(i => new Tag { Id = idProvider.GetNext(), No = i });

            var personTags = tags.Select((t, i) =>
            {
                var id = idProvider.GetNext();
                return new PersonTag
                {
                    Id = id,
                    PersonId = personId,
                    TagId = t.Id,
                    Tag = t,
                    Payload = options?.IncldueTagPayload == true
                        ? new PersonTagPayload { PersonTagId = id }
                        : null
                };
            }).Where(t => options?.IncludeTag?.Invoke(t.Tag) ?? false).ToList();

            return new Person
            {
                Id = personId,
                AddressId = addressId,
                Address = new Address
                {
                    Id = addressId,
                    Image = options?.IncludeAddressImage == true
                        ? new AddressImage { AddressId = addressId }
                        : null
                },
                Tags = personTags
            };
        }

        void TestGraph<E>(E original, E target, Action<ExtentBuilder<E>> mapping)
            where E : class
        {
            PrepareDbWithGraph(target);

            var reloadedTarget = new Context().LoadExtent(target, mapping);

            PrepareDbWithGraph(original);

            {
                var db = new Context();
                var attachedEntity = db.Reconcile(target, mapping);
                var entries = db.ChangeTracker.Entries().Select(e => new EntityWithState { Entry = e }).ToArray();
                db.SaveChanges();
            }

            var reloadedUpdate = new Context().LoadExtent(target, mapping);

            var settings = new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            };

            Assert.AreNotEqual(
                JsonConvert.SerializeObject(original, Formatting.Indented, settings),
                JsonConvert.SerializeObject(reloadedUpdate, Formatting.Indented, settings)
            );

            Assert.AreEqual(
                JsonConvert.SerializeObject(reloadedTarget, Formatting.Indented, settings),
                JsonConvert.SerializeObject(reloadedUpdate, Formatting.Indented, settings)
            );
        }

        [TestMethod]
        public void TestRemoveAndAddOne()
        {
            TestGraph(
                MakeGraph(),
                MakeGraph(new GraphOptions { AddressId = Guid.NewGuid() }),
                map => map.WithOne(p => p.Address)
            );

            Assert.AreEqual(1, new Context().Addresses.Count());
        }

        [TestMethod]
        public void TestRemoveAndAddOneWithNestedStuffAttached()
        {
            TestGraph(
                MakeGraph(new GraphOptions { IncludeAddressImage = true }),
                MakeGraph(new GraphOptions { IncludeAddressImage = true, AddressId = Guid.NewGuid() }),
                map => map.WithOne(p => p.Address, map2 => map2.WithOne(p => p.Image))
            );

            Assert.AreEqual(1, new Context().Addresses.Count());
            Assert.AreEqual(1, new Context().AddressImages.Count());
        }

        [TestMethod]
        public void TestRemoveAndAddMany()
        {
            TestGraph(
                MakeGraph(new GraphOptions { IncludeTag = t => t.No % 2 == 0 }),
                MakeGraph(new GraphOptions { IncludeTag = t => t.No % 3 == 0 }),
                map => map
                    .WithOne(p => p.Address)
                    .WithMany(p => p.Tags, with => with
                        .WithShared(e => e.Tag))
            );
        }

        [TestMethod]
        public void TestRemoveAndAddManyWithNestedStuffAttached()
        {
            TestGraph(
                MakeGraph(new GraphOptions { IncludeTag = t => t.No % 2 == 0 }),
                MakeGraph(new GraphOptions { IncludeTag = t => t.No % 3 == 0 }),
                map => map
                    .WithOne(p => p.Address)
                    .WithMany(p => p.Tags, with => with
                        .WithShared(e => e.Tag))
            );
        }
    }
}
