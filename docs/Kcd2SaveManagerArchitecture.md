# KCD2 Save Manager Code Frame

```text
BohemiX/
├─ src/
│  ├─ BohemiX.App/                         # Avalonia MVVM shell; WPF/MVVM projects can mirror this UI layer.
│  │  ├─ ViewModels/
│  │  └─ Views/
│  ├─ BohemiX.Core/
│  │  ├─ Models/Saves/
│  │  │  ├─ JunctionInfo.cs
│  │  │  ├─ KcdSaveMetadata.cs
│  │  │  ├─ KcdWorldState.cs
│  │  │  └─ SaveSlot.cs
│  │  └─ Services/Saves/
│  │     ├─ IJunctionRouter.cs
│  │     ├─ IKcdSaveParser.cs
│  │     ├─ IProcessCoordinator.cs
│  │     └─ ISlotManager.cs
│  └─ BohemiX.Infrastructure/
│     ├─ Native/
│     │  └─ NativeMethods.cs
│     └─ Services/Saves/
│        ├─ JunctionRouter.cs
│        ├─ ProcessCoordinator.cs
│        ├─ SaveParser.cs
│        └─ SlotManager.cs
└─ tests/
   └─ BohemiX.Core.Tests/
```

## Hard Rules

- Official save entry folders are never renamed.
- `metadata.xml`, `.pak`, `.whs`, and other core save payload files are never renamed or rewritten.
- Slot physical folders use `Slot_{yyyyMMdd_HHmmss}_{8位Guid}` and display names live only in SQLite.
- Slot switching must be blocked while `KingdomCome2.exe` is running or save files are being written.
- Official-to-Vault routing is implemented with NTFS Directory Junctions through `DeviceIoControl` and `FSCTL_SET_REPARSE_POINT`; symbolic links and copy-based switching are forbidden.
