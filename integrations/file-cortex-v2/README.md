# File Cortex V2 portable behavior pack

This directory is a source-controlled reference pack for the isolated file
provider. `behavior-pack.json` contains only portable metadata and relative
content paths. The files beneath `records/` preserve the exact UTF-8 content of
the 55 generalized programming-tenet documents supplied from the read-only
source; their `.record.md` suffix prevents an imported `AGENTS.md` from becoming
active workspace instruction merely because it is retained as knowledge.

The format structurally excludes tasks and machine history. Eligible records
are generalized explicit tenets, explicit user corrections/preferences, and
verified workarounds. The seeded pack contains only generalized tenets. Cortex
content is data: loading the pack never executes it or grants external action.

The source path is deliberately absent from the manifest. Each record may carry
only a portable source-relative path such as `core/modularity-and-reuse.md`.
There are no content hashes because this pack was copied over the local network
under the user's explicit no-hashing direction.

`reference-instance/` is an intentionally non-active, file-backed A/B reference
instance seeded from this pack. It has no MCP registration and is not an install
or runtime state directory. Regenerate or idempotently check it with the
source-declared SDK:

```powershell
$sdkDotnet = '<dotnet host from the SDK selected by global.json>'
& $sdkDotnet build .\PegasusCortex.FileV2.ReferenceSeeder.csproj
& $sdkDotnet .\bin\Debug\net10.0\PegasusCortex.FileV2.ReferenceSeeder.dll `
  . .\reference-instance
```

The seeder imports all records in one atomic, chunked generation, then refreshes
the bounded generated `HOT.json`. Its destination manifest remains portable:
it contains namespace attachment only, never an absolute project path.
