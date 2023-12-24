using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonkeyBusters.EntityFramework;

#nullable enable

namespace RevertChangesTesting;

/* There seems to be neither rhyme nor reason to when EF fixes navigation properties
 * in relation to SaveChanges:
 * 
 *             scalar nav props      collection nav props
 * Add         before                before
 * Delete      before                after
 */

[TestClass]
public class RevertChangesTests
{
    public delegate void TestFunc(SampleDbContext db, Action check, Action revert);

    [TestMethod]
    [DataRow("Earendil", "Feanor")]
    [DataRow(null, "Feanor")]
    [DataRow("Earendil", null)]
    public void TestProperties(String? originalName, String? newName) => TestRevert((db, check, revert) =>
    {
        var person = new Person
        {
            Id = Guid.NewGuid(),
            Name = originalName
        };

        db.People.Add(person);

        check();

        person.Name = newName;

        revert();

        Assert.AreEqual(originalName, person.Name);
    });

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TestAdd(Boolean checkCollectionInsertion) => TestRevert((db, check, revert) =>
    {
        var company = new Company
        {
            Id = Guid.NewGuid(),
            Employees =
            {
                new Person { Id = Guid.NewGuid() },
                new Person { Id = Guid.NewGuid() }
            }
        };

        db.Companies.Add(company);

        check();

        var newPerson = new Person { Id = Guid.NewGuid(), EmployerId = company.Id };

        db.People.Add(newPerson);

        if (checkCollectionInsertion)
        {
            // EF modifies the collection before saving, check that this is the case
            db.ChangeTracker.DetectChanges();
            CollectionAssert.Contains(company.Employees.ToList(), newPerson);
        }

        revert();

        // Check that it's no longer the case after reverting
        CollectionAssert.DoesNotContain(company.Employees.ToList(), newPerson);
    });

    [TestMethod]
    public void TestRemove() => TestRevert((db, check, revert) =>
    {
        var personToRemove = new Person { Id = Guid.NewGuid() };

        var company = new Company
        {
            Id = Guid.NewGuid(),
            Employees =
            {
                new Person { Id = Guid.NewGuid() },
                personToRemove
            }
        };

        db.Companies.Add(company);

        check();

        db.People.Remove(personToRemove);

        db.ChangeTracker.DetectChanges();
        // This is the only case where EF decides not to touch a collection prior to SaveChanges
        CollectionAssert.Contains(company.Employees.ToList(), personToRemove);

        revert();

        // Check that will still have the person
        CollectionAssert.Contains(company.Employees.ToList(), personToRemove);
    });

    [TestMethod]
    public void TestMissingNavProp()
    {
        var isBeforeEf7 = Extensions.GetEfMajorVersion() < 7;

        TestRevert((db, check, revert) =>
        {
            var company = new Company
            {
                Id = Guid.NewGuid(),
            };

            db.Companies.Add(company);

            check();

            var contactToAdd = new ContactNoNavProp { Id = Guid.NewGuid(), CompanyId = company.Id };

            db.Contacts.Add(contactToAdd);

            // Reverting an entity with a missing scalar nav prop is not supported.
            revert();

            if (isBeforeEf7)
            {
                // Consequently, this fails: (except on EF7 and higher)
                Assert.ThrowsException<Exception>(() =>
                {
                    // Check that it's no longer the case after reverting
                    if (company.Contacts.ToList().Contains(contactToAdd))
                    {
                        throw new Exception("The contact should no longer be here");
                    }
                });
            }
        },
        expectTheUnexpected: isBeforeEf7);
    }

    void MakeInvoiceCollection(out InvoiceCollection invoices, out Invoice invoice, out InvoicePosition invoicePosition)
    {
        invoicePosition = new InvoicePosition { Position = 0 };

        invoice = new Invoice
        {
            InvoiceId = Guid.NewGuid(),
            Positions = { invoicePosition }
        };

        invoices = new InvoiceCollection
        {
            Id = Guid.NewGuid(),
            Invoices = { invoice }
        };
    }

