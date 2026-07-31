# BohemiX Development Goals

## 1. Product Direction

BohemiX is a local-first desktop companion for Kingdom Come: Deliverance II players. The product combines a game launcher, a professional VFS-based mod manager, and an achievement and progression visualization system.

The long-term goal is to make BohemiX feel like a precise desktop instrument: fast to open, calm to operate, visually refined, and trustworthy when touching game files, save files, and mod archives.

## 2. Non-Negotiable Engineering Principles

- Keep the application offline-capable by default. Cloud sync must be optional and lightweight.
- Never overwrite physical game files for mod activation. All mod mounting must go through an isolated VFS integration layer.
- Keep UI work on the UI thread small. File scans, save parsing, conflict analysis, and process monitoring must run asynchronously.
- Keep all game, save, and mod IO defensive. Missing, locked, corrupt, or partially written files are expected states.
- Depend on interfaces at service boundaries and register implementations through dependency injection.
- Use CommunityToolkit.Mvvm source generators for ViewModel state and commands.
- Log meaningful operational events through Serilog with structured properties.
- Preserve NativeAOT compatibility when choosing patterns, avoiding broad dynamic reflection.

## 3. Target Architecture

### 3.1 Solution Layout

- `src/BohemiX.App`: Avalonia UI shell, Views, ViewModels, app composition, and desktop entry point.
- `src/BohemiX.Core`: domain models, service contracts, messages, validation rules, and pure business logic.
- `src/BohemiX.Infrastructure`: file system, SQLite, process, registry, Steam/Epic discovery, VFS interop, and logging implementations.
- `tests/BohemiX.Core.Tests`: unit tests for deterministic domain behavior, especially conflict analysis and rules evaluation.
- `docs`: product goals, architecture notes, and implementation records.

### 3.2 Layer Rules

- App may reference Core and Infrastructure.
- Infrastructure may reference Core.
- Core must not reference App or Infrastructure.
- Views must stay declarative and small. ViewModels coordinate state and commands but delegate IO and system interaction to services.
- Cross-service and cross-ViewModel notifications must use `IMessenger` messages from CommunityToolkit.Mvvm.

## 4. Development Phases

### Phase 0: Foundation

Deliver a compiling Avalonia solution with MVVM, DI, logging, and first-pass navigation.

Required results:

- Main window with shell layout.
- Dashboard, Mods, Achievements, and Launcher navigation targets.
- Core interfaces for mod management, game discovery, VFS session control, save monitoring, and achievement evaluation.
- Infrastructure placeholder implementations that are safe, logged, and easy to replace.
- Build verification through `dotnet build`.

### Phase 1: Launcher and Game Discovery

Implement automatic game location discovery and a reliable launcher workflow.

Required results:

- Steam library VDF scanning.
- Epic manifest scanning.
- Manual game path selection fallback.
- Process start and PID tracking.
- Game exit detection.
- Final cleanup hook that can request VFS unmount and save snapshot sync.

### Phase 2: VFS Mod Manager

Build the professional mod management core without touching physical game files.

Required results:

- Mod manifest model and local mod registry.
- File path hashing for conflict detection.
- Conflict matrix grouped by normalized virtual path.
- Load order model with deterministic drag reorder behavior.
- P/Invoke boundary for `usvfs.dll`, hidden behind an `IVfsSessionService`.
- UI conflict review and load order editing.

### Phase 3: Save Monitoring and Achievements

Implement the companion progression system.

Required results:

- `FileSystemWatcher` pipeline for `.whs` save changes.
- Debounced incremental parsing queue based on `System.Threading.Channels`.
- Local achievement rule DSL stored as JSON first, with YAML considered only after approval.
- Deterministic rule evaluation in Core.
- Achievement unlock messages through `IMessenger`.

### Phase 4: Data Visualization

Create the polished player progression experience.

Required results:

- Ability radar chart.
- Progression curves over time.
- Session summary timeline.
- Smooth rendering with approved visualization tooling or custom SkiaSharp drawing.
- Accessible color and contrast choices.

### Phase 5: Persistence and Sync Readiness

Persist user state and prepare for optional cloud sync.

Required results:

- SQLite schema for settings, discovered games, mods, load order, achievement state, and save snapshots.
- Dapper repositories for performance-critical queries.
- Exportable local backup format.
- Sync-safe record IDs and timestamps.

## 5. Immediate Implementation Targets

The first executable increment should focus on a stable foundation:

1. Create the solution and project structure.
2. Generate an Avalonia MVVM application targeting .NET 8.
3. Add Core service contracts and domain models for the three major modules.
4. Add Infrastructure placeholder services with defensive logging.
5. Wire dependency injection in the App layer.
6. Render a working shell that exposes Dashboard, Mods, Achievements, and Launcher sections.
7. Verify with `dotnet build`.

## 6. Definition of Done

A change is considered complete only when:

- It compiles locally.
- Service contracts have XML documentation.
- IO-facing implementations log failures and do not swallow exceptions silently.
- ViewModels use CommunityToolkit.Mvvm source generators.
- New behavior is isolated to the intended layer.
- Any introduced dependency is part of the approved stack or explicitly approved before use.

