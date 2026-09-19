# Koben.Umbraco.PendingChanges

Shows Umbraco editors which of a page's fields are **saved but not published**, who last changed
each one and when — in place, in the content editor.

Open a page with unpublished edits and every property whose saved value differs from the published
value gets an amber edge and a tag under its label:

> ✎ **Not published · Jane Doe · 2h ago**

Every tab holding one of those properties gets an amber dot, so a change on a tab you are not
looking at is still visible. A page whose saved content matches what is live looks exactly as it
always did.

## Install

```bash
dotnet add package Koben.Umbraco.PendingChanges
```

That is the whole setup. The comparison service is registered by a composer, the API controller and
the backoffice extension are found by Umbraco's own scanning. Editors need access to the Content
section; nothing else is configurable.

## Requirements

Umbraco **18.0.0** or newer, .NET 10.

## How it decides

- **What changed:** each stored property value's edited value is compared with its published value,
  per culture and segment, so what is flagged is exactly what publishing would push live. A missing
  value and an empty one count as the same thing.
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
      "changedAt": "2026-09-19T03:01:02.847+00:00"
    }
  ]
}
```

`state` is `NotPublished`, `Published` or `PendingChanges`.

## A note on how the flags are drawn

Umbraco has no extension point for decorating an individual property, so the flags are applied to
the rendered property elements by a document workspace context. They are re-applied when the
workspace re-renders and removed when it closes. If an Umbraco release changes the content editor's
markup the flags stop appearing — nothing else in the backoffice is affected. The tab dot is
Umbraco's own `umb-badge` in the tab's `extra` slot, the same element and position Umbraco uses for
a tab's validation badge; if a tab carries both at once they overlap.

## Licence

MIT © Koben Digital
