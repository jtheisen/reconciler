# Graph Reconciler for Entity Framework 6 and Core

[![Build status](https://ci.appveyor.com/api/projects/status/4qjaph7n7hpptso7/branch/master?svg=true)](https://ci.appveyor.com/project/jtheisen/reconciler/branch/master)

## Teaser

This library allows you to write

```
    await context.ReconcileAsync(personSentFromClient, e => e
        .WithOne(p => p.Address)
        .WithMany(p => p.Tags, e2 => e2
            .WithShared(p => p.Tag))
        ;
    await context.SaveChangesAsync();
```

and it will sync the four respective tables to match the given,
detached `personSentFromClient` entity by adding, updating and removing
entities as required.

Its primary use case are updates on multiple related entities
retrieved from a client through an API.

It is a replacement for the [`GraphDiff`](https://github.com/zzzprojects/GraphDiff) library.

The EF 6 and EF Core versions share the same source code as far as possible
to ensure consistency.

## NuGet

There is one NuGet package for each of the two frameworks:

```
Install-Package Reconciler.Ef6
```

```
Install-Package Reconciler.EfCore
```

## Definitions

- **Template entities** are the entities to reconcile towards
  (`personSentFromClient` in the teaser sample)
- **The extent** is the extent of the subtree rooted in the template entity
  of the first parameter that is to be reconciled as defined by
  the second parameter to the `Reconcile` extension methods.

## Further features

The extent definition can also contain certain extra information to help with common scenarios that would otherwise often require some convoluted manual code.

### Fixing

Sometimes we need to employ certain fixes on nested parts of the graph on saving:

    .OnInsertion(e => e.Id == Guid.NewGuid())
    .OnInsertion(e => e.CreatedAt == DateTimeOffset.Now)
    .OnUpdate(e => e.ModifiedAt == DateTimeOffset.Now)


The `OnUpdate` definitions apply to insertions as well.

Note the use of the equality operator, as the assignment operator can't be used in expression trees in C#. Also note that the insertion definitions prevent modifications on updates - you don't have to worry about modified `CreatedAt` values being persisted to storage.

### Exclude properties

Sometimes some properties should not be updated, and sometimes they shouldn't even be passed to a client on loading:

    .WithReadOnly(e => e.Unmodifiable)
    .WithBlacked(e => e.Secret)

## Some details and caveats

The are some things to be aware of:

- I'm using the library in production, but that doesn't mean
  it's mature. The test suite is thin and you may hit issues.
- Specifying relationships on derived classes in models
  with inheritance is not supported.
- Using entities that are part of an inheritance hierarchy
  in all other contexts is also untested and likely doesn't work yet.
- Many-to-many relationships are not supported and
  will probably never be.
- The foreign key properties in the template entity must be set
  to match the respective navigational properties before the call
  to one of the `Reconcile` overloads is made. For example, it should be that
  `person.AddressId == person.Address.Id` in the unit test's sample model.
- The extent must represent a subtree, i.e. have no cycles, and all
  entities must appear only once.
- The `Reconcile` overloads themselves access the database only
  for reading and thus need to be followed by a `SaveChanges` call.
- The number of reads done is the number of entities either in
  storage or in the template that are covered by the extent and
  have a non-trivial sub-extent themselves. In the above example,
  the address would have been fetched with the person itself and
  cause no further load, but there would be one load per
  tag-to-person bridge table row which each include the respective tag.
  There's room for optimization here, but that's where it's at.

## GraphDiff

Before writing this replacement I used the [`GraphDiff`](https://github.com/zzzprojects/GraphDiff) library, but
since it is no longer maintained I became motivated to write my own solution. In particular, I wanted

- Entity Framework Core support,
- async/await support and
- really understand what's going on.

As I don't fully understand the original there are some differences beyond the names of functions: GraphDiff had `OwnedEntity`, `OwnedCollection` and
`AssociatedCollection` as options for how to define the extent,
and I don't quite know what the difference between associated and owned is.

`Reconciler` has `WithOne`, `WithMany` and `WithShared`:

`WithOne` and `WithMany` reconcile a scalar or collection navigational property, respectively,
through respective addition, update and removal operations. `WithShared`
only works on scalar navigational properties and doesn't remove a formerly
related entity that is now no longer related. This is useful, for example, in
the sample in the teaser where we want to insert new tag entities when they are
needed but don't want to remove them as they may be shared by other entities.

## Roadmap

There are a number of exciting features that would make this library much
more useful. See [this document](ideas.md) for further information.
