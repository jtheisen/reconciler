using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.ComponentModel.DataAnnotations;

#if EF6
using System.Data.Entity;
#endif

#if EFCORE
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
#endif

namespace Reconciler.Tests
{
    [DebuggerDisplay("Id={Id}")]
    class Person
    {
        public Person()
        {
            Tags = new List<PersonTag>();
            EmailAddresses = new List<EmailAddress>();
        }

        public Guid Id { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset ModifiedAt { get; set; }

        public Guid AddressId { get; set; }

        public Address Address { get; set; }

        public ICollection<PersonTag> Tags { get; set; }

        public ICollection<EmailAddress> EmailAddresses { get; set; }
    }

    [DebuggerDisplay("Id={Id},{PersonId}->{TagId}")]
    class PersonTag
    {
        public Guid Id { get; set; }

        public DateTimeOffset? DeletedAt { get; set; }

        public Guid PersonId { get; set; }

        public Guid TagId { get; set; }

        public Person Person { get; set; }

        public Tag Tag { get; set; }

        public PersonTagPayload Payload { get; set; }
    }

    class PersonTagPayload
    {
        [Key]
        public Guid PersonTagId { get; set; }
    }

    [DebuggerDisplay("Id={Id}")]
    class Tag
    {
        public Guid Id { get; set; }

        public Int32 No { get; set; }
    }

    [DebuggerDisplay("Id={Id}")]
    class Address
    {
        public Guid Id { get; set; }

        public String City { get; set; }

        public AddressImage Image { get; set; }
    }

    [DebuggerDisplay("AddressId={AddressId}")]
    class AddressImage
    {
        [Key]
        public Guid AddressId { get; set; }

        public String EncodedImageData { get; set; }
    }

    [DebuggerDisplay("Id={Id}")]
    class EmailAddress
    {
        public int Id { get; set; }

        public Guid PersonId { get; set; }

        public Person Person { get; set; }

        public String Email { get; set; }
    }

    class Context : DbContext
    {
        public DbSet<Person> People { get; set; }
        public DbSet<Address> Addresses { get; set; }
        public DbSet<AddressImage> AddressImages { get; set; }
        public DbSet<PersonTag> PersonTags { get; set; }
        public DbSet<PersonTagPayload> PersonTagPayloads { get; set; }
        public DbSet<Tag> Tags { get; set; }
        public DbSet<EmailAddress> EmailAddresses { get; set; }

#if EF6

        // Connection string only relevant for EF6 as EF Core uses in-memory testing.
        public Context()
            : base(GetConnectionString())
        {
        }

        static String GetConnectionString()
        {
            Console.WriteLine($"env.appveyor={Environment.GetEnvironmentVariable("appveyor")}");

            return Environment.GetEnvironmentVariable("appveyor") == null
                ? "Context" // use app.config
                : @"Server=(local)\SQL2017;Database=reconcileref6;User ID=sa;Password=Password12!";
        }

        protected override void OnModelCreating(DbModelBuilder builder)
        {
            builder.Entity<Address>().HasOptional(a => a.Image).WithRequired();
            builder.Entity<PersonTag>().HasOptional(a => a.Payload).WithRequired();
        }
#endif

#if EFCORE
        public Context()
        {
            ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges;
            ChangeTracker.DeleteOrphansTiming = CascadeTiming.OnSaveChanges;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlServer(@"Data Source=.\;Initial Catalog=reconcilerefcore;Integrated Security=true");
            //optionsBuilder.UseInMemoryDatabase("Reconciler");
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<Address>().HasOne(a => a.Image).WithOne();
            builder.Entity<PersonTag>().HasOne(t => t.Payload).WithOne();
        }
#endif
    }
}
