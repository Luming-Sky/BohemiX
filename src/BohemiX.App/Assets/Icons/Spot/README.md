# BohemiX Spot Icons

This folder contains 32 transparent 256x256 PNG icons for feature cards, empty states, onboarding, and module entry points. They are not intended for dense 12-24 px toolbar controls; use the `BmxIcon*` vector resources there.

The defining palette uses the same cool white and neutral grey foregrounds as the application (`#F8FAFC`, `#E5E7EB`, `#CBD5E1`, `#A6ADBB`, `#94A3B8`, `#64748B`) so icons remain distinct from pale-blue and deep-blue surfaces. Amber, green, and coral are reserved for small semantic accents.

Avalonia usage:

```xml
<Image Source="/Assets/Icons/Spot/png/alchemy-lab.png"
       Width="72"
       Height="72"
       Stretch="Uniform" />
```

`manifest.json` records the row-major sheet order, final filenames, slicing mode, and prompt locations. The prompts are frozen so future batches can preserve the same palette and construction system.
