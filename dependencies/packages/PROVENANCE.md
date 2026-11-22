# Bundled Pegasus package

`Pegasus.Shared.1.20260711.421-a.nupkg` is the exact existing Pegasus package
selected by `VERSION.json`, copied from the existing Pegasus package feed on
2026-11-21. Its SHA256 was checked against that version manifest before copying
and checked again after copying. The package has not been rebuilt, repacked or
modified.

- Package ID: `Pegasus.Shared`
- Version: `1.20260711.421-a`
- Length: `114237` bytes
- SHA256: `9696db2ccc40e74649233cfbfa34b94a201d5810b71264ce62d1ae33a12a35a6`
- Declared author: Pegasus
- Declared purpose: versioned suite contracts and reusable .NET protocol utilities
- Declared target: `net8.0`, with no package dependencies in that target group

The existing package metadata contains no source-repository URL. This bundled
package preserves the admitted dependency without inventing a source provenance
or requiring the developer's sibling package directory. Its package version and
content remain pinned; a replacement requires a deliberate dependency update,
matching `VERSION.json` and the affected package locks.

Shayne Winter confirmed ownership and authorized public distribution of this
first-party package under the repository's [Pegasus Community License 1.0](../../LICENSE)
on November 21, 2026. This external grant accompanies the unchanged package;
the original nuspec is not represented as containing a license declaration.

`NuGet.Config` maps `Pegasus.*` to this repository-relative directory. Public
dependencies use the separately configured nuget.org source. Do not add signing
keys, credentials, certificates or unrelated package-cache contents here.
