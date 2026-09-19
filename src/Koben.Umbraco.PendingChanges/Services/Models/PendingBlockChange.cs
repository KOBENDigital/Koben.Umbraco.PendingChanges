namespace Koben.Umbraco.PendingChanges.Services.Models;

/// <summary>
/// One block inside a property's saved value that publishing would add, change or take away.
/// </summary>
public sealed class PendingBlockChange
{
    /// <summary>The key of the element that changed: a block's content or its settings.</summary>
    public required Guid Key { get; init; }

    /// <summary>
    /// The content key of the block the element belongs to. The same as <see cref="Key"/> for a
    /// block's content, and the block's own key for its settings, so both point at one block in the
    /// editor.
    /// </summary>
    public required Guid OwnerKey { get; init; }

    /// <summary>Whether the change is to the block's content or to its settings.</summary>
    public required BlockElementScope Scope { get; init; }

    /// <summary>What publishing would do to this block.</summary>
    public required BlockChangeStatus Status { get; init; }

    /// <summary>The element type the block is built from.</summary>
    public Guid ContentTypeKey { get; init; }

    /// <summary>The name of that element type, or <c>null</c> when it no longer exists.</summary>
    public string? ContentTypeName { get; init; }

    /// <summary>The name of the user whose save introduced the change.</summary>
    public required string ChangedBy { get; init; }

    /// <summary>When that save happened.</summary>
    public DateTimeOffset ChangedAt { get; init; }

    /// <summary>
    /// The block's own properties that differ, for a <see cref="BlockChangeStatus.Changed"/> block.
    /// Empty for a block that was added or removed whole.
    /// </summary>
    public IReadOnlyCollection<PendingBlockPropertyChange> Properties { get; init; } =
        Array.Empty<PendingBlockPropertyChange>();
}
