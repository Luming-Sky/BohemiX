# Alchemy sprite contract

`AlchemyCanvas` loads PNG sprites from this folder through Avalonia `AssetLoader` and draws
the resulting `IImage` directly into its `DrawingContext`. Missing files intentionally fall
back to the built-in vector silhouettes so gameplay remains testable before final art lands.

Supported names:

- `herb_<ingredient-id>_raw.png`
- `herb_<ingredient-id>_crushed.png`
- `bottle_water.png`, `bottle_wine.png`, `bottle_spirits.png`, `bottle_oil.png`
- `pestle.png`

Recommended source size is 256–512 px with transparent backgrounds. Keep the visible object
tightly cropped and use the same pivot/crop for raw/crushed herb pairs so cross-fades do not
jump during grinding.
