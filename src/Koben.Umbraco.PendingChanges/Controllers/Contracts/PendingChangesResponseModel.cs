using System.Text.Json.Serialization;
using Koben.Umbraco.PendingChanges.Services.Models;

namespace Koben.Umbraco.PendingChanges.Controllers.Contracts;

/// <summary>What a document still has to publish.</summary>
public sealed class PendingChangesResponseModel
{
    /// <summary>The document that was inspected.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>How the document's saved content relates to its published content.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<DocumentPublishState>))]
    public required DocumentPublishState State { get; init; }

    /// <summary>One entry per saved value that has not been published, in property alias order.</summary>
    public IReadOnlyCollection<PendingPropertyChangeResponseModel> Properties { get; init; } =
        Array.Empty<PendingPropertyChangeResponseModel>();
}
