using Microsoft.EntityFrameworkCore;

#nullable enable

namespace RevertChangesTesting;

public class Company
{
    public Guid Id { get; set; }

    public ICollection<Person> Employees { get; set; } = new HashSet<Person>();

    public ICollection<ContactNoNavProp> Contacts { get; set; } = new HashSet<ContactNoNavProp>();

    public Guid? InvoiceCollectionId { get; set; }

    public InvoiceCollection? InvoiceCollection { get; set; }
}

public class Person
{
    public Guid Id { get; set; }

    public String? Name { get; set; }

    public Guid? EmployerId { get; set; }

    public Company? Employer { get; set; }

    public Guid? InvoiceCollectionId { get; set; }

    public InvoiceCollection? InvoiceCollection { get; set; }
}

public class ContactNoNavProp
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }
}

public class InvoiceCollection
{
    public Guid Id { get; set; }

    public ICollection<Invoice> Invoices { get; set; } = new HashSet<Invoice>();

    public ICollection<InvoicePosition> Positions { get; set; } = new HashSet<InvoicePosition>();
}

public class Invoice
{
    public Guid CollectionId { get; set; }

    public Guid InvoiceId { get; set; }

    public InvoiceCollection? Collection { get; set; }

    public ICollection<InvoicePosition> Positions { get; set; } = new HashSet<InvoicePosition>();
}

public class InvoicePosition
{
    public Guid CollectionId { get; set; }

    public Guid InvoiceId { get; set; }

    public Int32 Position { get; set; }

    public InvoiceCollection? Collection { get; set; }

    public Invoice? Invoice { get; set; }
}

public class SampleDbContext : DbContext
{
    public DbSet<Company> Companies { get; set; } = null!;
    public DbSet<ContactNoNavProp> Contacts { get; set; } = null!;
    public DbSet<Person> People { get; set; } = null!;
    public DbSet<InvoiceCollection> InvoiceCollections { get; set; } = null!;
    public DbSet<Invoice> Invoices { get; set; } = null!;
    public DbSet<InvoicePosition> InvoicePositions { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        optionsBuilder.UseInMemoryDatabase("sample");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Person>()
            .HasOne(e => e.InvoiceCollection)
            .WithMany()
            .HasForeignKey(e => e.InvoiceCollectionId)
            ;

        modelBuilder.Entity<Company>()
            .HasOne(e => e.InvoiceCollection)
            .WithMany()
            .HasForeignKey(e => e.InvoiceCollectionId)
            ;
        modelBuilder.Entity<Company>()
            .HasMany(e => e.Contacts)
            .WithOne()
            .HasForeignKey(e => e.CompanyId)
            ;

        modelBuilder.Entity<Invoice>()
            .HasKey(p => new { p.CollectionId, p.InvoiceId })
            ;
        modelBuilder.Entity<Invoice>()
            .HasOne(e => e.Collection)
            .WithMany(e => e.Invoices)
            .HasForeignKey(e => e.CollectionId)
            ;

        modelBuilder.Entity<InvoicePosition>()
            .HasKey(p => new { p.CollectionId, p.InvoiceId, p.Position })
            ;
        modelBuilder.Entity<InvoicePosition>()
            .HasOne(e => e.Invoice)
            .WithMany(e => e.Positions)
            .HasForeignKey(e => new { e.CollectionId, e.InvoiceId })
            ;
        modelBuilder.Entity<InvoicePosition>()
            .HasOne(e => e.Collection)
            .WithMany(e => e.Positions)
            .HasForeignKey(e => e.CollectionId)
            ;

    }
}
