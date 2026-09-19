# Koben.Umbraco.PendingChanges

Shows Umbraco editors which of a page's fields are **saved but not published**, who last changed
each one and when — in place, in the content editor.

This repository is the package source. For what it does and how to install it, see
[the package README](src/Koben.Umbraco.PendingChanges/README.md).

## Layout

```
src/Koben.Umbraco.PendingChanges/
  Services/            comparison and attribution (the whole of the logic)
  Controllers/         the backoffice Management API endpoint
  wwwroot/             umbraco-package.json + the backoffice extension (plain JS, no build step)
  PendingChangesComposer.cs
```

The client is deliberately a single hand-written ES module with no build step — it is small, it
imports everything it needs from the backoffice's own import map, and keeping it to one file keeps
the cache-busting story simple (see below).

## Build

```bash
dotnet build -c Release
dotnet pack src/Koben.Umbraco.PendingChanges -c Release -o artifacts
```

CI builds against each supported Umbraco version with `-p:UmbracoVersion=`; release builds use the
default floor in `Directory.Packages.props`.

## Umbraco versions

The floor is **18.0.0**, and lowering it is not a one-line change. Umbraco 18 moved
`IContent.Published` and `IContent.PublishedVersionId` down to `IPublishableContentBase`. Source
compiles against either major, but the compiled binary binds to whichever interface declared the
member, so a nupkg built against a 17 floor throws `MissingMethodException` on 18 and vice versa —
the same trap that took `Koben.Umbraco.StructuredData` 0.1.0-beta.2 out on a live site. Supporting
17 means reflection-bound accessors for those two members, as that package's `PublishedContentCompat`
does. Everything else this package touches (`IContentVersionService`, `IContentService.GetById`,
`IProperty.Values`, `IntExtensions.ToGuid`) exists unchanged in 17.6 and 18.

## Cache busting matters here

`wwwroot/umbraco-package.json` declares a `version`, and it has to. The backoffice appends
`?umb__rnd=<the manifest's version>` to every `/App_Plugins/` script it loads, but only when the
manifest declares a version and the path carries no query string of its own. Sites commonly put a
long `Cache-Control` on static `.js`, so a manifest with no version can leave editors running the
previous copy of the script for as long as that cache lasts.

**Bump the manifest version together with the package version whenever the script changes.**

## Verification

The comparison, the attribution and both indicators were exercised against a real Umbraco 18.1.1
site (koben.com.au's CMS): property flags, tab dots, twelve published pages checked for false
positives, and the diff cross-checked against Umbraco's own `document/{id}/published` endpoint.
No screenshots yet, and no test site; the sibling package repos
(`Koben.Umbraco.StructuredData`, `Koben.Umbraco.CloudflareStream`) carry one under `test/TestSite`
and this repo should grow the same thing before it is published anywhere.

## Licence

MIT © Koben Digital
