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

The floor is **18.0.2**, and lowering it is not a one-line change. The `.2` is deliberate:
18.0.0 and 18.0.1 carry [GHSA-wr57-hqmp-fgvh](https://github.com/advisories/GHSA-wr57-hqmp-fgvh),
so a lower floor would let a consuming site resolve a vulnerable Umbraco. Umbraco 18 moved
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

## Test site

`test/TestSite` is a throwaway SQLite Umbraco install that references the package **from the packed
nupkg**, not by project reference, so a run exercises what a consuming site actually gets. Pack
before you restore it:

```bash
dotnet pack src/Koben.Umbraco.PendingChanges -c Release -o artifacts
dotnet build test/TestSite -c Release
dotnet run --project test/TestSite -c Release --no-build --urls http://localhost:5055
```

CI does the same and then checks, without logging in, that the static web assets are served under
`/App_Plugins/Koben.PendingChanges/` and that the API route answers 401 rather than 404 (registered
and protected, versus not registered at all).

Iterating locally: NuGet caches by id *and* version, so re-packing the same version will not be
picked up. `rm -rf ~/.nuget/packages/koben.umbraco.pendingchanges artifacts` between runs.

The admin credentials in `test/TestSite/appsettings.Development.json` are throwaway values for an
unattended local install. They are not used anywhere else.

## Verification

The comparison, the attribution and both indicators were exercised against a real Umbraco 18.1.1
site (koben.com.au's CMS): property flags, tab dots, twelve published pages checked for false
positives, and the diff cross-checked against Umbraco's own `document/{id}/published` endpoint. No
screenshots in the README yet.

## Releasing

nuget.org publishing is Trusted Publishing (OIDC) — no API key is stored anywhere. One-time setup:

- nuget.org → your profile → **Trusted Publishing** → a policy **owned by the KobenDigital
  organisation**, for repository owner `KOBENDigital`, repository `Koben.Umbraco.PendingChanges`,
  workflow file `release.yml`, environment `nuget`.
- GitHub → repository secret `NUGET_USER` = the nuget.org **profile name** that owns the policy
  (a username, not an email).

To release: bump `<Version>` in the csproj **and** `version` in `wwwroot/umbraco-package.json` — the
release workflow fails if the tag and the two versions disagree — then:

```bash
git tag v0.1.0 && git push origin v0.1.0
```

## Licence

MIT © Koben Digital
