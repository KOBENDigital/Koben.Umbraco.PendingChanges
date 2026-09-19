# AGENTS.md — Koben.Umbraco.PendingChanges

Rules for agents working in this repository. Modelled on `Koben.Umbraco.StructuredData/AGENTS.md`.

## What this is

An Umbraco **18+** package that flags a document's saved-but-unpublished property values in the
content editor, names the editor behind each one, and dots the tabs holding them. NuGet id
`Koben.Umbraco.PendingChanges`, MIT.

## The two halves

`Services/PendingChangesService.cs` is the whole of the logic: compare each stored property value's
edited value with its published value, then walk the save history to credit each difference to the
save that last changed it. `wwwroot/pending-changes.js` is the whole of the UI. The API between
them is the response contract in `Controllers/Contracts/`.

## The client is DOM decoration, and that is a deliberate cost

Umbraco has no extension point for decorating one property. The workspace context walks the
workspace's shadow roots for `umb-content-workspace-property[alias]` elements and attaches its own
markup. Rules for changing it:

- Never descend into a property element: the walk stops there, so a block editor's own properties
  are never mistaken for the document's.
- Everything added carries `data-koben-pending-change` / `data-koben-pending-tab` and is removed in
  `destroy()`. Nothing is left behind when the workspace closes.
- The element names it depends on (`umb-content-workspace-property`, `umb-property-type-based-property`,
  `umb-property`, `umb-property-layout`, `uui-tab[data-mark^="content-tab:tab/"]`) are Umbraco
  internals. When an Umbraco upgrade changes them, the flags stop appearing; that is the failure
  mode to design for, and it must stay a silent no-op rather than an error.

## Don't break cache busting

`wwwroot/umbraco-package.json` must keep its `version`, the script path must stay query-free, and
the version must be bumped whenever the script changes. See the README section for why.

## Umbraco version floor

18.0.2. See the README before lowering it — `IContent.Published` and `IContent.PublishedVersionId`
moved interfaces in 18 and the break is at runtime, not compile time.

## Verification

No test project (deliberate). Verify against a real backoffice: a page with unpublished edits shows
flags and tab dots, a fully published page is untouched, and the flagged aliases match
`GET /umbraco/management/api/v1/pending-changes/document/{id}`. Cross-check the comparison against
Umbraco's own `GET /umbraco/management/api/v1/document/{id}/published` when changing it.
