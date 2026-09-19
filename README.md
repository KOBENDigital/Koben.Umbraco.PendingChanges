# Koben.Umbraco.PendingChanges

Shows Umbraco editors which of a page's fields are **saved but not published**, who last changed
each one and when — in place, in the content editor.

This repository is the package source. For what it does and how to install it, see
[the package README](src/Koben.Umbraco.PendingChanges/README.md).

## Layout

```
src/Koben.Umbraco.PendingChanges/
  Services/            comparison and attribution (the whole of the logic)
    BlockValueReader   reads the blocks out of a stored block editor value
  Controllers/         the backoffice Management API endpoint
  wwwroot/             umbraco-package.json + the backoffice extension (plain JS, no build step)
  PendingChangesComposer.cs
```

The one backoffice extension file is registered twice: once for the document workspace, once for a
block's. Umbraco creates a workspace context extension as `new Api(host, workspaceContext)`, and
which workspace the second argument belongs to is what the class branches on. Two files would need
two manifest entries anyway, and a second file imported from the first would not get the manifest's
cache-busting query (see below).

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
picked up. `rm -rf ~/.nuget/packages/koben.umbraco.pendingchanges artifacts` between runs. Stop the
site before re-running it — it holds the SQLite file open, so a second run fails on a locked
database rather than on the port.

The connection string leaves out the `Cache=Shared` that Umbraco's own template carries. With a
shared cache, SQLite locks whole tables across the connections in a process, and a read that meets
a write fails outright with `SQLite Error 6: 'database table is locked'` instead of waiting. In
practice that lands on OpenIddict's token store — every backoffice request validates a token — so a
busy moment in the editor shows up as a 400 from the token endpoint and a backoffice that looks
broken. Without it, each connection has its own cache, and WAL plus the busy timeout lets readers
and the writer through.

The admin credentials in `test/TestSite/appsettings.Development.json` are throwaway values for an
unattended local install. They are not used anywhere else. That file also sets
`Umbraco:CMS:Global:UseHttps` to `false`, which is what lets the backoffice *log in* over the plain
http the run command uses: OpenIddict refuses an authorization request over http unless Umbraco
tells it to, and Umbraco tells it to exactly when `UseHttps` is off.

## Verification

The comparison, the attribution and both indicators were exercised against a real Umbraco 18.1.1
site (koben.com.au's CMS): property flags, tab dots, twelve published pages checked for false
positives, and the diff cross-checked against Umbraco's own `document/{id}/published` endpoint. No
screenshots in the README yet.

Block flagging was exercised against `test/TestSite` on Umbraco 18.1.1: a page with a Block List of
three blocks, one edited (content and settings), one untouched, one added, and a nested block added
inside another block. Checked in the backoffice and against the endpoint: only the blocks that
differ are marked, an added block reads **New** and an edited one **Edited**, a block's own editor
flags only the properties that differ in both its content and its settings views, and publishing
the page takes every mark away. The block tab dot — a dot on a tab *inside* a block's editor — uses
the same code as the document's and has not been exercised on a block whose element type has more
than one tab.

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
