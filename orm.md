# Many-to-many relationships and inheritance considered harmful

Reconciler isn't going to support either many-to-many relationships or inheritance unless a third-party contributes those features due to my lack of desire to use these features myself.

In fact, I don't think those features should be in ORMs to begin with, and I explain why in the following text.

## ORMs

ORM stands, unfortunately, for Object Relational Mapper.

This isn't because of what they do, it's because they came up when Object Oriented Programming was the new cool thing.

Think of the "stereo", the major feature of which certainly isn't that it's stereo, but that it makes sound, be that in mono, stereo or 5.1. Yet it's still just called stereo.

Regrettably the name isn't where the homage to OOP ended, and so basically all ORMs offer the possibility to map a class hierarchy on to one or more database tables.

While it's not obvious at first glance why that is a bad idea, what is clear at first glance is that mapping tables to hierarchies isn't _the most essential thing_ ORMs do: The most essential thing is that they simply provide a decent interface to databases - that themselves all ship only with very rudimentary APIs, which not only aren't type safe, they expect you to send queries in a textual format.

So if we start from the desire for a decent database access layer, what we should wish to aim for first is to reflect all concepts that are _known to SQL_, as such faithfulness will minimize conceptual impedance. And of course inheritance is unknown to SQL, as are many-to-many-relationships.

The relational database relationships known to SQL are of the following types:

- many-to-one (foreign key unequal to domestic keys)
- many-to-one-or-none (nullable foreign key unequal to domestic keys)
- one-to-one-or-none (foreign key that is also a domestic key)

That doesn't mean that inheritance and many-to-many relationships are not useful as higher-level abstractions, but they will have to justify themselves as that.

Without them, all tables are mapped to one type. All relationships are reflected by one foreign key. So those extra features will have to be so important that breaking this simple formula is worth it.

The only advantage in the case of many-to-many relationships is that the bridge table implementing it doesn't have to appear in the model, saving the programmer of some typing work. Since those relationships are on the rarer side of things, that's only a minor plus.

## Serialization

Inheritance makes the model side look leaner and have more frequent applications, but this feature is also troubled with one more caveat: Models are frequently serialized and sent to a client, and models that use inheritance need to store type information in the serialization.

For instance, a JSON result containing such information may look like this:

```
    {
        "$type": "Company",
        "Name": "Goliath Books",
        "Employees": [ ... ],
        ...
    }
```

Depending on what languages and tools are used, this can mean that the serializers and parsers need to be aware of the meaning of the `$type` property as well as which types can occur at all.

Forgoing inheritance, these issues go away:

```
    {
        "Name": "Goliath Books",
        "AsCompany": {
            "Employees": [ ... ],
            ...
        },
        "AsIndividual": null
    }
```

(This is less of an issue in clients using type-unsafe languages such as plain Javascript as then you're probably checking the `$type` property inside an `if` statement directly. It is a complication with typesafe clients that are generated from the model, however, such as `NSwag` for the .NET/TypeScript ecosystem.)
