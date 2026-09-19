namespace Koben.Umbraco.PendingChanges.Services.Models;

/// <summary>
/// One property value that has been saved but not published, and the last edit that changed it.
/// </summary>
public sealed class PendingPropertyChange
{
    /// <summary>The property type alias the change belongs to.</summary>
    public required string Alias { get; init; }

    /// <summary>The culture of the changed value, or <c>null</c> for invariant properties.</summary>
    public string? Culture { get; init; }

    /// <summary>The segment of the changed value, or <c>null</c> for unsegmented properties.</summary>
    public string? Segment { get; init; }

    /// <summary>The name of the tab the property sits on, or <c>null</c> when it sits outside one.</summary>
    public string? Tab { get; init; }

    /// <summary>The name of the user whose save introduced the current draft value.</summary>
    public required string ChangedBy { get; init; }

    /// <summary>When that save happened.</summary>
    public DateTimeOffset ChangedAt { get; init; }
}
