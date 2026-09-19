using System.Text.Json;
using System.Text.Json.Nodes;
using Koben.Umbraco.PendingChanges.Services.Models;

namespace Koben.Umbraco.PendingChanges.Services;

/// <summary>
/// Reads the elements out of a stored block editor value.
/// </summary>
/// <remarks>
/// Every block editor Umbraco ships — Block List, Block Grid, Block (single) and the blocks inside
/// a rich text value — stores its blocks as <c>contentData</c> and <c>settingsData</c> arrays
/// somewhere in one JSON value, with a <c>layout</c> that pairs a block's content with its
/// settings. This reader looks for those shapes anywhere in the value rather than binding to one
/// editor's model, which is what lets one pass flatten blocks nested inside blocks: a nested
/// editor's value is just more JSON hanging off a block's own property value.
/// <para>
/// Anything that is not a block value — a string, a number, an unrelated JSON object — reads as
/// "no blocks", so a property can be handed to it without asking what editor drew it.
/// </para>
/// </remarks>
internal static class BlockValueReader
{
    private const string ContentDataProperty = "contentData";
    private const string SettingsDataProperty = "settingsData";
    private const string ContentKeyProperty = "contentKey";
    private const string SettingsKeyProperty = "settingsKey";

    // Umbraco 13 and earlier identified elements by Udi. Umbraco's own upgrade rewrites those to
    // keys, so this only covers a value that has somehow escaped that migration.
    private const string KeyProperty = "key";
    private const string LegacyKeyProperty = "udi";
    private const string LegacyContentKeyProperty = "contentUdi";
    private const string LegacySettingsKeyProperty = "settingsUdi";

    /// <summary>
    /// Flattens every block held in one stored property value, however deeply they are nested.
    /// </summary>
    /// <param name="value">The stored property value.</param>
    /// <returns>
    /// The value's elements keyed by their own key, or <c>null</c> when the value holds no blocks.
    /// </returns>
    public static IReadOnlyDictionary<Guid, BlockElementSnapshot>? Read(object? value)
    {
        if (value is not string text || TryParse(text) is not JsonNode root)
        {
            return null;
        }

        Dictionary<Guid, ElementBuilder> elements = [];
        Dictionary<Guid, Guid> ownersBySettingsKey = [];

        Collect(root, elements, ownersBySettingsKey);

        if (elements.Count == 0)
        {
            return null;
        }

        return elements.ToDictionary(
            element => element.Key,
            element => element.Value.Build(ownersBySettingsKey));
    }

    /// <summary>
    /// Walks a value looking for the two shapes that matter: the arrays of elements, and the layout
    /// entries that say which settings element belongs to which block.
    /// </summary>
    /// <param name="node">The node to walk.</param>
    /// <param name="elements">Collects the elements found.</param>
    /// <param name="ownersBySettingsKey">Collects which block each settings element belongs to.</param>
    private static void Collect(JsonNode? node, Dictionary<Guid, ElementBuilder> elements, Dictionary<Guid, Guid> ownersBySettingsKey)
    {
        switch (node)
        {
            case JsonObject item:
                CollectFromObject(item, elements, ownersBySettingsKey);
                break;

            case JsonArray array:
                foreach (JsonNode? entry in array)
                {
                    Collect(entry, elements, ownersBySettingsKey);
                }

                break;
        }
    }

    /// <inheritdoc cref="Collect" />
    private static void CollectFromObject(JsonObject item, Dictionary<Guid, ElementBuilder> elements, Dictionary<Guid, Guid> ownersBySettingsKey)
    {
        // A layout entry: it names a block's content and, when the block has settings, the element
        // holding them. That pairing is the only place the two are tied together.
        if (ReadKey(item, ContentKeyProperty, LegacyContentKeyProperty) is Guid contentKey
            && ReadKey(item, SettingsKeyProperty, LegacySettingsKeyProperty) is Guid settingsKey)
        {
            ownersBySettingsKey[settingsKey] = contentKey;
        }

        foreach ((string name, JsonNode? child) in item)
        {
            bool isContentData = name == ContentDataProperty;

            if ((isContentData || name == SettingsDataProperty) && child is JsonArray items)
            {
                BlockElementScope scope = isContentData ? BlockElementScope.Content : BlockElementScope.Settings;

                foreach (JsonNode? entry in items)
                {
                    CollectElement(entry as JsonObject, scope, elements, ownersBySettingsKey);
                }

                continue;
            }

            Collect(child, elements, ownersBySettingsKey);
        }
    }