    [TestMethod]
    public void TestNestedAdd() => TestRevert((db, check, revert) =>
    {
        MakeInvoiceCollection(out var invoices, out var invoice, out var invoicePosition);

        db.InvoiceCollections.Add(invoices);

        check();

        var newPosition = new InvoicePosition
        {
            CollectionId = invoices.Id,
            InvoiceId = invoice.InvoiceId,
            Position = invoicePosition.Position + 1
        };

        Assert.IsNull(newPosition.Invoice);

        db.InvoicePositions.Add(newPosition);

        db.ChangeTracker.DetectChanges();
        CollectionAssert.Contains(invoices.Positions.ToList(), newPosition);
        CollectionAssert.Contains(invoice.Positions.ToList(), newPosition);
        Assert.IsNotNull(newPosition.Invoice);

        revert();

        // Check that it's no longer the case after reverting
        CollectionAssert.DoesNotContain(invoices.Positions.ToList(), newPosition);
        CollectionAssert.DoesNotContain(invoice.Positions.ToList(), newPosition);
    });

    [TestMethod]
    public void TestNestedDelete() => TestRevert((db, check, revert) =>
    {
        MakeInvoiceCollection(out var invoices, out var invoice, out var invoicePosition);

        db.InvoiceCollections.Add(invoices);

        check();

        db.InvoicePositions.Remove(invoicePosition);

        db.ChangeTracker.DetectChanges();

        // For collections, EF does *not* remove deleted items before SaveChanges
        CollectionAssert.Contains(invoices.Positions.ToList(), invoicePosition);
        CollectionAssert.Contains(invoice.Positions.ToList(), invoicePosition);

        revert();

        // Check that it's still the case after reverting
        CollectionAssert.Contains(invoices.Positions.ToList(), invoicePosition);
        CollectionAssert.Contains(invoice.Positions.ToList(), invoicePosition);
    });

    [TestMethod]
    public void TestRemoveRelated() => TestRevert((db, check, revert) =>
    {
        var invoicesToRemove = new InvoiceCollection { Id = Guid.NewGuid() };

        var company = new Company
        {
            Id = Guid.NewGuid(),
            InvoiceCollection = invoicesToRemove
        };

        db.Companies.Add(company);

        check();

        db.InvoiceCollections.Remove(invoicesToRemove);

        db.ChangeTracker.DetectChanges();

        // After removing, the respective key property becomes null
        Assert.IsNull(company.InvoiceCollectionId);

        // For scalars, EF *does* remove deleted items before SaveChanges
        Assert.IsNull(company.InvoiceCollection);

        revert();

        // After reverting, it is reset to the original value
        Assert.IsNotNull(company.InvoiceCollectionId);

        Assert.IsTrue(ReferenceEquals(company.InvoiceCollection, invoicesToRemove));
    });

    void TestRevert(TestFunc func, Boolean expectTheUnexpected = false)
    {
        DbContextHasOnlyNavPropsAdvisoryException.IsEnabled = false;

        var db = new SampleDbContext();

        String originalView = null!;

        Boolean didRevert = false;

        void Check()
        {
            db.SaveChanges();

            originalView = db.ChangeTracker.DebugView.LongView;
        }

        void Revert()
        {
            if (didRevert) return;

            db.RevertChanges();

            didRevert = true;
        }

        func(db, Check, Revert);

        Revert();

        var revertedView = db.ChangeTracker.DebugView.LongView;

        if (expectTheUnexpected)
        {
            Assert.AreNotEqual(originalView, revertedView);
        }
        else
        {
            Assert.AreEqual(originalView, revertedView);
        }
    }

    [TestMethod]
    public void TestOnlyNavPropsDetector()
    {
        var db = new SampleDbContext();

        var report = db.CheckContextForOnlyNavProps();

        Assert.AreEqual("Company.Contacts", report);
    }
}
