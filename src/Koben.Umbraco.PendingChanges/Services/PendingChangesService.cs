using System.Globalization;
using Koben.Umbraco.PendingChanges.Services.Interfaces;
using Koben.Umbraco.PendingChanges.Services.Models;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services.OperationStatus;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core;
using Umbraco.Extensions;

namespace Koben.Umbraco.PendingChanges.Services;

/// <inheritdoc cref="IPendingChangesService" />
public sealed class PendingChangesService : IPendingChangesService
{
    /// <summary>
    /// How far back through the save history attribution will look. Anything older than this is
    /// credited to the oldest version we did read, which keeps a heavily edited page cheap to open.
    /// </summary>
    private const int MaxVersionsInspected = 12;

    private const string UnknownEditorName = "Unknown user";

    /// <summary>
    /// Stands in for a block that exists at all, so that a block appearing or disappearing can be
    /// attributed to a save by the same comparison that attributes a value changing.
    /// </summary>
    private const string BlockPresentMarker = "block";

    private readonly IContentService _contentService;
    private readonly IContentTypeService _contentTypeService;
    private readonly IContentVersionService _contentVersionService;
    private readonly IUserService _userService;
    private readonly ILogger<PendingChangesService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="contentService">Reads the document being inspected.</param>
    /// <param name="contentTypeService">Reads the document and element types, to name the tab each property sits on.</param>
    /// <param name="contentVersionService">Reads the save history used to attribute each change.</param>
    /// <param name="userService">Resolves the display names of the editors behind those saves.</param>
    /// <param name="logger">Records history that could not be read.</param>
    public PendingChangesService(
        IContentService contentService,
        IContentTypeService contentTypeService,
        IContentVersionService contentVersionService,
        IUserService userService,
        ILogger<PendingChangesService> logger)
    {
        _contentService = contentService;
        _contentTypeService = contentTypeService;
        _contentVersionService = contentVersionService;
        _userService = userService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DocumentPendingChanges?> GetPendingChangesAsync(Guid documentId, CancellationToken cancellationToken)
    {
        IContent? content = _contentService.GetById(documentId);
        if (content is null)
        {
            return null;
        }

        // Nothing is live, so there is nothing to compare against: the whole document is a draft.
        if (!content.Published || content.PublishedVersionId == 0)
        {
            return new DocumentPendingChanges
            {
                DocumentId = documentId,
                State = DocumentPublishState.NotPublished
            };
        }

        IReadOnlyList<PropertyDifference> differences = FindUnpublishedValues(content);
        if (differences.Count == 0)
        {
            return new DocumentPendingChanges
            {
                DocumentId = documentId,
                State = DocumentPublishState.Published
            };
        }

        IReadOnlyCollection<PendingPropertyChange> properties = await AttributeChangesAsync(content, differences, cancellationToken);

        return new DocumentPendingChanges
        {
            DocumentId = documentId,
            State = DocumentPublishState.PendingChanges,
            Properties = properties
        };
    }

    /// <summary>
    /// Finds every stored property value whose saved (edited) value differs from the value the
    /// site is currently serving, and — for a value holding blocks — which of its blocks account
    /// for the difference.
    /// </summary>
    /// <param name="content">The document to inspect.</param>
    /// <returns>The culture and segment specific property values that are waiting to be published.</returns>
    private static IReadOnlyList<PropertyDifference> FindUnpublishedValues(IContent content)
    {
        List<PropertyDifference> changed = [];

        foreach (IProperty property in content.Properties)
        {
            foreach (IPropertyValue value in property.Values)
            {
                if (!ValuesDiffer(value.EditedValue, value.PublishedValue))
                {
                    continue;
                }

                changed.Add(new PropertyDifference(
                    new PropertyValueKey(property.Alias, value.Culture, value.Segment),
                    CompareBlocks(value.EditedValue, value.PublishedValue)));
            }
        }

        return changed;
    }

    /// <summary>
    /// Compares the blocks held in one property's saved value with the blocks in its published
    /// value, however deeply they are nested.
    /// </summary>
    /// <param name="editedValue">The saved value.</param>
    /// <param name="publishedValue">The value the site is serving.</param>
    /// <returns>
    /// One entry per block that publishing would add, change or take away, or an empty list when
    /// the property holds no blocks.
    /// </returns>
    private static IReadOnlyList<BlockDifference> CompareBlocks(object? editedValue, object? publishedValue)
    {
        IReadOnlyDictionary<Guid, BlockElementSnapshot>? edited = BlockValueReader.Read(editedValue);
        IReadOnlyDictionary<Guid, BlockElementSnapshot>? published = BlockValueReader.Read(publishedValue);

        if (edited is null && published is null)
        {
            return [];
        }

        edited ??= new Dictionary<Guid, BlockElementSnapshot>();
        published ??= new Dictionary<Guid, BlockElementSnapshot>();

        List<BlockDifference> differences = [];

        foreach ((Guid key, BlockElementSnapshot element) in edited)
        {
            if (!published.TryGetValue(key, out BlockElementSnapshot? live))
            {
                differences.Add(new BlockDifference(element, BlockChangeStatus.Added, []));
                continue;
            }

            BlockElementValueKey[] changedValues = [.. FindChangedValues(element, live)];
            if (changedValues.Length > 0)
            {
                differences.Add(new BlockDifference(element, BlockChangeStatus.Changed, changedValues));
            }
        }

        foreach ((Guid key, BlockElementSnapshot element) in published)
        {
            if (!edited.ContainsKey(key))
            {
                differences.Add(new BlockDifference(element, BlockChangeStatus.Removed, []));
            }
        }

        return differences;
    }

    /// <summary>
    /// Compares one element's saved values with the same element's published values.
    /// </summary>
    /// <param name="edited">The element as it is saved.</param>
    /// <param name="published">The same element as it is published.</param>
    /// <returns>The values that differ, in property alias order.</returns>
    private static IEnumerable<BlockElementValueKey> FindChangedValues(BlockElementSnapshot edited, BlockElementSnapshot published) =>
        edited.Values.Keys
            .Union(published.Values.Keys)
            .Where(key => !string.Equals(
                edited.Values.GetValueOrDefault(key),
                published.Values.GetValueOrDefault(key),
                StringComparison.Ordinal))
            .OrderBy(key => key.Alias, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Walks back through the save history and credits each unpublished value — the document's own
    /// and the ones inside its blocks — to the save that last changed it.
    /// </summary>
    /// <param name="content">The current draft of the document.</param>
    /// <param name="differences">The property values that differ from the published content.</param>
    /// <param name="cancellationToken">Cancels the per-version lookups.</param>
    /// <returns>One entry per unpublished property, carrying the editor's name and the time of their save.</returns>
    private async Task<IReadOnlyCollection<PendingPropertyChange>> AttributeChangesAsync(
        IContent content,
        IReadOnlyList<PropertyDifference> differences,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ContentVersionMeta> versions = await GetRecentVersionsAsync(content.Key);

        VersionValueReader reader = new();
        HashSet<ChangeKey> unattributed = [.. EnumerateChangeKeys(differences)];
        Dictionary<ChangeKey, SaveAttribution> attributions = [];

        int draftVersionId = content.VersionId;
        ContentVersionMeta? draftVersion = versions.FirstOrDefault(version => version.VersionId == draftVersionId);

        IContent newer = content;
        SaveAttribution newerSave = draftVersion is not null
            ? ToAttribution(draftVersion)
            : new SaveAttribution(content.WriterId, null, ToOffset(content.UpdateDate));

        foreach (ContentVersionMeta olderVersion in versions)
        {
            // The newest row is the draft in hand; the walk compares it against what came before.
            if (olderVersion.VersionId == draftVersionId)
            {
                continue;
            }

            if (unattributed.Count == 0)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            IContent? older = await GetVersionAsync(olderVersion.VersionId);
            if (older is null)
            {
                continue;
            }

            foreach (ChangeKey key in unattributed.ToArray())
            {
                if (ValuesDiffer(reader.Read(newer, key), reader.Read(older, key)))
                {
                    attributions[key] = newerSave;
                    unattributed.Remove(key);
                }
            }

            bool reachedPublishedVersion = olderVersion.CurrentPublishedVersion;
            newer = older;
            newerSave = ToAttribution(olderVersion);

            if (reachedPublishedVersion)
            {
                break;
            }
        }

        // Older than the history we read (or no history at all): credit the oldest save we saw.
        foreach (ChangeKey key in unattributed)
        {
            attributions[key] = newerSave;
        }

        return BuildChanges(content, differences, attributions);
    }

    /// <summary>
    /// Lists everything the history walk has to attribute: each changed property value, each block
    /// that appeared or disappeared, and each changed value inside a block.
    /// </summary>
    /// <param name="differences">The differences found between the saved and published content.</param>
    /// <returns>The keys to attribute.</returns>
    private static IEnumerable<ChangeKey> EnumerateChangeKeys(IReadOnlyList<PropertyDifference> differences)
    {
        foreach (PropertyDifference difference in differences)
        {
            yield return new ChangeKey(difference.Property, null, null);

            foreach (BlockDifference block in difference.Blocks)
            {
                if (block.Status == BlockChangeStatus.Changed)
                {
                    foreach (BlockElementValueKey value in block.Values)
                    {
                        yield return new ChangeKey(difference.Property, block.Element.Key, value);
                    }

                    continue;
                }

                yield return new ChangeKey(difference.Property, block.Element.Key, null);
            }
        }
    }

    /// <summary>
    /// Reads the most recent saves of a document, newest first.
    /// </summary>
    /// <param name="documentKey">The key of the document.</param>
    /// <returns>The version metadata, or an empty list when the history cannot be read.</returns>
    private async Task<IReadOnlyList<ContentVersionMeta>> GetRecentVersionsAsync(Guid documentKey)
    {
        Attempt<PagedModel<ContentVersionMeta>?, ContentVersionOperationStatus> attempt =
            await _contentVersionService.GetPagedContentVersionsAsync(documentKey, null, 0, MaxVersionsInspected);

        if (!attempt.Success || attempt.Result is null)
        {
            _logger.LogWarning(
                "Could not read the version history for document {DocumentKey}: {Status}. Unpublished changes will be credited to the last save.",
                documentKey,
                attempt.Status);

            return [];
        }

        return [.. attempt.Result.Items
            .OrderByDescending(version => version.VersionDate)
            .ThenByDescending(version => version.VersionId)];
    }

    /// <summary>
    /// Loads the saved values of a single historic version.
    /// </summary>
    /// <param name="versionId">The identifier of the version to load.</param>
    /// <returns>The version's content, or <c>null</c> when it can no longer be read.</returns>
    private async Task<IContent?> GetVersionAsync(int versionId)
    {
        Attempt<IContent?, ContentVersionOperationStatus> attempt = await _contentVersionService.GetAsync(versionId.ToGuid());

        if (!attempt.Success)
        {
            _logger.LogDebug("Version {VersionId} could not be read for change attribution: {Status}.", versionId, attempt.Status);
        }

        return attempt.Success ? attempt.Result : null;
    }

    /// <summary>
    /// Turns the collected attributions into the returned model, resolving editor names in one pass.
    /// </summary>
    /// <param name="content">The document the values belong to, used to name their tabs.</param>
    /// <param name="differences">The differences found between the saved and published content.</param>
    /// <param name="attributions">The save each value was credited to.</param>
    /// <returns>The per-property changes, in property alias order.</returns>
    private IReadOnlyCollection<PendingPropertyChange> BuildChanges(
        IContent content,
        IReadOnlyList<PropertyDifference> differences,
        IReadOnlyDictionary<ChangeKey, SaveAttribution> attributions)
    {
        IReadOnlyDictionary<int, string> editorNames = ResolveEditorNames(attributions.Values);
        Dictionary<Guid, ContentTypeSummary> contentTypes = [];
        ContentTypeSummary document = DescribeContentType(content.ContentType.Key, contentTypes);

        return [.. differences
            .Select(difference =>
            {
                SaveAttribution save = attributions[new ChangeKey(difference.Property, null, null)];

                return new PendingPropertyChange
                {
                    Alias = difference.Property.Alias,
                    Culture = difference.Property.Culture,
                    Segment = difference.Property.Segment,
                    Tab = document.TabsByProperty.GetValueOrDefault(difference.Property.Alias),
                    ChangedBy = NameOf(save, editorNames),
                    ChangedAt = save.SavedAt,
                    Blocks = BuildBlockChanges(difference, attributions, editorNames, contentTypes)
                };
            })
            .OrderBy(change => change.Alias, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Shapes one property's block differences for the response.
    /// </summary>
    /// <param name="difference">The property difference holding them.</param>
    /// <param name="attributions">The save each value was credited to.</param>
    /// <param name="editorNames">The display names of the editors behind those saves.</param>
    /// <param name="contentTypes">Caches the element types read while naming tabs.</param>
    /// <returns>The blocks that changed, most recently changed first.</returns>
    private IReadOnlyCollection<PendingBlockChange> BuildBlockChanges(
        PropertyDifference difference,
        IReadOnlyDictionary<ChangeKey, SaveAttribution> attributions,
        IReadOnlyDictionary<int, string> editorNames,
        Dictionary<Guid, ContentTypeSummary> contentTypes)
    {
        if (difference.Blocks.Count == 0)
        {
            return Array.Empty<PendingBlockChange>();
        }

        List<PendingBlockChange> changes = [];

        foreach (BlockDifference block in difference.Blocks)
        {
            ContentTypeSummary elementType = DescribeContentType(block.Element.ContentTypeKey, contentTypes);

            PendingBlockPropertyChange[] properties = [.. block.Values
                .Select(value =>
                {
                    SaveAttribution save = attributions[new ChangeKey(difference.Property, block.Element.Key, value)];

                    return new PendingBlockPropertyChange
                    {
                        Alias = value.Alias,
                        Culture = value.Culture,
                        Segment = value.Segment,
                        Tab = elementType.TabsByProperty.GetValueOrDefault(value.Alias),
                        ChangedBy = NameOf(save, editorNames),
                        ChangedAt = save.SavedAt
                    };
                })];

            // A block that was added or taken away is credited to the save that did it; one whose
            // values changed is credited to the most recent of those changes.
            PendingBlockPropertyChange? latest = properties.MaxBy(property => property.ChangedAt);
            SaveAttribution blockSave = attributions.GetValueOrDefault(new ChangeKey(difference.Property, block.Element.Key, null));

            changes.Add(new PendingBlockChange
            {
                Key = block.Element.Key,
                OwnerKey = block.Element.OwnerKey,
                Scope = block.Element.Scope,
                Status = block.Status,
                ContentTypeKey = block.Element.ContentTypeKey,
                ContentTypeName = elementType.Name,
                ChangedBy = latest?.ChangedBy ?? NameOf(blockSave, editorNames),
                ChangedAt = latest?.ChangedAt ?? blockSave.SavedAt,
                Properties = properties
            });
        }

        return [.. changes.OrderByDescending(change => change.ChangedAt).ThenBy(change => change.Key)];
    }

    /// <summary>
    /// Names the editor behind a save.
    /// </summary>
    /// <param name="save">The save to name.</param>
    /// <param name="editorNames">The display names resolved for this document.</param>
    /// <returns>The editor's display name, or the closest thing to it that is known.</returns>
    private static string NameOf(SaveAttribution save, IReadOnlyDictionary<int, string> editorNames) =>
        editorNames.TryGetValue(save.UserId, out string? name) ? name : save.Username ?? UnknownEditorName;

    /// <summary>
    /// Reads what the response needs to know about a document or element type: its name, and which
    /// tab each of its properties is edited on, so a change can be pointed at the tab an editor
    /// would have to open to see it.
    /// </summary>
    /// <param name="contentTypeKey">The type to describe.</param>
    /// <param name="cache">Holds the types already read for this document.</param>
    /// <returns>The type's name and tabs, or an empty summary when it no longer exists.</returns>
    private ContentTypeSummary DescribeContentType(Guid contentTypeKey, Dictionary<Guid, ContentTypeSummary> cache)
    {
        if (cache.TryGetValue(contentTypeKey, out ContentTypeSummary? cached))
        {
            return cached;
        }

        IContentType? contentType = contentTypeKey == Guid.Empty ? null : _contentTypeService.Get(contentTypeKey);
        ContentTypeSummary summary = contentType is null
            ? ContentTypeSummary.Unknown
            : new ContentTypeSummary(contentType.Name, MapPropertiesToTabs(contentType));

        cache[contentTypeKey] = summary;

        return summary;
    }

    /// <summary>
    /// Works out which tab each of a type's properties is edited on.
    /// </summary>
    /// <param name="contentType">The document or element type to read.</param>
    /// <returns>A lookup of property alias to tab name, holding only properties that sit on a tab.</returns>
    private static IReadOnlyDictionary<string, string> MapPropertiesToTabs(IContentType contentType)
    {
        PropertyGroup[] groups = [.. contentType.CompositionPropertyGroups];

        // Compositions can contribute the same tab more than once; they merge into one in the editor.
        Dictionary<string, string> tabsByAlias = groups
            .Where(group => group.Type == PropertyGroupType.Tab && !string.IsNullOrWhiteSpace(group.Alias))
            .GroupBy(group => group.Alias!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                tab => tab.Key,
                tab => tab.Select(group => group.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? tab.Key,
                StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string> tabsByProperty = new(StringComparer.OrdinalIgnoreCase);

        foreach (PropertyGroup group in groups)
        {
            // A group nested in a tab carries the tab's alias as the first segment of its own.
            string? owningTabAlias = group.Alias?.Split('/').FirstOrDefault();
            if (owningTabAlias is null || group.PropertyTypes is null || !tabsByAlias.TryGetValue(owningTabAlias, out string? tabName))
            {
                continue;
            }

            foreach (IPropertyType propertyType in group.PropertyTypes)
            {
                tabsByProperty[propertyType.Alias] = tabName;
            }
        }

        return tabsByProperty;
    }

    /// <summary>
    /// Looks up the display names of the editors behind a set of saves.
    /// </summary>
    /// <param name="saves">The saves whose editors should be named.</param>
    /// <returns>A lookup of user id to display name, holding only users that still exist.</returns>
    private IReadOnlyDictionary<int, string> ResolveEditorNames(IEnumerable<SaveAttribution> saves)
    {
        int[] userIds = [.. saves.Select(save => save.UserId).Where(userId => userId > 0).Distinct()];
        if (userIds.Length == 0)
        {
            return new Dictionary<int, string>();
        }

        IEnumerable<IUser> users = _userService.GetUsersById(userIds) ?? [];

        return users
            .Where(user => !string.IsNullOrWhiteSpace(user.Name))
            .ToDictionary(user => user.Id, user => user.Name!);
    }

    /// <summary>
    /// Compares two stored property values the way an editor would read them, so that a missing
    /// value and an empty one count as the same thing.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><c>true</c> when the two values would show differently on the site.</returns>
    private static bool ValuesDiffer(object? left, object? right) =>
        !string.Equals(Normalise(left), Normalise(right), StringComparison.Ordinal);

    /// <summary>
    /// Compares two values that have already been reduced to their comparable form.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><c>true</c> when the two values would show differently on the site.</returns>
    private static bool ValuesDiffer(string? left, string? right) =>
        !string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>
    /// Reduces a stored property value to a comparable string, treating blank values as absent.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The comparable form of the value, or <c>null</c> when it holds nothing.</returns>
    private static string? Normalise(object? value) => value switch
    {
        null => null,
        string text => string.IsNullOrWhiteSpace(text) ? null : text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    /// <summary>
    /// Describes a save in the terms the response needs.
    /// </summary>
    /// <param name="version">The version metadata to describe.</param>
    /// <returns>The editor and time behind that save.</returns>
    private static SaveAttribution ToAttribution(ContentVersionMeta version)
    {
        version.EnsureUtc();

        return new SaveAttribution(version.UserId, version.Username, ToOffset(version.VersionDate));
    }

    /// <summary>
    /// Presents an Umbraco timestamp as an unambiguous instant for the backoffice to format.
    /// </summary>
    /// <param name="value">The stored timestamp, which Umbraco keeps in UTC.</param>
    /// <returns>The same instant with an explicit offset.</returns>
    private static DateTimeOffset ToOffset(DateTime value) =>
        new(value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime());

    /// <summary>Identifies one stored property value: its alias and, when it varies, its culture and segment.</summary>
    private readonly record struct PropertyValueKey(string Alias, string? Culture, string? Segment);

    /// <summary>
    /// Identifies one thing the history walk has to attribute to a save.
    /// </summary>
    /// <param name="Property">The document property holding it.</param>
    /// <param name="ElementKey">The block element it belongs to, or <c>null</c> for the property itself.</param>
    /// <param name="Value">The value inside that element, or <c>null</c> for the element existing at all.</param>
    private readonly record struct ChangeKey(PropertyValueKey Property, Guid? ElementKey, BlockElementValueKey? Value);

    /// <summary>One property value that is waiting to be published, and the blocks that explain it.</summary>
    /// <param name="Property">The property value that differs.</param>
    /// <param name="Blocks">The blocks inside it that differ, if it holds any.</param>
    private sealed record PropertyDifference(PropertyValueKey Property, IReadOnlyList<BlockDifference> Blocks);

    /// <summary>One block that publishing would add, change or take away.</summary>
    /// <param name="Element">The element as it is stored in whichever value still has it.</param>
    /// <param name="Status">What publishing would do to it.</param>
    /// <param name="Values">The values that differ, for a block that exists in both.</param>
    private sealed record BlockDifference(
        BlockElementSnapshot Element,
        BlockChangeStatus Status,
        IReadOnlyList<BlockElementValueKey> Values);

    /// <summary>What the response needs to know about a document or element type.</summary>
    /// <param name="Name">The type's name.</param>
    /// <param name="TabsByProperty">Which tab each of its properties is edited on.</param>
    private sealed record ContentTypeSummary(string? Name, IReadOnlyDictionary<string, string> TabsByProperty)
    {
        /// <summary>A type that could not be read: it names nothing and has no tabs.</summary>
        public static ContentTypeSummary Unknown { get; } = new(null, new Dictionary<string, string>());
    }

    /// <summary>The editor and moment behind a single save.</summary>
    private readonly record struct SaveAttribution(int UserId, string? Username, DateTimeOffset SavedAt);

    /// <summary>
    /// Reads the value behind a <see cref="ChangeKey"/> out of one version of a document, parsing
    /// each version's block values at most once.
    /// </summary>
    private sealed class VersionValueReader
    {
        private readonly Dictionary<(int VersionId, PropertyValueKey Property), IReadOnlyDictionary<Guid, BlockElementSnapshot>?> _blocks = [];

        /// <summary>
        /// Reads a document's own saved value, ignoring published values.
        /// </summary>
        /// <param name="content">The document or version to read from.</param>
        /// <param name="key">The value to read.</param>
        /// <returns>The comparable form of the value, or <c>null</c> when that version has no such value.</returns>
        public string? Read(IContent content, ChangeKey key)
        {
            object? value = content.GetValue(key.Property.Alias, key.Property.Culture, key.Property.Segment, published: false);

            if (key.ElementKey is not Guid elementKey)
            {
                return Normalise(value);
            }

            IReadOnlyDictionary<Guid, BlockElementSnapshot>? elements = ReadBlocks(content, key.Property, value);

            if (elements is null || !elements.TryGetValue(elementKey, out BlockElementSnapshot? element))
            {
                return null;
            }

            return key.Value is BlockElementValueKey valueKey
                ? element.Values.GetValueOrDefault(valueKey)
                : BlockPresentMarker;
        }

        /// <summary>
        /// Flattens one version's blocks for a property, remembering the result for the other keys
        /// that will ask for the same value.
        /// </summary>
        /// <param name="content">The version being read.</param>
        /// <param name="property">The property holding the blocks.</param>
        /// <param name="value">That property's stored value in this version.</param>
        /// <returns>The version's elements, or <c>null</c> when the value holds no blocks.</returns>
        private IReadOnlyDictionary<Guid, BlockElementSnapshot>? ReadBlocks(IContent content, PropertyValueKey property, object? value)
        {
            (int VersionId, PropertyValueKey Property) cacheKey = (content.VersionId, property);

            if (!_blocks.TryGetValue(cacheKey, out IReadOnlyDictionary<Guid, BlockElementSnapshot>? elements))
            {
                elements = BlockValueReader.Read(value);
                _blocks[cacheKey] = elements;
            }

            return elements;
        }
    }
}
