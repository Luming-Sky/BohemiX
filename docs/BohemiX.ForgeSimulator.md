# BohemiX Forge Simulator

Forge is a Lab module, not a separate game. The Avalonia view owns input and presentation;
`ForgeEngine` owns the deterministic craft process.

## Runtime boundaries

- `Engine/`: state machine, heat, shape lattice, quench, grinding and quality evaluation.
- `Data/forge-catalog.json`: versioned recipe/material/tool definitions embedded in the module.
- `Controls/ForgeOpenGlControl`: OpenGL scene, procedural station meshes and pointer gestures.
- `ViewModels/ForgeWorkshopViewModel`: MVVM commands, pause lifecycle, audio cues and history.
- `Services/ForgeServices.cs`: no-device-safe procedural audio and atomic local profile storage.

The state machine is explicit: `RecipeSelect`, `MaterialSelect`, `Heating`, `Hammering`,
`RotateWorkpiece`, `Reheat`, `Quenching`, `Grinding`, `Inspection`, and `Result`. The
engine uses a fixed 60 Hz step and the renderer never changes craft state directly.

## Input

- Select a recipe and material in the parchment panels.
- During heating, drag vertically over the station or use `鼓风`.
- On the anvil, pull the pointer upward and release to strike; the release distance sets force.
- `Q` flips the workpiece and `E` switches between flat and cross-peen faces.
- Drag in the quench and grinding stages to control depth, speed and coverage.
- `Tab` opens the recipe strip and `Esc` pauses the run.

Temperature is intentionally not shown numerically. The workpiece glow, fire, smoke,
sound and the deformation response are the feedback channel.

## Adding content

Add a recipe or material to `Data/forge-catalog.json`, keeping schema version `1`. A recipe
must define every material's recommended quench medium and at least one ordered zone. The
existing lattice renderer supports sword profiles and axe eye voids without new engine code.
New operation handlers can implement `IForgeOperationHandler` when a future craft needs a
different interaction model.

The local profile is stored at `%LocalAppData%/BohemiX/forge/profile-v1.json`; it contains
best scores and the latest 20 results, with no unlock gating.
