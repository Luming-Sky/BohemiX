# BohemiX Architecture

## Dependency Direction

BohemiX uses a layered core with independently registered feature modules:

```text
BohemiX.App
  -> BohemiX.Infrastructure
  -> BohemiX.Modules.Alchemy
  -> BohemiX.Modules.Forge
  -> BohemiX.Modules.SaveManager
  -> BohemiX.Core

BohemiX.Infrastructure -> BohemiX.Core
Feature modules         -> BohemiX.Core
BohemiX.Core            -> no BohemiX project
```

Lower layers must not reference `BohemiX.App` or sibling feature modules. Cross-feature contracts belong in `BohemiX.Core`; platform and persistence implementations belong in `BohemiX.Infrastructure`.

## Composition Root

`ApplicationServiceRegistration.AddBohemiXApplication` is the only application-level dependency registration entry. `BohemiXApplicationHost` owns logging and creates a validated service provider. `App.axaml.cs` owns only Avalonia lifetime and window presentation.

Long-running runtime coordinators are singletons. A coordinator owns its watcher, serializes refreshes, publishes immutable snapshots, and provides an awaited stop operation. Views and view models consume coordinators; they do not create competing watcher loops.

`GameRuntimeMonitorService` owns post-launch process monitoring, exit-frame capture, abnormal-exit reporting and VFS cleanup. `MainWindowViewModel` receives a typed exit update and only maps it to bound status plus the save-manager exit hook.

## Desktop Lifetime

- `SingleInstanceCoordinator` owns the named mutex and best-effort activation of the existing process.
- `WindowsWindowInterop` is the only location for startup-window Win32 calls.
- `ApplicationExceptionCoordinator` owns global exception subscriptions and delegates presentation to `IApplicationErrorReporter`.
- Disposable singleton services are released by the root service provider during application exit.

## View Models

View models may format state for bindings and coordinate user commands. File access, database access, process monitoring, network requests, installation transactions, and validation belong in services behind `BohemiX.Core` interfaces.

`MainWindowViewModel` is a compatibility shell and remains the largest migration target. New feature state must be added to a feature view model or application service first. Existing areas should be extracted one domain at a time while preserving the current binding surface through delegation; avoid dependency-bag objects that merely hide large constructor parameter lists.

Cross-cutting presentation concerns are also extracted behind focused services. `IMainWindowTextCatalog` owns the main window's language normalization, display-name formatting, and keyed English/Chinese text mapping. The view model keeps its existing `T(key)` binding surface, but no longer owns the localization table.

The compatibility shell is split by responsibility as well:

- `MainWindowViewModel.Navigation.cs` contains top-level navigation, mod-manager page routing, and workspace visibility coordination.
- `MainWindowViewModel.Tracker.cs` contains Tracker package installation, diagnostics, monitoring subscription, and snapshot-to-binding projection.
- `MainWindowViewModel.GameRuntime.cs` contains game discovery, launch preflight, process-session handoff, and game-exit mapping.
- `MainWindowViewModel.Settings.cs` contains settings snapshots, persistence, reset, category selection, and apply commands.
- `MainWindowViewModel.ModAccounts.cs` contains Nexus and Steam account-binding dialogs and source approval flows.
- `MainWindowViewModel.ModRecommendations.cs` contains daily recommendations, candidate loading, scoring, filtering, and localization.
- `MainWindowViewModel.ModDownloads.cs` contains Workshop installation, download queue commands, queue installation, and local package installation.

These partial files keep the existing generated command and binding names intact while preventing feature changes from expanding the already large main file. Long-running work still belongs to the injected runtime services; partial files only own presentation coordination.

## Verification

`ApplicationArchitectureTests` enforces lower-layer dependency direction and validates the complete dependency injection graph. Feature-specific services require focused unit tests for failure retention, cancellation, restart, and concurrent calls where applicable.
