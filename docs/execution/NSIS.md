# Typed NSIS compiler adapter

Reusable Windows installer generation and certificate guidance are shipped in
`tools\windows-installer\README.md` beneath the Cortex install root. Start
there for another Pegasus product. The generator uses a validated small JSON
definition, the existing NSIS compiler adapter, and shared signing scripts in
`tools\signing`. Cortex consumes that same generator. No project-source path is
required after installation.

The unified Pegasus Cortex package carries the admitted NSIS 3.12 distribution
under `tools\nsis\3.12`. Its source archive SHA-256 is
`56581f90db321581c5381193d796fffcf2d24b2f8fed2160a6c6a3baa67f2c4f` and
the upstream `Bin\makensis.exe` SHA-256 is
`25d1aa7081db1a9de9690b59983f9652b7409a189c3444328cc1841eff693e8d`.
The package signs the compiler as a shipped executable, then binds its signed
bytes in `tools\nsis\3.12\pegasus-nsis-admission.v1.json` and the canonical
signed product manifest. Runtime admission verifies that signed-byte digest
before and after the exact `/VERSION` probe. It never searches `PATH`.

A separately registered, hash-pinned tool named `nsis` remains a compatible
fallback for development and older layouts. It must pass a bounded `/?` probe
containing `MakeNSIS`. The immutable product provider takes precedence in an
installed unified product and is reported as `provider=product`.

Fixed operations:

- `nsis_status` reports whether the registered definition is probe-verified.
- `nsis_validate` runs `/SAFEPPO /V4 /WX /NOCONFIG /OUTPUTCHARSET UTF8` and
  returns bounded line/byte/SHA-256 metadata plus a bounded trace tail.
- `nsis_build` first performs the same safe preprocess, then compiles one
  admitted `.nsi` to one absent exact `.exe`. The script must contain exactly
  one `OutFile "${PAO_OUT}"`; AgentOps supplies and verifies that destination.

No operation accepts `/X`, stdin scripts, arbitrary defines, launch switches,
or passthrough arguments. Compile-time process/filesystem directives such as
`!system`, `!execute`, `!appendfile`, `!delfile`, `!packhdr`, and finalize hooks
are rejected before compilation. Warnings are errors. The final output must be
a nonempty bounded PE file and is returned with byte length and SHA-256.

```json
{"op":"nsis_validate","src":"setup/AgentOps.nsi","ws":"project","tmo":60}
```

```text
pao nsis build setup/AgentOps.nsi artifacts/AgentOps-Setup.exe ws=project tmo=120
```

The standalone `pao.exe` compact CLI and its `mcp` JSON-RPC mode use the same
operation catalog, normalizer, dispatcher, engine, receipt, and error taxonomy.
Use `pao --json nsis status` when a script needs the exact receipt object,
`pao help nsis_validate` or `pao help nsis_build` for the live contract, and
`pao.exe mcp` only for an MCP client. `nsis_unconfigured` means neither the
admitted product compiler nor a probe-verified fallback is available; repair
the unified product or explicitly register the development fallback. It never
authorizes an automatic download or installation.

NSIS is a bounded build adapter, not a generic command transport. The product
includes the upstream `COPYING` and distribution license files. Release
construction uses the admitted compiler directly, signs all final PE content,
then creates and signs the canonical manifests; ordinary AgentOps calls cannot
invoke release-only signing or finalize hooks.
