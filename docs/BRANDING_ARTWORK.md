# Cortex branding artwork

The refreshed Cortex artwork uses the suite's deep navy, silver, ice-blue and
cyan palette. Its open C and connected nodes retain the earlier identity, with
the dimensional material and lighting used by the Cerebral artwork.

The full-color source is
[`Cortex.Graphic.png`](../src/Pegasus.AgentOps.Tray/Assets/Cortex.Graphic.png).
The separate transparent GUI source is
[`Cortex.Watermark.png`](../src/Pegasus.AgentOps.Tray/Assets/Cortex.Watermark.png).
The GUI applies a theme-colored low-opacity mask; icons use the full-contrast
artwork. The build version is ordinary interface text, so it stays current when
the reusable artwork is used in a later build.

The original artwork and transparent counterpart were made with the built-in
OpenAI image-generation tool. Source references were the existing first-party
Cerebral graphic and the earlier Cortex mark visible in its About preview.
The prompts below record the design brief without machine-specific paths.

## Main artwork prompt

> Use case: stylized-concept / logo-brand. Create one premium raster product
> emblem for Pegasus Cortex, refreshing its flat vector-looking mark into a
> rich dimensional graphic that belongs beside the existing Pegasus Cerebral
> artwork. Image 1 is a STYLE reference: luminous sculpted ice-white/silver
> forms, electric royal blue and cyan highlights, depth, subtle fine network
> detail, deep navy square background. Do not copy its wing subject. Image 2 is
> a STRUCTURAL reference only: use the Cortex app mark in the top-left of its
> About page, the open circular C and branching connected knowledge nodes;
> ignore all page text/layout. Reimagine that Cortex identity as a polished
> volumetric sculptural open-ring/cortex form with flowing layered surfaces
> around a small constellation of connected glowing spheres on its open right
> side. Preserve a strong recognizable C silhouette and a clear relationship
> between central connection and three major nodes, with tasteful smaller
> details. Visually substantial, material, illuminated and elegant; not flat
> vector art, not a simple beveled letter, not generic clip art. A self-contained
> centered emblem on deep navy (#03152f to #061e46), square composition,
> generous 10 percent safe margins, soft controlled blue halo with excellent
> contrast, crisp shape legible at small app-icon size and beautiful at large
> hero size. Match Cerebral's family palette and polished visual depth while
> making Cortex distinct. No letters, no words, no watermark, no frames, no
> mouse cursor, no corner badge, no mockup sheet, no multiple variants. Output
> exactly a single square finished graphic.

## Transparent GUI artwork prompt

> Edit the supplied Pegasus Cortex graphic into its matching transparent
> GUI-watermark master. Preserve the exact open C silhouette, all sculptural
> flowing silver/ice-blue surfaces, the central hub and three main connected
> glowing spheres, orientation, position and proportions. Remove only the dark
> navy background completely, including the open area inside the C and all
> space between the node branches, leaving true alpha transparency; keep a
> restrained edge glow without a rectangular navy matte. No new objects, no
> text, no badge, no logo redesign, no frame. This is a normal-contrast
> transparent artwork master; the application will apply subtle watermark
> opacity when painting its background. Keep the full emblem centered with
> existing safe margins in a square canvas. Deliver one transparent PNG.

## Build the icon family and kit

The maintained [asset guide](../src/Pegasus.AgentOps.Tray/Assets/README.md)
describes the role badges, consumers, nine ICO frame sizes and raster-backed
SVGs. The generator resizes the fixed artwork; it does not call an image model.

From a Windows source checkout:

```powershell
& .\src\Pegasus.AgentOps.Tray\Assets\New-CortexAppMark.ps1
& .\tools\New-CortexBrandingKit.ps1
```

The exported kit is `artifacts/branding/CortexBrandingKit.zip`, accompanied by a
checksum and relative-file inventory. Use the exporter's explicit `-Replace`
option when deliberately rebuilding existing kit outputs. Runtime installation
and program signing follow the [normal product build guide](BUILD_FROM_SOURCE.md).
