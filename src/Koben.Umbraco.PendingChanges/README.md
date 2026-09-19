# Koben.Umbraco.PendingChanges

Shows Umbraco editors which of a page's fields are **saved but not published**, who last changed
each one and when — in place, in the content editor.

Open a page with unpublished edits and every property whose saved value differs from the published
value gets an amber edge and a tag under its label:

> ✎ **Not published · Jane Doe · 2h ago**

Every tab holding one of those properties gets an amber dot, so a change on a tab you are not
looking at is still visible. A page whose saved content matches what is live looks exactly as it
always did.

## Blocks

A block editor is one property, so "this property is not published" is rarely enough: the page has
one Block Grid on it and the editor still has to find what changed inside it. So the blocks are
flagged too.

Each block whose content or settings are waiting to be published is outlined on its own card, with
a tag saying which it is:

> ✎ **New** — the block is not on the live page at all yet
>
> ✎ **Edited** — the block is live, but not as it now stands

Open that block and only the properties that actually differ carry the usual **Not published** tag —
its settings as well as its content. Blocks nested inside blocks are flagged the same way, at every
depth, so an edit buried three levels down leaves a trail of marks from the page to the field.
Block List, Block Grid, Block (single) and the blocks inside a rich text property are all read the
same way, and so is any block editor that stores its value the way those do.

## Install

```bash
dotnet add package Koben.Umbraco.PendingChanges
```

That is the whole setup. The comparison service is registered by a composer, the API controller and
the backoffice extension are found by Umbraco's own scanning. Editors need access to the Content
section; nothing else is configurable.

## Requirements

Umbraco **18.0.2** or newer, .NET 10. (18.0.0 and 18.0.1 are excluded by
[GHSA-wr57-hqmp-fgvh](https://github.com/advisories/GHSA-wr57-hqmp-fgvh).)

## How it decides

- **What changed:** each stored property value's edited value is compared with its published value,
  per culture and segment, so what is flagged is exactly what publishing would push live. A missing
  value and an empty one count as the same thing.
- **Which block:** a value holding blocks is compared block by block, matching each block to the
  published copy of itself by its key, so a block reads as added, changed or removed and a changed
  block names the properties behind it. Nested blocks are part of the same comparison — a block's
  own value is just more of the same JSON.
- **Who changed it:** the document's save history is walked back (up to 12 versions) and each
  changed value is credited to the save that last changed it. Anything older than that window — or a
  version that Umbraco's content version cleanup has removed — is credited to the oldest save read.
- **Which tab:** resolved from the document type's property groups, so a change is pointed at the
  tab an editor would have to open to see it.
- **Never published:** a document with nothing live reports `NotPublished` and is *not* flagged
  field by field, because every field would be flagged and nothing would stand out.

## API

```
GET /umbraco/management/api/v1/pending-changes/document/{documentId}
```

Backoffice authentication, Content section access. Responds:

```json
{
  "documentId": "34dc9a25-77a2-48f4-a386-2101da4e6eb4",
  "state": "PendingChanges",
  "properties": [
    {
      "alias": "heroTitle",
      "culture": null,
      "segment": null,
      "tab": "Hero",
      "changedBy": "Jane Doe",
      "changedAt": "2026-09-19T03:01:02.847+00:00",
      "blocks": []
    },
    {
      "alias": "content",
      "culture": null,
      "segment": null,
      "tab": "Content",
      "changedBy": "Jane Doe",
      "changedAt": "2026-09-19T03:04:11.219+00:00",
      "blocks": [
        {
          "key": "1eee80ce-ddfe-44e2-9bf9-e3d80c37aef3",
          "ownerKey": "1eee80ce-ddfe-44e2-9bf9-e3d80c37aef3",
          "scope": "Content",
          "status": "Changed",
          "contentTypeKey": "2635900d-ee72-4bdc-b967-7e4e5bf53316",
          "contentTypeName": "Text Block",
          "changedBy": "Jane Doe",
          "changedAt": "2026-09-19T03:04:11.219+00:00",
          "properties": [
            {
              "alias": "heading",
              "culture": null,
              "segment": null,
              "tab": "Main",
              "changedBy": "Jane Doe",
              "changedAt": "2026-09-19T03:04:11.219+00:00"
            }
          ]
        }
      ]
    }
  ]
}
```

`state` is `NotPublished`, `Published` or `PendingChanges`.

A property's `blocks` holds one entry per block element that differs, at any depth of nesting, and
is empty for a property that holds no blocks. `scope` is `Content` or `Settings` and `status` is
`Added`, `Changed` or `Removed`. `key` is the element that changed; `ownerKey` is the block it
belongs to, which is the same thing for a block's content and the block's own key for its settings,
so both point at one card in the editor. A `Changed` block lists the properties behind it; an
`Added` or `Removed` one does not, because all of it is new or all of it is going.

## A note on how the flags are drawn

Umbraco has no extension point for decorating an individual property, so the flags are applied to
the rendered property elements by a document workspace context. They are re-applied when the
workspace re-renders and removed when it closes. If an Umbraco release changes the content editor's
markup the flags stop appearing — nothing else in the backoffice is affected. The tab dot is
Umbraco's own `umb-badge` in the tab's `extra` slot, the same element and position Umbraco uses for
a tab's validation badge; if a tab carries both at once they overlap.

A block's own editor is a workspace of its own — in an overlay, or expanded inline — so a second
workspace context does the same job there, and takes the document's answer from the first rather
than asking the server again. The outline on a block's card is drawn inside the card, because a
block card has no slot of its own to render into; it is taken off again with everything else.

## Licence

MIT © Koben Digital
