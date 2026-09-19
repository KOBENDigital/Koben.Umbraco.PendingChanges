namespace Koben.Umbraco.PendingChanges.Services.Models;

/// <summary>
/// The unpublished state of a single document: its publish state and every property that is
/// waiting to be published.
/// </summary>
public sealed class DocumentPendingChanges
{
    /// <summary>The document the changes belong to.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>How the saved content relates to the published content.</summary>
    public required DocumentPublishState State { get; init; }

    /// <summary>The properties whose saved value differs from the published value.</summary>
    public IReadOnlyCollection<PendingPropertyChange> Properties { get; init; } = Array.Empty<PendingPropertyChange>();
}
