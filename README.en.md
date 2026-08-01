<div align="center">
  <img src="src/BohemiX.App/Assets/bohemix-wordmark.png" alt="BohemiX" width="420">

  <p><strong>A local-first Windows desktop companion for Kingdom Come: Deliverance II</strong></p>
  <p>Launch the game, manage mods, protect saves, track progress, and explore alchemy and forging from one reliable workspace.</p>

  [![BohemiX CI](https://github.com/Luming-Sky/BohemiX/actions/workflows/ci.yml/badge.svg)](https://github.com/Luming-Sky/BohemiX/actions/workflows/ci.yml)
  ![Version](https://img.shields.io/badge/version-0.9.1%20Beta-2f6fed)
  ![Platform](https://img.shields.io/badge/platform-Windows%20x64-087cd5)
  [![License](https://img.shields.io/badge/license-MIT-2f855a)](LICENSE)
</div>

> [!IMPORTANT]
> BohemiX 0.9.1 is a Beta release. Its core workflows are covered by automated tests and release validation, but it does not yet carry the stability guarantees of a 1.0 release. Keep an independent backup of important saves and install mods only from trusted sources.

## Product Positioning

BohemiX is designed for players who want reliable control over their local Kingdom Come: Deliverance II environment. It is not a memory-editing cheat tool or a mod pack that permanently overwrites game files. Instead, it is a desktop platform built around the following principles:

- **Local-first:** Settings, player profiles, mod states, save snapshots, and Tracker projections are stored locally by default.
- **Isolated and controlled:** Enabled mods are mounted through a real usvfs virtual file system, reducing the need to overwrite original game files.
- **Recovery-first:** Save switching, backup, and restoration use validation, staging, rollback, and recovery checkpoints.
- **Diagnosable:** Startup checks, dependency validation, structured logs, and error-location reporting make failures easier to identify.
- **Respectful of the game:** Tracker reads only local events written by its in-game mod. It does not read process memory, inject DLLs, or hook the game process.

## Core Features

| Module | Capabilities |
| --- | --- |
| Game launcher | Scans Steam, Epic Games, and local folders for `KingdomCome.exe`, stores verified installation paths, and tracks launch sessions, running state, and abnormal exits. |
| Mod management | Scans local mods, checks health, detects file conflicts, plans load order, performs startup checks, and mounts enabled content through usvfs. |
| Mod acquisition and installation | Supports local archives, Nexus Mods, Nexus Collections, and Steam Workshop workflows, with download queues, collection catalogues, prerequisite prompts, and rollback on failed installation. |
| Save system | Manages player save profiles and slots, monitors `.whs` changes, creates individual protection points or complete snapshots, and supports import, export, retention policies, and pre-restore validation. |
| Tracker and achievements | Installs and diagnoses the read-only Tracker mod, incrementally reads local JSONL events, and projects quest, entity, coordinate, session, and achievement progress while preserving anti-spoiler behavior. |
| Player profiles | Manages multiple local player profiles with Steam account associations, avatars, installation paths, and separate data directories. |
| Lab | Provides alchemy and forging simulations driven by deterministic state machines, recipe data, physical feedback, and quality evaluation. |
| Settings and diagnostics | Supports Simplified Chinese and English, appearance and launch preferences, log navigation, global error reports, update checks, and dependency health information. |

### VFS Mod Management

- Analyses file conflicts using normalized virtual paths.
- Generates a deterministic mount plan from enabled states and load order.
- Validates mods, native dependencies, and usvfs health before launching the game.
- Creates and releases the VFS within a launch session, including cleanup after abnormal exits.
- Extracts and validates mods in a staging directory before committing them to the library.
- Rolls back failed installations so incomplete files do not remain in the mod library.

### Save Protection

- Does not rename or rewrite the official save entry directory or core save files.
- Keeps writable configuration in normal directories and routes slots through NTFS Directory Junctions.
- Uses content-addressed storage, SHA-256 verification, chunk-level deduplication, and atomic writes for snapshots.
- Reconstructs and validates a snapshot in a staging directory before restoration.
- Creates a recovery checkpoint before replacing existing files.
- Blocks unsafe slot switching while the game is running or save files are still being written.

### Tracker Compliance Boundary

The in-game Tracker Lua mod appends events to a local file. BohemiX reads new events incrementally and projects them into useful progress data. Continuous coordinate events are throttled, unconfirmed sessions are marked separately, and undiscovered quest entities remain hidden by default.

Tracker does not read game memory, modify save files, or require a remote service.

### Lab Craft Simulations

- **Alchemy:** Bases, ingredients, grinding, heating, timing, distillation, and bottling are handled as ordered actions. Results are evaluated from action order, boiling cycles, and ingredient state.
- **Forging:** Covers material selection, heating, hammering, turning, quenching, grinding, inspection, and final evaluation with fixed-step simulation and OpenGL feedback.

## Installation and Use

### Using a Release Package

1. Download the Windows x64 ZIP and SHA-256 checksum from [GitHub Releases](https://github.com/Luming-Sky/BohemiX/releases).
2. Verify the archive and extract it completely to a writable directory. Do not run the application from inside the ZIP file.
3. Run `BohemiX.App.exe`.
4. Let BohemiX scan for the game automatically, or select `KingdomCome.exe` manually.

The release is a self-contained .NET 8 build and normally does not require a separate .NET Runtime installation. Nexus browser authentication requires Microsoft Edge WebView2 Runtime, which is included with most current Windows 10 and Windows 11 installations.

### System Requirements

- Windows 10 or Windows 11, x64
- A legitimate local installation of Kingdom Come: Deliverance II
- Enough disk space for mods, save snapshots, and download caches
- Microsoft Edge WebView2 Runtime for Nexus browser authentication

## Privacy and Security

- BohemiX works offline by default. Network access is limited to explicit online features such as downloads, account authorization, and update checks.
- Nexus authentication takes place in an isolated WebView2 session. Passwords are submitted only to Nexus Mods, and cookies are attached only to verified `nexusmods.com` hosts.
- Personal API keys are protected with Windows Data Protection.
- Logs exclude sensitive values such as Nexus cookies, API keys, and server tokens.
- Remote mod catalogues must pass ECDSA P-256/SHA-256 signature verification. Failed verification falls back to the most recent trusted cache or the built-in catalogue.
- Native usvfs dependencies use pinned versions and SHA-256 validation. Release packaging fails if a dependency is missing, has the wrong architecture, or does not match the expected hash.

Further documentation:

- [Nexus account binding](docs/NexusAccountBinding.md)
- [Nexus cookie security audit](docs/NexusCookieAuthSecurityAudit.md)
- [Save snapshot architecture](docs/SnapshotStorageArchitecture.md)
- [Release checklist](docs/ReleaseChecklist.md)

## Building from Source

Git, PowerShell, and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) are required.

```powershell
git clone https://github.com/Luming-Sky/BohemiX.git
cd BohemiX
dotnet restore BohemiX.sln
dotnet test BohemiX.sln -c Release --nologo
dotnet run --project src/BohemiX.App/BohemiX.App.csproj -c Release
```

To generate a tested and dependency-validated portable Windows x64 package:

```powershell
.\tools\publish-win-x64.ps1 -Version "0.9.1"
```

The script runs the complete Release test suite, publishes a self-contained application, removes unrelated runtime files, validates native dependencies, copies licenses, and generates a ZIP archive and `SHA256SUMS.txt`.

## Architecture

```text
BohemiX.App                         Avalonia desktop shell, views, and composition
  |-- BohemiX.Modules.Alchemy      Alchemy module
  |-- BohemiX.Modules.Forge        Forging module
  |-- BohemiX.Modules.SaveManager  Save management module
  |-- BohemiX.Infrastructure       SQLite, file system, process, network, and usvfs implementations
  `-- BohemiX.Core                 Domain models, service contracts, and business rules
```

BohemiX uses .NET 8, Avalonia 11, CommunityToolkit.Mvvm, SQLite/Dapper, Serilog, SkiaSharp, Silk.NET/OpenGL, WebView2, LibVLCSharp, and usvfs. See the [architecture documentation](docs/Architecture.md) for dependency rules and runtime lifecycle details.

Main directories:

```text
src/          Application, core, infrastructure, and feature modules
tests/        Core, App, Alchemy, and Forge automated tests
tools/        Publishing, asset generation, and catalogue validation tools
workers/      Echo Cave Cloudflare Worker
third-party/  Pinned native dependencies and complete license files
docs/         Product, architecture, security, and release documentation
```

## Current Status

- Version: `0.9.1 Beta`
- Target platform: Windows x64
- Local Release validation: 807 tests passing
- Release notes: [CHANGELOG.md](CHANGELOG.md)

Report bugs and suggest features through [GitHub Issues](https://github.com/Luming-Sky/BohemiX/issues).

## License and Disclaimer

BohemiX source code is distributed under the [MIT License](LICENSE). usvfs and other third-party components remain subject to their respective licenses. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the release package's `native` directory for details.

BohemiX is an independent community project. It is not affiliated with or endorsed by Warhorse Studios, Deep Silver, Nexus Mods, Valve, or Epic Games. Kingdom Come: Deliverance and all related names, images, and trademarks belong to their respective owners.
