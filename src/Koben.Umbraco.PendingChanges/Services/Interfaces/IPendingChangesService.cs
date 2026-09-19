using Koben.Umbraco.PendingChanges.Services.Models;

namespace Koben.Umbraco.PendingChanges.Services.Interfaces;

/// <summary>
/// Works out which of a document's saved property values have not been published yet, and who
/// last changed each of them.
/// </summary>
public interface IPendingChangesService
{
    /// <summary>
    /// Compares a document's saved values with its published values.
    /// </summary>
    /// <param name="documentId">The key of the document to inspect.</param>
    /// <param name="cancellationToken">Cancels the version lookups used for attribution.</param>
    /// <returns>The document's publish state and its unpublished property values, or <c>null</c> when no such document exists.</returns>
    Task<DocumentPendingChanges?> GetPendingChangesAsync(Guid documentId, CancellationToken cancellationToken);
}
