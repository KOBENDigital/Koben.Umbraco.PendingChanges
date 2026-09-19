namespace Koben.Umbraco.PendingChanges.Services.Models;

/// <summary>
/// One element (a block's content or its settings) as it was stored in a single saved value of a
/// block editor.
/// </summary>
/// <param name="Key">The element's own key, unique across the document.</param>
/// <param name="ContentTypeKey">The element type the block is built from.</param>
/// <param name="OwnerKey">
/// The content key of the block the element belongs to. The same as <paramref name="Key"/> for a
/// block's content, and the block's content key for its settings.
/// </param>
/// <param name="Scope">Whether the element holds the block's content or its settings.</param>
/// <param name="Values">The element's stored values, keyed by alias, culture and segment.</param>
internal sealed record BlockElementSnapshot(
    Guid Key,
    Guid ContentTypeKey,
    Guid OwnerKey,
    BlockElementScope Scope,
    IReadOnlyDictionary<BlockElementValueKey, string?> Values);

/// <summary>Identifies one stored value inside a block element.</summary>
/// <param name="Alias">The element type's property alias.</param>
/// <param name="Culture">The culture of the value, or <c>null</c> when the property does not vary by culture.</param>
/// <param name="Segment">The segment of the value, or <c>null</c> when the property does not vary by segment.</param>
internal readonly record struct BlockElementValueKey(string Alias, string? Culture, string? Segment);
