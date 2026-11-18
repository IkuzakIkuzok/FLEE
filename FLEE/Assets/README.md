# FLEE application icon

FLEE means **Fluorescence Lifetime Exchange Engine**. The cyan excitation pulse
and bright photon lead into a mint fluorescence decay curve. Its arrow-shaped
tail represents exchanging measurement data. A dark blue tile keeps the luminous
signal legible on light and dark Windows surfaces; the outer corners are transparent.

## Assets and integration

- `Flee.png`: original generated artwork with its alpha channel preserved.
- `Flee.ico`: Windows icon containing 16, 20, 24, 32, 40, 48, 64, 128, and 256 px images.
- `build_icon.py`: packages the PNG into ICO sizes using Pillow; no artwork changes.

`FLEE.csproj` embeds the ICO as the executable's application icon and as a managed
resource for the main window. The app header uses the 256 px image from that icon.
No external image files are required when the app runs.

To rebuild the ICO after replacing the source artwork:

```powershell
python FLEE/Assets/build_icon.py
dotnet build FLEE/FLEE.csproj
```

Pillow is needed only to rebuild the artwork package, not to build or run FLEE.

## Generation record

Created with the **built-in imagegen tool**, with `transparent_background: true`.
No fallback CLI or external image-generation API was used. The original generated
PNG was copied into this project and mechanically packaged at Windows icon sizes.

Prompt:

> Use case: logo-brand. Asset type: finished Windows desktop application icon for FLEE, Fluorescence Lifetime Exchange Engine, a scientific app that converts fluorescence decay measurements. Create ONE polished square icon, straight-on orthographic view, on a genuinely transparent canvas. An opaque deep midnight-blue rounded-square tile occupies about 92% of the canvas, with a crisp silhouette and very subtle blue gradient. Inside it, one bold luminous cyan-to-mint fluorescent decay signal: a short rising excitation pulse on the left, a bright compact photon at its peak, then a smooth exponential decay sweeping down toward the right. Integrate a small elegant arrowhead at the end of the decay to suggest exchange of measurement data. The bright curve is the dominant unmistakable shape; use generous negative space and thick clean strokes readable at 16px and 32px. Soft restrained glow close to the line, no fuzzy outer glow. Sophisticated scientific software identity, minimal, precise, calm and premium. Keep the symbol comfortably inset within the tile. No lettering or acronym or numbers, no chart axes, no grid, no extra particles, no separate orbit rings, no gears, no frames, no mockup, no perspective, no cast shadow outside the tile, no watermark. All space outside the rounded tile must have real alpha transparency. The final is production artwork, not a presentation sheet.
