namespace Koben.Umbraco.PendingChanges.Services.Models;

/// <summary>
/// One property of a block whose saved value has not been published, and the last edit that
/// changed it.
/// </summary>
public sealed class PendingBlockPropertyChange
{
    /// <summary>The element type's property alias.</summary>
    public required string Alias { get; init; }

    /// <summary>The culture of the changed value, or <c>null</c> when the property is invariant.</summary>
    public string? Culture { get; init; }

    /// <summary>The segment of the changed value, or <c>null</c> when the property is unsegmented.</summary>
    public string? Segment { get; init; }

    /// <summary>The tab of the block's editor the property sits on, or <c>null</c> when it sits outside one.</summary>
    public string? Tab { get; init; }

    /// <summary>The name of the user whose save introduced the current value.</summary>
    public required string ChangedBy { get; init; }

    /// <summary>When that save happened.</summary>
    public DateTimeOffset ChangedAt { get; init; }
}
