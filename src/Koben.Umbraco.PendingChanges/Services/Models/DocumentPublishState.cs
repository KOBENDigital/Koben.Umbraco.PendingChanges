namespace Koben.Umbraco.PendingChanges.Services.Models;

/// <summary>
/// How a document's saved (draft) content relates to what the public site is serving.
/// </summary>
public enum DocumentPublishState
{
    /// <summary>Nothing of this document is published, so every value is still only a draft.</summary>
    NotPublished,

    /// <summary>The saved content matches the published content.</summary>
    Published,

    /// <summary>The document is published, but some saved values have not been published yet.</summary>
    PendingChanges
}
