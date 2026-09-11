using System;

namespace Dreambit;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class BlueprintTypeAttribute : Attribute
{
    public BlueprintTypeAttribute(string id)
        : this(id, [])
    {
    }

    public BlueprintTypeAttribute(string id, params string[] formerIds)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Blueprint type ID cannot be empty.", nameof(id));
        if (formerIds is not null)
            foreach (var formerId in formerIds)
                if (string.IsNullOrWhiteSpace(formerId))
                    throw new ArgumentException(
                        "Former Blueprint type IDs cannot be empty.",
                        nameof(formerIds));

        Id = id;
        FormerIds = formerIds ?? [];
    }

    public string Id { get; }

    /// <summary>
    /// Previous serialized type IDs accepted while loading older Blueprints.
    /// Saving writes only <see cref="Id" />.
    /// </summary>
    public string[] FormerIds { get; }
}
