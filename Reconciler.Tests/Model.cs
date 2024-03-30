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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
#endif

namespace Reconciler.Tests
{
    class Star
    {
        public String Id { get; set; }

        public ICollection<Planet> Planets { get; set; } = new List<Planet>();
    }

    class Planet
    {
        public String Id { get; set; }

        public String StarId { get; set; }

        public Star Star { get; set; }

        public Int32 Misc { get; set; }

        public ICollection<Moon> Moons { get; set; } = new List<Moon>();
    }

    class Moon
    {
        public String Id { get; set; }

        public Planet Planet { get; set; }

        public String PlanetId { get; set; }
    }

    class AutoIncRoot
    {
        public Int32 Id { get; set; }

        public ICollection<AutoIncMany> Manys { get; set; } = new List<AutoIncMany>();
    }

    class AutoIncMany
    {
        public Int32 Id { get; set; }

        public Int32 RootId { get; set; }

        public AutoIncRoot Root { get; set; }

        public Int32? ManyOneId { get; set; }

        public AutoIncManyOne ManyOne { get; set; }

        public ICollection<AutoIncManyMany> ManyManys { get; set; } = new List<AutoIncManyMany>();
    }

    class AutoIncManyMany
    {
        public Int32 Id { get; set; }

        public Int32 ManyId { get; set; }

        public AutoIncMany Many { get; set; }
    }

    class AutoIncManyOne
    {
        public Int32 Id { get; set; }
    }

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
#if EFCORE
        , IReconcilerLoggerProvider
#endif
        , IReconcilerDiagnosticsSettings
    {
        public DbSet<Star> Stars { get; set; }
        public DbSet<Planet> Planets { get; set; }
        public DbSet<Moon> Moons { get; set; }

        public DbSet<AutoIncRoot> AutoIncRoots { get; set; }
        public DbSet<AutoIncMany> AutoIncManys { get; set; }
        public DbSet<AutoIncManyMany> AutoIncManyManys { get; set; }
        public DbSet<AutoIncManyOne> AutoIncManyOne { get; set; }

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

        public Boolean ReverseInteration { get; set; }

#if EFCORE
        public static class OptionsMonitor
        {
            public static OptionsMonitor<TOptions> Create<TOptions>(TOptions options) => new OptionsMonitor<TOptions>(options);
        }

        public class OptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
        {
            public OptionsMonitor(TOptions initialValue) => CurrentValue = initialValue;

            readonly List<Action<TOptions, string>> _listeners = new();
            public TOptions CurrentValue { get; private set; }
            public TOptions Get(string name) => throw new NotImplementedException();

            public IDisposable OnChange(Action<TOptions, string> listener)
            {
                _listeners.Add(listener);
                return new ActionDisposable(() => _listeners.Remove(listener));
            }

            public void UpdateOptions(TOptions options)
            {
                CurrentValue = options;
                _listeners.ForEach(listener => listener(options, string.Empty));
            }

            public sealed class ActionDisposable : IDisposable
            {
                readonly Action _action;

                public ActionDisposable(Action action) => _action = action;

                public void Dispose() => _action();
            }
        }

        public ILogger ReconcilerLogger => StaticReconcilerLogger;

        public Boolean LogDebugView => true;

        public static ILogger StaticReconcilerLogger { get; set; }

        static Boolean EfLoggingFilter(String category, String name, LogLevel level)
        {
            return false;
        }

        public static readonly LoggerFactory ConsoleLoggerFactory = new LoggerFactory(
            new[] { new ConsoleLoggerProvider(OptionsMonitor.Create(new ConsoleLoggerOptions())) },
            new LoggerFilterOptions().AddFilter(EfLoggingFilter)
        );

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder
                .UseLoggerFactory(ConsoleLoggerFactory)
                .EnableSensitiveDataLogging()
                .UseSqlServer(@"Data Source=.\;Initial Catalog=reconcilerefcore;Integrated Security=true;TrustServerCertificate=True")
                ;
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
