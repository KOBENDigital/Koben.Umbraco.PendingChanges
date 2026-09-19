namespace Koben.Umbraco.PendingChanges.Controllers.Contracts;

/// <summary>A single saved value that has not been published, and the edit behind it.</summary>
public sealed class PendingPropertyChangeResponseModel
{
    /// <summary>The property type alias.</summary>
    public required string Alias { get; init; }

    /// <summary>The culture of the value, or null for an invariant property.</summary>
    public string? Culture { get; init; }

    /// <summary>The segment of the value, or null for an unsegmented property.</summary>
    public string? Segment { get; init; }

    /// <summary>The tab the property is edited on, or null when it sits outside one.</summary>
    public string? Tab { get; init; }

    /// <summary>The name of the user whose save introduced the current value.</summary>
    public required string ChangedBy { get; init; }

    /// <summary>When that save happened.</summary>
    public required DateTimeOffset ChangedAt { get; init; }
}
