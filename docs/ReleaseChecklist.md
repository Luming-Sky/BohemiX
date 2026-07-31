# BohemiX Release Checklist

## Required before publishing

- [ ] Select a release version and update the changelog.
- [ ] Confirm the project license and add the approved root `LICENSE` file.
- [ ] Review `THIRD-PARTY-NOTICES.md` and include the required license texts.
- [ ] Obtain a legally redistributable, version-pinned usvfs bundle.
- [ ] Verify the usvfs SHA-256 from the trusted upstream release.
- [ ] Run `tools/publish-win-x64.ps1` with the native bundle path and SHA-256.
- [ ] Test the generated portable ZIP on a clean Windows 10/11 machine.
- [ ] Test with no enabled Mods and with at least one enabled Mod.
- [ ] Verify that a missing or corrupt VFS dependency blocks Mod launch and shows an error dialog.
- [ ] Verify save backup, restore validation, Tracker bridge monitoring, and error-log navigation.
- [ ] Check 100%, 125%, and 150% display scaling at the supported window sizes.

## Build command

```powershell
.\tools\publish-win-x64.ps1 `
  -NativeBundlePath 'C:\approved\usvfs\win-x64' `
  -UsvfsSha256 '<64-character SHA-256>'
```

The script fails closed when the native bundle or its expected hash is missing or does not match. Do not download or commit native binaries from an unverified source.
