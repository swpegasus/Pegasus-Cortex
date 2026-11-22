# Cortex artwork and branding kit

`Cortex.Graphic.png` is the full-contrast Cortex master, generated with AI image
assistance for this product refresh. Its C-shaped ribbon and connected nodes
follow the suite's blue, white and cyan visual style. Use this master for icons
and ordinary product artwork.

`Cortex.Watermark.png` is the separate transparent GUI-background artwork. It is
not the source for executable, tray or file-association icons. The GUI owns its
display opacity; exported icon pixels retain full contrast.

## Current asset family

- `Pegasus.AgentOps.png` / `.ico`: main application and stable launcher.
- `CortexAppMark.svg`: the main raster-backed SVG wrapper.
- `Cortex.<Role>.png` / `.ico` / `.svg`: eleven role variants; the unchanged
  badge definitions and project mappings are in `CortexRoleArtwork.psd1`.
- `Cortex.IconFamily.preview.png`: actual 16px/32px icons and enlarged pixels
  on light and dark backgrounds.

Every ICO contains nine PNG-compressed 32-bit frames: 16, 20, 24, 32, 40, 48,
64, 128 and 256 pixels. The PNG previews are 512px. Rounded icon corners have
alpha transparency, while the artwork remains full contrast.

**The SVGs are not pure vector artwork.** Each references the adjacent
`Cortex.Graphic.png` and adds vector clipping and, for role variants, a vector
badge. Keep that PNG beside the SVG when copying or distributing it. PNG and
ICO files are self-contained.

## Regenerate

On Windows with PowerShell 7 and its native System.Drawing support, run the
existing generator from a source checkout:

```powershell
& .\src\Pegasus.AgentOps.Tray\Assets\New-CortexAppMark.ps1
```

It resizes the fixed master and overlays the existing code-defined badges;
it does not generate new artwork or access an installed product. `-RoleIconsOnly`
retains its existing partial-regeneration behavior. Use the default invocation
for a coherent complete family. The same icon-generation script can run inside
an extracted branding kit, where its source images and role data are adjacent.

## Export the kit

From the repository, run:

```powershell
& .\tools\New-CortexBrandingKit.ps1
```

The fixed output is `artifacts/branding/CortexBrandingKit/`, alongside
`CortexBrandingKit.zip` and its `.sha256` checksum. The exporter includes only
the public artwork, source generator/exporter, role/palette data, this guide,
the repository license, provenance and a file hash inventory. It contains no
application executable, user data, certificates or private keys.

Existing outputs are refused by default. `-Replace` replaces only the exact
known files, after rejecting unexpected entries and reparse paths. It never
recursively deletes an output directory. Individual files and the archive
publish through temporary files; an interrupted export can be rerun explicitly
with `-Replace`.

`CortexBrandingKit.inventory.json` records relative filenames, byte lengths and
SHA256 digests for every other kit file. The adjacent checksum covers the final
ZIP. ZIP entries use a fixed timestamp and sorted names; reproducibility assumes
the same source assets and native graphics/runtime versions.

Existing executable and file-association mappings keep their current asset
paths. Regenerating artwork does not rebuild or update an installed application.

## Existing build and installer consumers

The explicit mappings in `Directory.Build.targets` and the tray project use:

| Asset family | Source projects |
| --- | --- |
| Main (`Pegasus.AgentOps`) | `Pegasus.AgentOps.Tray`, `PegasusCortex.Launcher` |
| Command | `PegasusCortex.App`, `Pegasus.AgentOps.ProcessHost` |
| Pao | `pao` |
| Service | `Pegasus.AgentOps.Service`, `PegasusCortex.Service` |
| Ui | `Pegasus.AgentOps.UiHost` |
| Setup | `PegasusCortex.Setup`, `PegasusCortex.InstallerBootstrap` |
| Connection | `PegasusCortex.Mcp.Local`, `PegasusCortex.Mcp`, `PegasusCortex.Mcp.Integrated` |
| Sync | `PegasusCortex.Sync.Client` |
| Export | `PegasusCortex.LearningExportCli` |
| Archive | `PegasusCortex.ArchiveTool` |
| Diagnostics | `PegasusCortex.V2.Benchmarks`, `PegasusCortex.ContinuityMcpTestHost` |
| Library | `VisualLibrary` |

The normal unified publisher also copies the main ICO to
`branding/PegasusCortex.ico` and the Setup ICO to `branding/Cortex.Setup.ico`.
The installer definition uses the main launcher's embedded icon for its normal
shortcut and Windows uninstall entry, and the Setup ICO for the wizard. These
paths remain unchanged; this refresh adds no file association or startup entry.

The included `LICENSE` contains the repository's applicable product terms.
