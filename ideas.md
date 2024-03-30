### Loading and decycling

Extent definitions are also suited to be simply loaded - so that the respective information doesn't have to be repeated in explicit `.Include`s.

It's somewhat complicated when combined with other features on the road map, in particular support for inheritance and semantic deletion.

There's also another feature that Reconciler can do while loading: It can ensure all navigational properties that are not explicitly mentioned in the extent definition are set to null, so that there are no cycles in the graph. Cycles make it more difficult to serialize, in particular you can't just use plain JSON.

Note that the load of an extent may be less than the load the reconciliation needs to do: The latter also needs to check if certain yet-to-become-related entities that are passed attached to the root already exist, whereas a simple load of an extent needs only to look at the root itself.

### Order

Common is also that collection nav props should come sorted by one of its integer properties. On reconciliation, that property should be updated for all items:

    .WithMany(e => e.Tags, t => t.Order)

### Semantic Deletion

Deletion is often not literal but semantically expressed by setting a flag. Reconciler should support that. For example,

    .WithDeletionAs(e => e.DeletedAt == DateTimeOffset.Now)

should mean that

- loading only loads those with `DeletedAt` being null (as it is `default(DateTimeOffset?)`) and
- deletion should not delete but merely set `DeletedAt` to `DateTimeOffset.Now`.

It should be investigated how EF Core's idea of default filters can fit into this.

### Cloning

Sometimes you need to transplant a persisted graph to a new one, replacing the respective ids with new ones. In my experience that's the kind of logic that always breaks when done in an ad-hoc manner because

- it needs to be fixed every time some new relationship is added that needs to be cloned as well, but
- the respective feature needing cloning is often neglected in testing.

This could be avoided by the common loading code sharing the extent definition with the cloning function, using Reconciler to do the cloning.

The cloning feature is a bit different from all the others in that it requires knowledge about the relationship of navigational properties and the foreign keys that represent them - which is difficult in EF 6.

### Merging

Similar to cloning is the task of taking all entities in all specified collection nav props from one entity, the merge source, and reroot them to another, the merge target.

### Key Consistency Requirement Drop (Done for EF Core in 1.1.0)

Reconciler currently requires that for all given nav props values, the respective foreign keys need to be set and match - which is something easy to get wrong when done explicitly in a client:

```
new X {
    YId = yId, // easy to forget to keep in sync...
    Y = new Y {
        Id = yId // ...with this!
    }
}
```

We want all foreign key values to be inferred from the given nav prop values, and in fact we also want their absence to indicate a change in the foreign key as well:

```
new X {
    YId = yId, // even if the foreign key is non-null here...
    Y = null // ...this indicates that it should be set to null
}
```

Somewhat related to this is that in the case of collection nav props whose values are part of the extent, a null value should simply lead to an exception rather than being interpreted as an empty collection.

All of this implies a subtle change in semantics.

Also, setting the `Id` key property as in one of the sample code above can only work on leaves until the Key Consistency Requirement Drop is implemented.

### Inheritance support

It would be great if we could also include navigational properties of derived types in extents:

    .WhenOf<Derived>(m => m.
        With(e => e.PropertyOnDerivedType)
    )
