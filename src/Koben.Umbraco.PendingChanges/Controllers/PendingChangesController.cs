using Koben.Umbraco.PendingChanges.Controllers.Contracts;
using Koben.Umbraco.PendingChanges.Services.Interfaces;
using Koben.Umbraco.PendingChanges.Services.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Web.Common.Authorization;

namespace Koben.Umbraco.PendingChanges.Controllers;

/// <summary>Serves the backoffice extension the list of a document's unpublished values.</summary>
[VersionedApiBackOfficeRoute("pending-changes")]
[ApiExplorerSettings(GroupName = "Pending Changes")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
public sealed class PendingChangesController : ManagementApiControllerBase
{
    private const string DocumentNotFoundProblem = "urn:koben:pending-changes:document-not-found";

    private readonly IPendingChangesService _pendingChangesService;

    /// <summary>Creates the controller.</summary>
    /// <param name="pendingChangesService">Compares a document's saved values with its published values.</param>
    public PendingChangesController(IPendingChangesService pendingChangesService)
    {
        _pendingChangesService = pendingChangesService;
    }

    /// <summary>Returns the document's publish state and every value waiting to be published.</summary>
    /// <param name="documentId">The key of the document to inspect.</param>
    /// <param name="cancellationToken">Cancels the version lookups used to attribute each change.</param>
    /// <returns>The document's pending changes, or a problem response when no such document exists.</returns>
    [HttpGet("document/{documentId:guid}")]
    [ProducesResponseType(typeof(PendingChangesResponseModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ByDocumentIdAsync(Guid documentId, CancellationToken cancellationToken)
    {
        DocumentPendingChanges? pendingChanges = await _pendingChangesService.GetPendingChangesAsync(documentId, cancellationToken);

        if (pendingChanges is null)
        {
            return Problem(
                detail: $"No document has the id {documentId}.",
                title: "Document not found",
                statusCode: StatusCodes.Status404NotFound,
                type: DocumentNotFoundProblem);
        }

        return Ok(Map(pendingChanges));
    }

    /// <summary>
    /// Shapes the service's result for the backoffice, which only needs to know what to flag and who to name.
    /// </summary>
    /// <param name="pendingChanges">The document's unpublished state.</param>
    /// <returns>The response the backoffice extension consumes.</returns>
    private static PendingChangesResponseModel Map(DocumentPendingChanges pendingChanges) => new()
    {
        DocumentId = pendingChanges.DocumentId,
        State = pendingChanges.State,
        Properties = [.. pendingChanges.Properties.Select(change => new PendingPropertyChangeResponseModel
        {
            Alias = change.Alias,
            Culture = change.Culture,
            Segment = change.Segment,
            Tab = change.Tab,
            ChangedBy = change.ChangedBy,
            ChangedAt = change.ChangedAt,
            Blocks = [.. change.Blocks.Select(Map)]
        })]
    };

    /// <summary>
    /// Shapes one block's difference for the backoffice, which needs to know which block to mark
    /// and which of its properties to flag once it is opened.
    /// </summary>
    /// <param name="change">The block that changed.</param>
    /// <returns>The response the backoffice extension consumes.</returns>
    private static PendingBlockChangeResponseModel Map(PendingBlockChange change) => new()
    {
        Key = change.Key,
        OwnerKey = change.OwnerKey,
        Scope = change.Scope,
        Status = change.Status,
        ContentTypeKey = change.ContentTypeKey,
        ContentTypeName = change.ContentTypeName,
        ChangedBy = change.ChangedBy,
        ChangedAt = change.ChangedAt,
        Properties = [.. change.Properties.Select(property => new PendingBlockPropertyChangeResponseModel
        {
            Alias = property.Alias,
            Culture = property.Culture,
            Segment = property.Segment,
            Tab = property.Tab,
            ChangedBy = property.ChangedBy,
            ChangedAt = property.ChangedAt
        })]
    };
}