    /// <summary>
    /// Reads one element and, through its values, any blocks nested inside it.
    /// </summary>
    /// <param name="element">The element to read.</param>
    /// <param name="scope">Whether the element holds a block's content or its settings.</param>
    /// <param name="elements">Collects the elements found.</param>
    /// <param name="ownersBySettingsKey">Collects which block each settings element belongs to.</param>
    private static void CollectElement(
        JsonObject? element,
        BlockElementScope scope,
        Dictionary<Guid, ElementBuilder> elements,
        Dictionary<Guid, Guid> ownersBySettingsKey)
    {
        if (element is null || ReadKey(element, KeyProperty, LegacyKeyProperty) is not Guid key)
        {
            return;
        }

        ElementBuilder builder = new(key, ReadKey(element["contentTypeKey"]) ?? Guid.Empty, scope);

        if (element["values"] is JsonArray values)
        {
            foreach (JsonObject entry in values.OfType<JsonObject>())
            {
                string? alias = ReadString(entry["alias"]);
                if (alias is null)
                {
                    continue;
                }

                JsonNode? value = entry["value"];
                builder.Values[new BlockElementValueKey(alias, ReadString(entry["culture"]), ReadString(entry["segment"]))] = Stringify(value);

                // A block editor inside a block: its blocks belong to the same document property,
                // so they are collected alongside their parent rather than under it. A nested
                // editor's value is stored as JSON *text* inside the outer JSON, so it is parsed
                // again rather than walked.
                Collect(ReadString(value) is string nested ? TryParse(nested) : value, elements, ownersBySettingsKey);
            }
        }

        elements[key] = builder;
    }

    /// <summary>
    /// Reduces a stored value to a comparable string, treating blank values as absent the way the
    /// document level comparison does.
    /// </summary>
    /// <param name="node">The stored value.</param>
    /// <returns>The comparable form of the value, or <c>null</c> when it holds nothing.</returns>
    private static string? Stringify(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue(out string? text) => string.IsNullOrWhiteSpace(text) ? null : text,
        _ => node.ToJsonString()
    };

    /// <summary>
    /// Parses text that might be a block value.
    /// </summary>
    /// <param name="text">The text to parse.</param>
    /// <returns>
    /// The parsed JSON, or <c>null</c> when the text is not a JSON object. Every block value is
    /// one, so the check keeps the parser off the plain text, numbers and dates that make up most
    /// of a document.
    /// </returns>
    private static JsonNode? TryParse(string text)
    {
        text = text.TrimStart();

        if (text.Length == 0 || text[0] != '{')
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a JSON value as a string, without throwing on the ones that are not.</summary>
    /// <param name="node">The node to read.</param>
    /// <returns>The string, or <c>null</c> when the node is absent, null or another type.</returns>
    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>
    /// Reads an element key from whichever of two properties an object carries it in.
    /// </summary>
    /// <param name="item">The object to read from.</param>
    /// <param name="name">The property the current format uses.</param>
    /// <param name="legacyName">The property Umbraco 13 and earlier used.</param>
    /// <returns>The key, or <c>null</c> when neither property holds one.</returns>
    private static Guid? ReadKey(JsonObject item, string name, string legacyName) =>
        ReadKey(item[name]) ?? ReadKey(item[legacyName]);

    /// <summary>
    /// Reads a JSON value as an element key, accepting the Udi that Umbraco 13 and earlier stored.
    /// </summary>
    /// <param name="node">The node to read.</param>
    /// <returns>The key, or <c>null</c> when the node holds nothing usable.</returns>
    private static Guid? ReadKey(JsonNode? node)
    {
        string? text = ReadString(node);
        if (text is null)
        {
            return null;
        }

        if (Guid.TryParse(text, out Guid key))
        {
            return key;
        }

        // umb://element/<guid>
        int separator = text.LastIndexOf('/');

        return separator >= 0 && Guid.TryParse(text[(separator + 1)..], out key) ? key : null;
    }

    /// <summary>Collects one element's values while the surrounding value is still being read.</summary>
    /// <param name="key">The element's own key.</param>
    /// <param name="contentTypeKey">The element type the block is built from.</param>
    /// <param name="scope">Whether the element holds a block's content or its settings.</param>
    private sealed class ElementBuilder(Guid key, Guid contentTypeKey, BlockElementScope scope)
    {
        /// <summary>The element's stored values.</summary>
        public Dictionary<BlockElementValueKey, string?> Values { get; } = [];

        /// <summary>
        /// Completes the element once the layout has been read, so its settings can be pointed at
        /// the block they configure.
        /// </summary>
        /// <param name="ownersBySettingsKey">Which block each settings element belongs to.</param>
        /// <returns>The finished element.</returns>
        public BlockElementSnapshot Build(IReadOnlyDictionary<Guid, Guid> ownersBySettingsKey)
        {
            Guid ownerKey = scope == BlockElementScope.Settings && ownersBySettingsKey.TryGetValue(key, out Guid owner)
                ? owner
                : key;

            return new BlockElementSnapshot(key, contentTypeKey, ownerKey, scope, Values);
        }
    }
}
