# AGENTS.md — Koben.Umbraco.PendingChanges

Rules for agents working in this repository. Modelled on `Koben.Umbraco.StructuredData/AGENTS.md`.

## What this is

An Umbraco **18+** package that flags a document's saved-but-unpublished property values in the
content editor, names the editor behind each one, dots the tabs holding them, and marks the blocks
inside those values — the cards in a block editor, and the block's own properties once it is open.
NuGet id `Koben.Umbraco.PendingChanges`, MIT.

## The two halves

`Services/PendingChangesService.cs` is the whole of the logic: compare each stored property value's
edited value with its published value, then walk the save history to credit each difference to the
save that last changed it. `wwwroot/pending-changes.js` is the whole of the UI. The API between
them is the response contract in `Controllers/Contracts/`.

`Services/BlockValueReader.cs` flattens a block editor's stored value into its elements, keyed by
element key, and the same comparison then runs over those. Two things about that format are load
bearing and neither is obvious:

- **A nested block editor's value is stored as JSON *text* inside the outer JSON**, not as a nested
  object. The reader parses a string value again when it looks like an object; without that, blocks
  inside blocks are invisible and nothing below the first level is ever flagged.
- **A block's settings are a separate element** with a key of their own. Only the `layout` entries
  pair a settings key with its content key, which is how a settings change is pointed at the card
  an editor would click. `expose` entries carry a `contentKey` too and must not be mistaken for a
  pairing — the reader takes a pairing only from an object carrying both keys.

The reader is deliberately shape-driven rather than bound to a property editor alias: it looks for
`contentData`/`settingsData` anywhere in a value, which is what makes one pass cover Block List,
Block Grid, Block (single) and rich text blocks.

## The client is DOM decoration, and that is a deliberate cost

Umbraco has no extension point for decorating one property. The workspace context walks the
workspace's shadow roots for `umb-content-workspace-property[alias]` elements and attaches its own
markup. Rules for changing it:

- A document property is only the document's own when nothing above it in the walk was a property.
  The walk now carries on through a property to reach the block cards inside it — but only when
  there are unpublished blocks to find, and it collects nothing else on the way.
- Inside a property, only roots belonging to block elements are watched for re-renders. A property
  editor re-renders on every keystroke and none of that can change what is waiting to be published.
- Everything added carries `data-koben-pending-change` / `data-koben-pending-block` /
  `data-koben-pending-tab` and is removed in `destroy()`. Nothing is left behind when the workspace
  closes — and a block's overlay goes with it, even though it lives inside another component's
  shadow root.
- The element names it depends on (`umb-content-workspace-property`, `umb-property-type-based-property`,
  `umb-property`, `umb-property-layout`, `uui-tab[data-mark^="content-tab:tab/"]`,
  `umb-block-workspace-view-edit-property`, and the `umb-block-*-entry` / `umb-rte-block` cards) are
  Umbraco internals. When an Umbraco upgrade changes them, the flags stop appearing; that is the
  failure mode to design for, and it must stay a silent no-op rather than an error.
- The same file is registered twice in the manifest: once against `Umb.Workspace.Document`, once
  against `Umb.Workspace.Block`. Umbraco creates a workspace context extension as
  `new Api(host, workspaceContext)` — the workspace is the **second** argument, and mixing that up
  silently gives you two document-mode instances rather than one of each. The document instance
  fetches and provides `Koben.PendingChanges`; the block instance consumes it, which works from
  inside a modal because Umbraco proxies context requests out of one.
- A block's properties are matched to a block by asking the rendered property which element it is
  bound to (`ownerContext.getUnique()`), never by where it was found. That is what keeps a block
  nested inside the open one from being credited to its parent.

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

For blocks, the page to build is a Block List with a block that has both a content and a settings
element type, and an element type that contains the same block list again so nesting is exercised.
Then: edit one block, leave another alone, add a third, and add one inside another — saved, not
published. The endpoint should name exactly those, and the editor should mark exactly those cards.
Reading the raw value out of `umbracoPropertyData` is the fastest way to see what the comparison is
actually being handed.
