using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonkeyBusters.Reconciliation.Internal;
using Newtonsoft.Json;
using System.Threading;

#if EF6
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
#endif

#if EFCORE
using Microsoft.EntityFrameworkCore;
using DbEntityEntry = Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using System.Text;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;
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

        Byte[] bytes = new Byte[16];

        public Guid GetNext()
        {
            if (currentI >= staticIds.Count)
            {
                BitConverter.GetBytes(currentI + 1).CopyTo(bytes, 0);
                
                var id = new Guid(bytes);

                staticIds.Add(id);
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
    public class ReconcilerTests
    {
        public ReconcilerTests()
        {
        }

        [TestInitialize]
        public void Initialize()
        {
#if EFCORE
            var loggerFactory = LoggerFactory.Create(logging => logging
                .AddConsole()
                .SetMinimumLevel(LogLevel.Trace)
            );

            Context.StaticReconcilerLogger = loggerFactory.CreateLogger("Reconciler");

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

            ClearDbSet(db.Planets);
            ClearDbSet(db.Moons);
            ClearDbSet(db.Stars);

            ClearDbSet(db.AutoIncManyManys);
            ClearDbSet(db.AutoIncManyOne);
            ClearDbSet(db.AutoIncManys);
            ClearDbSet(db.AutoIncRoots);

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

            public Dictionary<Guid, String> Details { get; set; }

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

        void TestGraph<E>(E original, E target, Action<ExtentBuilder<E>> extent, Boolean reverseIteration = false)
            where E : class
        {
            TestGraph(original, target, extent, out var reloadedTarget, out var reloadedUpdate, reverseIteration);

            AssertGraphEquality(original, reloadedTarget, reloadedUpdate);
        }

        void TestGraph<E>(E original, E target, Action<ExtentBuilder<E>> extent, out E reloadedTarget, out E reloadedUpdate, Boolean reverseIteration = false)
            where E : class
        {
            var targetJson = JsonConvert.SerializeObject(target, Formatting.Indented);

            E CloneTarget() => JsonConvert.DeserializeObject<E>(targetJson);

            SaveGraph(CloneTarget());

            reloadedTarget = new Context().LoadExtent(CloneTarget(), extent);

            ClearDb();

            SaveGraph(original);

            {
                Console.WriteLine("Beginning of test reconciliationto target:\n" + targetJson);

                var db = new Context();

                db.ReverseInteration = reverseIteration;

                db.Reconcile(CloneTarget(), extent);

                db.SaveChanges();
            }

            reloadedUpdate = new Context().LoadExtent(target, extent);

            new Context().Normalize(original, extent);
            new Context().Normalize(reloadedTarget, extent);
            new Context().Normalize(reloadedUpdate, extent);
        }

        E SaveAndReloadGraph<E>(E target, Action<ExtentBuilder<E>> extent)
            where E : class
        {
            SaveGraph(target);

            var reloadedTarget = new Context().LoadExtent(target, extent);

            return reloadedTarget;
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
        [DataRow(false), DataRow(true)]
        public void TestRemoveAndAddMany(Boolean reverseIteration)
        {
            TestGraph(
                MakeGraph(new GraphOptions { IncludeTag = t => t.No % 2 == 0 }),
                MakeGraph(new GraphOptions { IncludeTag = t => t.No % 3 == 0 }),
                map => map
                    .WithOne(p => p.Address)
                    .WithMany(p => p.Tags, with => with
                        .WithShared(e => e.Tag)),
                reverseIteration
            );
        }

        [TestMethod]
        [DataRow(false), DataRow(true)]
        public void TestRemoveAndAddManyWithNestedStuffAttached(Boolean reverseIteration)
        {
            TestGraph(
                MakeGraph(new GraphOptions { IncludeTag = t => t.No % 2 == 0, IncludeTagPayload = true }),
                MakeGraph(new GraphOptions { IncludeTag = t => t.No % 3 == 0, IncludeTagPayload = true }),
                map => map
                    .WithOne(p => p.Address)
                    .WithMany(p => p.Tags, with => with
                        .WithOne(e => e.Payload)
                        .WithShared(e => e.Tag)),
                reverseIteration
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

            Assert.AreEqual(null, reloaded.Address.City);

            // This used to test for null prior to 1.1.1, but we can't help to change this:
            // WithBlacked must behave like WithReadOnly on saving to be useful.
            Assert.AreEqual("Bochum", reconciled.Address.City);
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

            Thread.Sleep(10);

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
        [DataRow(false), DataRow(true)]
        public void TestInsertNestedCollections(Boolean reverseIteration)
        {
            TestGraph(
                new Star { Id = "sun" },
                new Star {
                    Id = "sun",
                    Planets = {
                        new Planet {
                            Id = "mars",
                            StarId = "sun",
                            Moons =
                            {
                                new Moon { Id = "deimos", PlanetId = "mars" },
                                new Moon { Id = "phobos", PlanetId = "mars" }
                            }
                        }
                    }
                },
                map => map.WithMany(e => e.Planets, map2 => map2.WithMany(e2 => e2.Moons)),
                reverseIteration
            );
        }

        [TestMethod]
        [DataRow(false), DataRow(true)]
        public void TestInsertNestedCollectionsAutoInc(Boolean reverseIteration)
        {
            ClearDb();

            var root = SaveAndReloadGraph(new AutoIncRoot { }, e => e.WithMany(e2 => e2.Manys));

            root.Manys.Add(
                new AutoIncMany
                {
                    ManyManys =
                    {
                        new AutoIncManyMany { },
                        new AutoIncManyMany { }
                    }
                }
            );
            root.Manys.Add(
                new AutoIncMany
                {
                    ManyManys =
                    {
                        new AutoIncManyMany { },
                        new AutoIncManyMany { }
                    }
                }
            );

            var db = new Context();

            db.ReverseInteration = reverseIteration;

            db.ReconcileAndSaveChanges(root, m0 => m0
                .WithMany(e0 => e0.Manys, m1 => m1
                    .WithMany(e1 => e1.ManyManys)
                )
            );

            var reloaded = new Context().AutoIncRoots
                .OrderBy(e => e.Id)
                .AsNoTracking()
#if EFCORE
                .Include(e => e.Manys)
                    .ThenInclude(e => e.ManyManys)
#endif
#if EF6
                .Include("Manys.ManyManys")
#endif
                .Single()
                    ;

            Assert.AreEqual(2, reloaded.Manys.Count);
            Assert.AreEqual(2, reloaded.Manys.First().ManyManys.Count);
            Assert.AreEqual(2, reloaded.Manys.Skip(1).First().ManyManys.Count);
        }

        [TestMethod]
        public void TestInsertNestedScalarAutoInc()
        {
            ClearDb();

            var root = SaveAndReloadGraph(new AutoIncRoot { }, e => e.WithMany(e2 => e2.Manys));

            root.Manys.Add(
                new AutoIncMany
                {
                    ManyOne = new AutoIncManyOne { },
                }
            );
            root.Manys.Add(
                new AutoIncMany
                {
                    ManyOne = new AutoIncManyOne { },
                }
            );

            new Context().ReconcileAndSaveChanges(root, m0 => m0
                .WithMany(e0 => e0.Manys, m1 => m1
                    .WithOne(e1 => e1.ManyOne)
                )
            );

            var reloaded = new Context().AutoIncRoots
                .OrderBy(e => e.Id)
                .AsNoTracking()
#if EFCORE
                .Include(e => e.Manys)
                    .ThenInclude(e => e.ManyOne)
#endif
#if EF6
                .Include("Manys.ManyOne")
#endif
                .Single()
                    ;

            Assert.AreEqual(2, reloaded.Manys.Count);
            Assert.IsNotNull(reloaded.Manys.First().ManyOne);
            Assert.IsNotNull(reloaded.Manys.Skip(1).First().ManyOne);
        }

        [TestMethod]
        [DataRow(false), DataRow(true)]
        public void TestMoveSputnikFromEarthToMars(Boolean reverseIteration)
        {
            TestGraph(new Star
                {
                    Id = "sun",
                    Planets = {
                        new Planet {
                            Id = "earth",
                            StarId = "sun",
                            Moons =
                            {
                                new Moon { PlanetId = "earth", Id = "sputnik" }
                            }
                        },
                        new Planet {
                            StarId = "sun",
                            Id = "mars"
                        }
                    }
                }, new Star
                {
                    Id = "sun",
                    Planets = {
                        new Planet {
                            Id = "earth",
                            StarId = "sun"
                        },
                        new Planet {
                            StarId = "sun",
                            Id = "mars",
                            Moons =
                            {
                                new Moon { PlanetId = "mars", Id = "sputnik" }
                            }
                        }
                    }
                }, s => s.WithMany(e => e.Planets, p => p.WithMany(e => e.Moons)),
                reverseIteration
            );
        }

        [TestMethod]
        public void TestOnInsertAndOnUpdate()
        {
            var firstSun = new Star
            {
                Id = "sun",
                Planets = {
                        new Planet {
                            Id = "earth",
                            StarId = "sun"
                        }
                    }
            };

            var extent = ExtentBuilder<Star>.CastAction(es => es
                .WithMany(s => s.Planets, ep => ep
                    .OnInsertion(p => p.Misc == 21 + 21)
                    .OnUpdate(p => p.Misc == p.Misc + 8)
                )
            );

            var first = new Context().ReconcileAndSaveChanges(firstSun, extent);

            var second = new Context().ReconcileAndSaveChanges(firstSun, extent);

            Assert.AreEqual(42, first.Planets.First().Misc);
            Assert.AreEqual(50, second.Planets.First().Misc);

            var afterRemoval = new Context().ReconcileAndSaveChanges(new Star { Id = "sun" }, extent);

            Assert.AreEqual(0, afterRemoval.Planets.Count);
        }

        [TestMethod]
        public void TestBlacked2()
        {
            // Another blacken test just to be sure.

            var firstSun = new Star
            {
                Id = "sun",
                Planets = {
                        new Planet {
                            Id = "earth",
                            StarId = "sun",
                            Misc = 42
                        }
                    }
            };

            new Context().ReconcileAndSaveChanges(firstSun, es => es.WithMany(s => s.Planets));

            // Change the misc to test if it is not erroneously writting with a blackened reconciliation
            firstSun.Planets.First().Misc = 11;

            new Context().ReconcileAndSaveChanges(firstSun, es => es
                .WithMany(s => s.Planets, ep => ep
                    .WithBlacked(p => p.Misc)
                )
            );

            var reloadedBlacked = new Context().LoadExtent(firstSun, es => es
                .WithMany(s => s.Planets, ep => ep
                    .WithBlacked(p => p.Misc)
                )
            );

            var reloadedPlain = new Context().LoadExtent(firstSun, es => es.WithMany(s => s.Planets));

            Assert.AreEqual(0, reloadedBlacked.Planets.First().Misc);
            Assert.AreEqual(42, reloadedPlain.Planets.First().Misc);
        }

        [TestMethod]
        public void TestEntityKeyUniqueness()
        {
            var star0 = new Star { Id = "foo" };
            var moon0 = new Moon { Id = "foo" };

            var star1 = new Star { Id = "foo" };
            var moon1 = new Moon { Id = "foo" };

            var db = new Context();

            Boolean HaveSameKey(Object lhs, Object rhs)
            {
                var lKey = db.GetEntityKey(lhs);
                var rKey = db.GetEntityKey(rhs);

                return lKey == rKey;
            }

            Assert.IsFalse(HaveSameKey(star0, moon0));

            Assert.IsTrue(HaveSameKey(star0, star1));
            Assert.IsTrue(HaveSameKey(moon0, moon1));
        }

#if EF6
        [TestMethod]
        public void InvestigateEf6EntityKeyNullBehavior()
        {
            var db = new Context();

            var root = new AutoIncRoot();

            var key = db.GetEntityKey(root);

            // This is tragic
            Assert.IsNotNull(key);
        }
#endif

#if EFCORE
        [TestMethod]
        [DataRow(null)] // no foreign keys
        [DataRow("earth")] // one foreign key
        [DataRow("mars")] // an inconsistent foreign key
        public void TestKeyConsistencyFixing(String sputnikPlanetId)
        {
            var templateSun = new Star
            {
                Id = "sun",
                Planets = {
                    new Planet {
                        Id = "earth",
                        Moons =
                        {
                            new Moon { PlanetId = sputnikPlanetId, Id = "sputnik" }
                        }
                    },
                    new Planet {
                        Id = "mars"
                    }
                }
            };

            var sun = new Context().CreateDetachedDeepClone(templateSun, s => s.WithMany(e => e.Planets, p => p.WithMany(e => e.Moons)));

            var earth = sun.Planets.Single(e => e.Id == "earth");
            var mars = sun.Planets.Single(e => e.Id == "mars");
            var sputnik = earth.Moons.Single();

            Assert.AreEqual("sun", earth.StarId);
            Assert.AreEqual("sun", mars.StarId);
            Assert.AreEqual("earth", sputnik.PlanetId);
        }

        [TestMethod]
        [DataRow(false), DataRow(true)]
        public void TestNoParentKeysMoveSputnikFromEarthToMars(Boolean reverseIteration)
        {
            TestGraph(new Star
            {
                Id = "sun",
                Planets = {
                        new Planet {
                            Id = "earth",
                            Moons =
                            {
                                new Moon { Id = "sputnik" }
                            }
                        },
                        new Planet {
                            Id = "mars"
                        }
                    }
            }, new Star
            {
                Id = "sun",
                Planets = {
                        new Planet {
                            Id = "earth",
                        },
                        new Planet {
                            Id = "mars",
                            Moons =
                            {
                                new Moon { Id = "sputnik" }
                            }
                        }
                    }
            }, s => s.WithMany(e => e.Planets, p => p.WithMany(e => e.Moons)),
                reverseIteration
            );
        }

        [TestMethod]
        public void InvestigateAddToCollection()
        {
            ClearDb();

            var db = new Context();

            var person = new Person
            {
                Id = Guid.NewGuid(),
                Address = new Address()
            };

            db.People.Add(person);

            db.SaveChanges();

            var personInOtherContext = new Context().Entry(person);

            Assert.AreEqual(EntityState.Detached, personInOtherContext.State);

            Assert.IsTrue(Object.ReferenceEquals(personInOtherContext.Entity, person));

            var clonedPerson = personInOtherContext.CurrentValues.Clone().ToObject();

            Assert.IsFalse(Object.ReferenceEquals(clonedPerson, person));

            var personTag = new PersonTag { Id = Guid.NewGuid() };

            person.Tags.Add(personTag);

            Assert.AreEqual(EntityState.Detached, db.Entry(personTag).State);

            db.ChangeTracker.DetectChanges();

            Assert.AreEqual(EntityState.Modified, db.Entry(personTag).State);
        }

        [TestMethod]
        public void InvestiageAddScalar()
        {
            ClearDb();

            {
                var db = new Context();

                var root1 = new AutoIncRoot();

                var root1many1 = new AutoIncMany();

                root1.Manys.Add(root1many1);

                var root1many1one1 = new AutoIncManyOne();

                root1many1.ManyOne = root1many1one1;

                db.AutoIncRoots.Add(root1);

                db.SaveChanges();
            }

            {
                var db = new Context();

                var root1 = db.AutoIncRoots.Single();

                var root1many1 = db.AutoIncManys.Single();

                var root1many1one1 = db.AutoIncManyOne.Single();
                db.AutoIncManyOne.Remove(root1many1one1);

                var root1many1one2 = new AutoIncManyOne();

                root1many1.ManyOne = root1many1one2;

                db.SaveChanges();
            }
        }

        [TestMethod]
        public void InvestigateChangeCollection()
        {
            ClearDb();

            {
                var db = new Context();

                var root1 = new AutoIncRoot();

                var root1many1 = new AutoIncMany();

                root1.Manys.Add(root1many1);

                var root1many1many1 = new AutoIncManyMany();

                root1many1.ManyManys.Add(root1many1many1);

                db.AutoIncRoots.Add(root1);

                db.SaveChanges();
            }

            {
                var db = new Context();

                var root1 = db.AutoIncRoots.Single();

                var root1many1many1 = db.AutoIncManyManys.Single();

                var root1many2 = new AutoIncMany();

                root1many2.ManyManys.Add(root1many1many1);

                root1.Manys.Add(root1many2);

                db.SaveChanges();
            }
        }

        [TestMethod]
        public void InvestigateMoveSputnikFromEarthToMars()
        {
            ClearDb();

            var db = new Context();

            Planet earth, mars;

            Moon sputnik;

            var sun = new Star
            {
                Id = "sun",
                Planets = {
                    (earth = new Planet {
                        Id = "earth",
                        Moons =
                        {
                            (sputnik = new Moon { Id = "sputnik" })
                        }
                    }),
                    (mars = new Planet {
                        Id = "mars"
                    })
                }
            };

            db.Stars.Add(sun);

            db.SaveChanges();

            earth.Moons.Clear();

            Assert.AreEqual(EntityState.Modified, db.Entry(sputnik).State);
            Assert.IsNull(sputnik.PlanetId);

            mars.Moons.Add(sputnik);

            db.ChangeTracker.DetectChanges();

            Assert.AreEqual(EntityState.Modified, db.Entry(sputnik).State);
            Assert.AreEqual(mars.Id, sputnik.PlanetId);
        }

        [TestMethod]
        public void InvestigateMoveSputnikToMarsFromEarth()
        {
            ClearDb();

            var db = new Context();

            Planet earth, mars;

            Moon sputnik;

            var sun = new Star
            {
                Id = "sun",
                Planets = {
                    (earth = new Planet {
                        Id = "earth",
                        Moons =
                        {
                            (sputnik = new Moon { Id = "sputnik" })
                        }
                    }),
                    (mars = new Planet {
                        Id = "mars"
                    })
                }
            };

            db.Stars.Add(sun);

            db.SaveChanges();

            mars.Moons.Add(sputnik);

            db.ChangeTracker.DetectChanges();

            Assert.AreEqual(mars.Id, sputnik.PlanetId);
            Assert.AreEqual(0, earth.Moons.Count);
        }
#endif

        [TestMethod]
        public void InvestigateGeneratedKeysRemainDefault()
        {
            // Database generated keys don't become something weird
            // but stay their default.

            var db = new Context();

            var root1 = new AutoIncRoot
            {
                Manys =
                {
                    new AutoIncMany()
                }
            };

            var root1many1 = root1.Manys.Single();

            db.AutoIncRoots.Add(root1 = new AutoIncRoot());

            db.ChangeTracker.DetectChanges();

            Assert.AreEqual(0, root1.Id);
            Assert.AreEqual(0, root1many1.Id);
        }

        [TestMethod]
        public void InvestigateKeyFixup()
        {
            // Key fixup only works in EF Core

            var db = new Context();

            var sun = new Star
            {
                Id = "sun",
                Planets =
                {
                    new Planet
                    {
                        Id = "earth"
                    }
                }
            };

            var earth = sun.Planets.First();

            db.Stars.Add(sun);

            db.ChangeTracker.DetectChanges();

#if EFCORE
            Assert.AreEqual("sun", earth.StarId);
#endif
#if EF6
            Assert.IsNull(earth.StarId);
#endif
        }
    }
}
