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

    private readonly IContentService _contentService;
    private readonly IContentTypeService _contentTypeService;
    private readonly IContentVersionService _contentVersionService;
    private readonly IUserService _userService;
    private readonly ILogger<PendingChangesService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="contentService">Reads the document being inspected.</param>
    /// <param name="contentTypeService">Reads the document type, to name the tab each property sits on.</param>
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

        IReadOnlyCollection<PropertyValueKey> changedValues = FindUnpublishedValues(content);
        if (changedValues.Count == 0)
        {
            return new DocumentPendingChanges
            {
                DocumentId = documentId,
                State = DocumentPublishState.Published
            };
        }

        IReadOnlyCollection<PendingPropertyChange> properties = await AttributeChangesAsync(content, changedValues, cancellationToken);

        return new DocumentPendingChanges
        {
            DocumentId = documentId,
            State = DocumentPublishState.PendingChanges,
            Properties = properties
        };
    }

    /// <summary>
    /// Finds every stored property value whose saved (edited) value differs from the value the
    /// site is currently serving.
    /// </summary>
    /// <param name="content">The document to inspect.</param>
    /// <returns>The culture and segment specific property values that are waiting to be published.</returns>
    private static IReadOnlyCollection<PropertyValueKey> FindUnpublishedValues(IContent content)
    {
        List<PropertyValueKey> changed = [];

        foreach (IProperty property in content.Properties)
        {
            foreach (IPropertyValue value in property.Values)
            {
                if (ValuesDiffer(value.EditedValue, value.PublishedValue))
                {
                    changed.Add(new PropertyValueKey(property.Alias, value.Culture, value.Segment));
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Walks back through the save history and credits each unpublished value to the save that
    /// last changed it.
    /// </summary>
    /// <param name="content">The current draft of the document.</param>
    /// <param name="changedValues">The property values that differ from the published content.</param>
    /// <param name="cancellationToken">Cancels the per-version lookups.</param>
    /// <returns>One entry per unpublished value, carrying the editor's name and the time of their save.</returns>
    private async Task<IReadOnlyCollection<PendingPropertyChange>> AttributeChangesAsync(
        IContent content,
        IReadOnlyCollection<PropertyValueKey> changedValues,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ContentVersionMeta> versions = await GetRecentVersionsAsync(content.Key);

        HashSet<PropertyValueKey> unattributed = [.. changedValues];
        Dictionary<PropertyValueKey, SaveAttribution> attributions = [];

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

            foreach (PropertyValueKey key in unattributed.ToArray())
            {
                if (ValuesDiffer(GetDraftValue(newer, key), GetDraftValue(older, key)))
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
        foreach (PropertyValueKey key in unattributed)
        {
            attributions[key] = newerSave;
        }

        return BuildChanges(content, changedValues, attributions);
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
    /// <param name="changedValues">The property values that are waiting to be published.</param>
    /// <param name="attributions">The save each value was credited to.</param>
    /// <returns>The per-property changes, in property alias order.</returns>
    private IReadOnlyCollection<PendingPropertyChange> BuildChanges(
        IContent content,
        IReadOnlyCollection<PropertyValueKey> changedValues,
        IReadOnlyDictionary<PropertyValueKey, SaveAttribution> attributions)
    {
        IReadOnlyDictionary<int, string> editorNames = ResolveEditorNames(attributions.Values);
        IReadOnlyDictionary<string, string> tabNames = MapPropertiesToTabs(content);

        return [.. changedValues
            .Select(key =>
            {
                SaveAttribution save = attributions[key];

                return new PendingPropertyChange
                {
                    Alias = key.Alias,
                    Culture = key.Culture,
                    Segment = key.Segment,
                    Tab = tabNames.TryGetValue(key.Alias, out string? tab) ? tab : null,
                    ChangedBy = editorNames.TryGetValue(save.UserId, out string? name) ? name : save.Username ?? UnknownEditorName,
                    ChangedAt = save.SavedAt
                };
            })
            .OrderBy(change => change.Alias, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Works out which tab each of a document type's properties is edited on, so a change can be
    /// pointed at the tab an editor would have to open to see it.
    /// </summary>
    /// <param name="content">The document whose type should be read.</param>
    /// <returns>A lookup of property alias to tab name, holding only properties that sit on a tab.</returns>
    private IReadOnlyDictionary<string, string> MapPropertiesToTabs(IContent content)
    {
        IContentType? contentType = _contentTypeService.Get(content.ContentType.Key);
        if (contentType is null)
        {
            return new Dictionary<string, string>();
        }

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
    /// Reads a document's own saved value for one culture and segment, ignoring published values.
    /// </summary>
    /// <param name="content">The document or version to read from.</param>
    /// <param name="key">The property value to read.</param>
    /// <returns>The stored value, or <c>null</c> when that version has no such value.</returns>
    private static object? GetDraftValue(IContent content, PropertyValueKey key) =>
        content.GetValue(key.Alias, key.Culture, key.Segment, published: false);

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

    /// <summary>The editor and moment behind a single save.</summary>
    private readonly record struct SaveAttribution(int UserId, string? Username, DateTimeOffset SavedAt);
}
