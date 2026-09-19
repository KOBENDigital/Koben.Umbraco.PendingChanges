using System.Text.Json.Serialization;
using Koben.Umbraco.PendingChanges.Services.Models;

namespace Koben.Umbraco.PendingChanges.Controllers.Contracts;

/// <summary>A single block inside a property's value that has not been published as it now stands.</summary>
public sealed class PendingBlockChangeResponseModel
{
    /// <summary>The key of the element that changed: a block's content or its settings.</summary>
    public required Guid Key { get; init; }

    /// <summary>The content key of the block the element belongs to, which identifies it in the editor.</summary>
    public required Guid OwnerKey { get; init; }

    /// <summary>Whether the change is to the block's content or to its settings.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<BlockElementScope>))]
    public required BlockElementScope Scope { get; init; }

    /// <summary>What publishing would do to this block.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<BlockChangeStatus>))]
    public required BlockChangeStatus Status { get; init; }

    /// <summary>The element type the block is built from.</summary>
    public Guid ContentTypeKey { get; init; }

    /// <summary>The name of that element type, or null when it no longer exists.</summary>
    public string? ContentTypeName { get; init; }

    /// <summary>The name of the user whose save introduced the change.</summary>
    public required string ChangedBy { get; init; }

    /// <summary>When that save happened.</summary>
    public required DateTimeOffset ChangedAt { get; init; }

    /// <summary>The block's own properties that differ, in property alias order.</summary>
    public IReadOnlyCollection<PendingBlockPropertyChangeResponseModel> Properties { get; init; } =
        Array.Empty<PendingBlockPropertyChangeResponseModel>();
}
