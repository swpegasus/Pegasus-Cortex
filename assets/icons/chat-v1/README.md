# Pegasus Cerebral chat and status icon pack, version 1

This compact pack contains 11 Microsoft Fluent System Icons in the regular 20px style.
It supplies neutral status and chat shapes for native Desktop and plugins. Pegasus product
branding remains in the existing branding resource pack.

Upstream: https://github.com/microsoft/fluentui-system-icons  
Pinned revision: `cd332ec1f90cda2fad16f86754f8da52f1e9ab30`  
License: MIT, copyright (c) 2020 Microsoft Corporation. Keep `LICENSE.txt` with redistributed assets.
`UPSTREAM-NOTICE.txt` preserves the upstream repository's accompanying notices; no upstream
build tools, fonts, npm packages or platform libraries are included. The upstream license
was checked on 2026-10-07 against the repository's LICENSE.

The SVG files are exact upstream bytes. `index.ini` maps semantic keys to source paths,
pinned source URLs, human labels, descriptions, and native WPF geometry. Geometry is
the upstream SVG path with WPF's `F1` nonzero fill rule prepended. Native rendering replaces
the SVG's fixed ink color with the host's current text brush while retaining the full
20px canvas. `SHA256SUMS.txt` records the delivered text/source files.

| Key | Meaning | Upstream source |
| --- | --- | --- |
| status.success | Succeeded; completed successfully | Checkmark Circle |
| status.error | Failed; diagnostic details remain available | Dismiss Circle |
| status.warning | Attention needed or completed with a warning | Warning |
| status.working | Currently running; static glyph | Arrow Sync |
| status.info | Neutral information | Info |
| status.cancelled | Cancelled or stopped | Prohibited |
| status.awaiting | Queued or awaiting input; retain precise text | Clock |
| chat.message | Chat message or conversation | Chat |
| chat.assistant | AI assistant contribution | Bot |
| chat.tool | Tool operation or details | Wrench |
| chat.attachment | Attached file | Attach |

## Native WPF and plugin use

The public helper is in `Cerebral.Desktop.Plugins.dll`:

```csharp
FrameworkElement icon = DesktopIcons.Create(
    DesktopIconKeys.Success, size: 16, productPaths: productPaths);
```

Get the current `DesktopProductPaths` from a host implementing `IDesktopProductEnvironment`.
The `IDesktopPluginEnvironment` package/data directories do not imply shared resource roots.
Do not persist a guessed installation path. The helper checks these locations, in order:

1. `ProductPaths.SharedDirectory/Icons/chat-v1/index.ini`, when current product paths are supplied.
2. `ProductPaths.InstallDirectory/Resources/Icons/chat-v1/index.ini`.
3. `AppContext.BaseDirectory/Resources/Icons/chat-v1/index.ini`.
4. The embedded index in the shared SDK assembly.

The SDK project copies the complete pack to `Resources/Icons/chat-v1` on build and publish;
it does not modify an installed shared directory. Plugin package resource declarations can
also distribute the complete pack as a `shared` resource at `Icons/chat-v1` through the
existing reviewed resource-install flow. Retain the index, source mapping, license and hashes
together when copying the pack.

`DesktopIcons.Definitions` exposes the built-in semantic inventory.
`DesktopIcons.GetPackStatus(productPaths)` reports the selected physical directory
(or null for the embedded fallback) and any rejected-pack diagnostic. External packs are
data only: UTF-8 INI, at most 128 KiB, all 11 known keys, required semantic metadata,
and geometry bounded within the 20px canvas. Duplicate sections/properties, incomplete
packs, invalid geometry and escaping source paths are rejected. No XAML is executed,
SVG renderer is loaded, network asset is fetched, or plugin authority is granted.

Create controls on the owning WPF dispatcher. Each call returns a fresh unparented
control with a tooltip, automation name/help text, and the live `Cerebral.Theme.TextBrush`
binding. Use 16 or 20 device-independent pixels for ordinary status rows. The source
shape's canvas padding is preserved; smaller sizes remain shapes with readable
state text available. Working is deliberately static and does not imply ongoing progress
by animation.

## Human and AI guidance

Use semantic keys from this table rather than inventing glyph names or hard-coding
Unicode emoji. Distinct monochrome shapes communicate state without relying only on
color. Icons supplement the precise status, diagnostic detail, row text, tooltip or
accessible name. For awaiting, retain whether work is queued or needs human input.
Do not mark an operation successful solely because a checkmark asset exists.

The source SVGs also work in HTML and tools that support SVG; WPF uses the geometry in
the INI index. This inventory is discoverable reference data for a plugin or agent,
not a capability grant or an instruction injected into every model request.

The broader emoji catalogs are intentionally outside this pack. Microsoft's Fluent
emoji catalog is also MIT licensed; OpenMoji declares CC BY-SA 4.0. Remix Icon's current
repository states a custom Remix Icon License v1.0 effective January 2026. This subset
uses Fluent System Icons to keep one consistent small UI style and MIT notice handling.
