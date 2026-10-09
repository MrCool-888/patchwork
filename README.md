# Patchwork 0.4.2

An installable Windows patch manager with separate patch-file imports, previews, verified backups, restore, and a GitHub release updater. **The app and installer include no patch packs.**

- Patcher source and releases: https://github.com/MrCool-888/patchwork
- Separate patch files: https://github.com/MrCool-888/patchwork-patches

## Install and use

Download `Patchwork-Setup.exe` from the patcher releases. Setup installs for the current Windows user to `%LOCALAPPDATA%\Programs\Patchwork`, adds Start menu shortcuts and an Installed Apps entry, and offers a desktop shortcut. Setup itself needs no administrator rights. Windows 10/11 x64 and .NET Framework 4.8 are required. This is an unsigned development build.

1. Open Patchwork. A new installation starts with an empty library.
2. Download a separate patch file from its repository or another source you trust. Choose **Add patch file** and select a `.json` or `.patchwork` file. Reimporting the same bundle ID updates its definition.
3. Choose the matching original app folder. Compatibility requires exact SHA-256 fingerprints, not just a matching version number.
4. Select patches and choose **Preview changes**. Review original and modified content or assembly methods. Importing and previewing do not change target files.
5. Close the target application and choose **Apply**. Protected folders use a Windows administrator prompt for the apply helper.
6. Use **History & restore** to restore verified originals. Restore before changing a patch selection or reapplying.

## App updates

In **About this build → App updates**, choose **Check for updates**. Patchwork also checks once when it opens; turn off that behavior using the checkbox. Checks use the public GitHub latest-release API for `MrCool-888/patchwork`, require no GitHub login, and exclude drafts/prereleases. Offline or rate-limit failures leave the app usable. Patching itself stays local.

When a newer version is available, **Download and install** fetches the release's `Patchwork-Setup.exe`, verifies its exact size and GitHub SHA-256 digest, opens setup, and closes Patchwork. Complete the installer to update. Setup releases the inherited working folder and waits for the old app process to exit before replacing files. A custom recognized installation is updated in place. If the app remains open for 30 seconds, setup asks you to close it and retry; it never terminates the process. This is a checksum check against GitHub metadata, not a publisher code signature. No installer runs automatically. Imported patch files and history remain in your separate data folder. App updates do not fetch or update patch packs.

Version 0.4.1 fixed the Windows “process is being used” update error. Current setup also handles the working-folder lock when launched by older Patchwork versions. If an older setup is still open after an error, close it and run the latest `Patchwork-Setup.exe` from the release page.

## Backups and recovery

Backups, history, imported files, update downloads, and preferences live in `%LOCALAPPDATA%\Patchwork\Data`. Updating/uninstalling Patchwork preserves this data and does not undo target patches. Restore targets before uninstalling if you want their original state.

Apply rechecks fingerprints, saves durable originals/journals, replaces files atomically one at a time, and verifies results. Failures trigger rollback; interrupted sessions can recover from History. Restore refuses outside changes. Network shares, symlinks/junctions, mixed native/managed assemblies, and target files over 8 MB are unsupported. New target-app releases need matching new patch files.

## Patch files

The library and patch cards show the imported pack version and individual patch versions, separately from the target app version. Preview includes the pack version. History records the versions actually applied and the imported file's SHA-256, so importing a newer definition never relabels an older session. Legacy files and sessions without version metadata show **Unversioned**. Version labels are author declarations; exact original fingerprints still decide compatibility.

See [PATCH-FORMAT.md](PATCH-FORMAT.md). Files use declarative operations and contain no scripts, commands, downloads, or executable plugins. Review the author and method preview: matching fingerprints do not establish author trust. App behavior and server entitlements remain subject to the target app's implementation.

Version 0.4.2 adds conditional boolean setter overrides for patch packs that need to change selected rows while preserving the other rows' restrictions. Proton pack 1.2.0 requires this operation. Patch packs remain separate downloads.

## Build and test

No SDK or NuGet download is required; use the Windows .NET Framework 4.x compiler. Mono.Cecil and its license are included in `source/lib`; see [THIRD-PARTY.md](THIRD-PARTY.md).

```powershell
& .\source\build.ps1 -OutputDirectory .\build
& .\source\build.ps1 -OutputDirectory .\build-tests -Console -Tests
& .\build-tests\Patchwork.exe --self-test --data-dir .\test-data
& .\source\installer\build-installer.ps1 -WorkDirectory .\build-setup -OutputFile .\Patchwork-Setup.exe
```

Self-test fixtures compile only with `-Tests` and are excluded from the installer. Production builds contain no demo recipes or Proton patch definitions. The installer contains only the app, Mono.Cecil, and guides. `--data-dir PATH` isolates testing. `--import-patch FILE` imports the same external file as the GUI and then opens the app.

See [VALIDATION.md](VALIDATION.md) for evidence and limits. Patchwork is independent of Proton and Morphe.
