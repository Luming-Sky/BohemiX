# BohemiX Icon Library

This directory contains 144 globally registered, 24x24 rounded glyphs for Avalonia.

Use any icon through its `BmxIcon` resource key:

```xml
<PathIcon Data="{StaticResource BmxIconDownload}"
          Width="16"
          Height="16"
          Foreground="#F4F8FF" />
```

Files:

- `BohemiXIcons.axaml`: application-ready `StreamGeometry` resources.
- `catalog.html`: visual browser for every icon.
- `manifest.json`: searchable category/name/source mapping.
- `style-spec.json`: frozen oil-icon style specification for future batches.
- `LICENSE-MATERIAL-ICONS.txt`: upstream Apache 2.0 license.
- `Spot/`: 32 transparent 256x256 PNG spot icons, prompts, and their slicing manifest.
- `../../../../tools/Recolor-BohemiXSpotIcons.py`: deterministic blue-to-neutral palette migration tool.

The UI chooses icon color semantically. Default glyphs use the foreground color; blue is interactive, amber is emphasis, green is success, and red is destructive/error. Keep functional glyphs at 12-24 px. Use generated transparent PNG spot icons only at larger sizes.
