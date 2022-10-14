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
            ClearDb();
        }

        void ClearDbSet<T>(DbSet<T> set) where T : class
        {
            set.RemoveRange(set.ToArray());
        }

        void ClearDb()
        {
            var db = new Context();
            ClearDbSet(db.People);
            ClearDbSet(db.Addresses);
            ClearDbSet(db.AddressImages);
            ClearDbSet(db.PersonTags);
            ClearDbSet(db.PersonTagPayloads);
            ClearDbSet(db.Tags);
            ClearDbSet(db.EmailAddresses);
            db.SaveChanges();
        }

        void SaveGraph<E>(E entity)
            where E : class
        {
            var db = new Context();
            db.Set<E>().Add(entity);
            db.SaveChanges();
        }

        class GraphOptions
        {
            public Guid? AddressId { get; set; }

            public String City { get; set; }

            public List<EmailAddress> EmailsToCopy { get; set; }

            public Boolean IncludeAddressImage { get; set; }
            public Boolean IncludeTagPayload { get; set; }

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
                    Payload = options?.IncludeTagPayload == true
                        ? new PersonTagPayload { PersonTagId = id }
                        : null
                };
            }).Where(t => options?.IncludeTag?.Invoke(t.Tag) ?? false).ToList();

            var emailsToCopy = options?.EmailsToCopy ?? new List<EmailAddress>();
            var emails = emailsToCopy.Select(e => new EmailAddress { Email = e.Email, PersonId = personId, Id = e.Id }).ToList();

            return new Person
            {
                Id = personId,
                AddressId = addressId,
                Address = new Address
                {
                    Id = addressId,
                    City = options?.City ?? "Bochum",
                    Image = options?.IncludeAddressImage == true
                        ? new AddressImage { AddressId = addressId }
                        : null
                },
                EmailAddresses = emails,
                Tags = personTags,
            };
        }


        List<EmailAddress> MakeEmailAddresses(int start, int count)
        {
            return Enumerable.Range(start, count).Select(i => new EmailAddress { Email = $"test{i}@test.com" }).ToList();
        }

        void TestGraph<E>(E original, E target, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            TestGraph(original, target, extent, out var reloadedTarget, out var reloadedUpdate);

            AssertGraphEquality(original, reloadedTarget, reloadedUpdate);
        }

        void TestGraph<E>(E original, E target, Action<ExtentBuilder<E>> extent, out E reloadedTarget, out E reloadedUpdate)
            where E : class
        {
            SaveGraph(target);

            reloadedTarget = new Context().LoadExtent(target, extent);

            ClearDb();

            SaveGraph(original);

            {
                var db = new Context();
                var attachedEntity = db.Reconcile(target, extent);
                //var entries = db.ChangeTracker.Entries().Select(e => new EntityWithState { Entry = e }).ToArray();
                db.SaveChanges();
            }

            reloadedUpdate = new Context().LoadExtent(target, extent);

            new Context().Normalize(original, extent);
            new Context().Normalize(reloadedTarget, extent);
            new Context().Normalize(reloadedUpdate, extent);
        }

        private static void AssertGraphEquality<E>(E original, E reloadedTarget, E reloadedUpdate) where E : class
        {
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
        public void TestPrematureCascading()
        {
            SaveGraph(MakeGraph());

            var db = new Context();

            var person = db.People.Include(p => p.Address).Single();

            var oldAddress = person.Address;
            var address = new Address { Id = Guid.NewGuid(), City = "foo" };
            db.Addresses.Add(address);
            // This needs to happen before the removal in newer versions of
            // EF Core (it was not necessary in 2.1 but is in 6.0).
            // This test is just to produce the issue by putting the assignment
            // after the following removal.
            person.AddressId = address.Id;
            db.Addresses.Remove(oldAddress);

            db.SaveChanges();

            person = db.People.Single();
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
                MakeGraph(new GraphOptions { IncludeTag = t => t.No % 2 == 0, IncludeTagPayload = true }),
                MakeGraph(new GraphOptions { IncludeTag = t => t.No % 3 == 0, IncludeTagPayload = true }),
                map => map
                    .WithOne(p => p.Address)
                    .WithMany(p => p.Tags, with => with
                        .WithOne(e => e.Payload)
                        .WithShared(e => e.Tag))
            );
        }

        [TestMethod]
        public void TestImmutable()
        {
            TestGraph(
                MakeGraph(new GraphOptions { City = "Bochum" }),
                MakeGraph(new GraphOptions { City = "Witten" }),
                map => map
                    .WithOne(p => p.Address, map2 => map2
                        .WithReadOnly(a => a.City)),
                out var reloadedTarget, out var reloadedUpdate
            );

            Assert.AreEqual("Bochum", reloadedUpdate.Address.City);
        }

        [TestMethod]
        public void TestBlacked()
        {
            var graph = MakeGraph(new GraphOptions { City = "Bochum" });

            SaveGraph(graph);

            Action<ExtentBuilder<Person>> GetExtent()
            {
                return map => map
                    .WithOne(p => p.Address, map2 => map2
                        .WithBlacked(a => a.City)
                    )
                ;
            };

            var reconciled = new Context().Reconcile(graph, GetExtent());

            var reloaded = new Context().LoadExtent(graph, GetExtent());

            Assert.AreEqual(null, reconciled.Address.City);
            Assert.AreEqual(null, reloaded.Address.City);
        }

        [TestMethod]
        public void TestFixes()
        {
            Action<ExtentBuilder<Person>> GetExtent()
            {
                return map => map
                    .WithOne(p => p.Address)
                    .OnInsertion(p => p.CreatedAt == DateTimeOffset.Now)
                    .OnUpdate(p => p.ModifiedAt == DateTimeOffset.Now)
                ;
            };

            new Context().ReconcileAndSaveChanges(MakeGraph(), GetExtent());

            var initialPerson = new Context().LoadExtent(MakeGraph(), GetExtent());

            new Context().ReconcileAndSaveChanges(MakeGraph(), GetExtent());

            var updatedPerson = new Context().LoadExtent(MakeGraph(), GetExtent());

            Assert.AreEqual(initialPerson.CreatedAt, updatedPerson.CreatedAt);
            Assert.AreNotEqual(initialPerson.ModifiedAt, updatedPerson.ModifiedAt);
        }

        [TestMethod]
        public void TestAddManyWithAutoIncrementId()
        {
            Action<ExtentBuilder<Person>> extent = map => map.WithOne(p => p.Address)
                                                             .WithMany(p => p.EmailAddresses);

            new Context().ReconcileAndSaveChanges(MakeGraph(new GraphOptions { EmailsToCopy = MakeEmailAddresses(1, 2) }), extent);

            var initialPerson = new Context().LoadExtent(MakeGraph(), extent);

            new Context().ReconcileAndSaveChanges(MakeGraph(new GraphOptions { EmailsToCopy = initialPerson.EmailAddresses.ToList() }), extent);

            var updatedPerson = new Context().LoadExtent(MakeGraph(), extent);

            Assert.AreEqual(updatedPerson.EmailAddresses.Count, 2);
            AssertGraphEquality(MakeGraph(), initialPerson, updatedPerson);

        }

        [TestMethod]
        public void TestRemoveAndAddManyWithAutoIncrementId()
        {
            Action<ExtentBuilder<Person>> extent = map => map.WithOne(p => p.Address)
                                                             .WithMany(p => p.EmailAddresses);

            new Context().ReconcileAndSaveChanges(MakeGraph(new GraphOptions { EmailsToCopy = MakeEmailAddresses(1, 2) }), extent);

            var initialPerson = new Context().LoadExtent(MakeGraph(), extent);
            var initialEmails = initialPerson.EmailAddresses.OrderBy(e => e.Email);

            var changedEmails = initialEmails.Skip(1).Concat(MakeEmailAddresses(3, 2)).ToList();

            new Context().ReconcileAndSaveChanges(MakeGraph(new GraphOptions { EmailsToCopy = changedEmails }), extent);

            var updatedPerson = new Context().LoadExtent(MakeGraph(), extent);
            var updatedEmails = updatedPerson.EmailAddresses.OrderBy(e => e.Email);

            Assert.AreEqual(initialPerson.EmailAddresses.Count, 2);
            Assert.AreEqual(updatedPerson.EmailAddresses.Count, 3);
            Assert.AreEqual(changedEmails.First().Id, updatedEmails.First().Id);
            Assert.IsTrue(Enumerable.SequenceEqual(changedEmails.Select(e => e.Email), updatedEmails.Select(e => e.Email)));
        }


        [TestMethod]
        public void TestMoveOneOrphaneOtAnotherParrent()
        {
            Action<ExtentBuilder<Person>> extent = map => map.WithMany(p => p.EmailAddresses)
                                                             .WithOne(p => p.Address);

            Context context = new Context();

            var grapth1 = MakeGraph(new GraphOptions { EmailsToCopy = MakeEmailAddresses(1, 2), AddressId = Guid.NewGuid() });
            grapth1.Id = Guid.NewGuid();

            foreach (var item in grapth1.EmailAddresses)
                item.PersonId = grapth1.Id;

            var grapth2 = MakeGraph(new GraphOptions { EmailsToCopy = MakeEmailAddresses(1, 2), AddressId = Guid.NewGuid() });
            grapth2.Id = Guid.NewGuid();

            foreach (var item in grapth2.EmailAddresses)
                item.PersonId = grapth2.Id;

            context.Reconcile(grapth1, extent);
            context.Reconcile(grapth2, extent);

            context.SaveChanges();

            grapth1 = context.People.FirstOrDefault(p => p.Id == grapth1.Id);
            grapth2 = context.People.FirstOrDefault(p => p.Id == grapth2.Id);

            context = new Context();

            //load entity to local context
            context.People.FirstOrDefault(p => p.Id == grapth1.Id);
            context.People.FirstOrDefault(p => p.Id == grapth2.Id);

            var firstAddress = grapth2.EmailAddresses.FirstOrDefault();

            grapth2.EmailAddresses.Remove(firstAddress);

            firstAddress.PersonId = grapth1.Id;
            grapth1.EmailAddresses.Add(firstAddress);

            grapth2 = context.Reconcile(grapth2, extent);
            grapth1 = context.Reconcile(grapth1, extent);
            
            context.SaveChanges();

            grapth1 = context.People.FirstOrDefault(p => p.Id == grapth1.Id);
            grapth2 = context.People.FirstOrDefault(p => p.Id == grapth2.Id);

            Assert.AreEqual(grapth1.EmailAddresses.Count, 3);
            Assert.AreEqual(grapth2.EmailAddresses.Count, 1);
        }
    }
}
