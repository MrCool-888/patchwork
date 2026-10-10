# Patchwork 0.7.0 prerelease

This experimental prerelease supports the separate Proton guest patch. A real installed-client guest connection remains unverified. The current stable patcher remains 0.6.1.

An installable Windows patch manager with separate patch-file imports, automatically updated GitHub patch sources, previews, verified backups, direct patch upgrades, restore, and a GitHub release updater. **The app and installer include no patch packs.**

- Patcher source and releases: https://github.com/MrCool-888/patchwork
- Separate patch files: https://github.com/MrCool-888/patchwork-patches

## Install and use

Download `Patchwork-Setup.exe` from the patcher releases. Setup installs for the current Windows user to `%LOCALAPPDATA%\Programs\Patchwork`, adds Start menu shortcuts and an Installed Apps entry, and offers a desktop shortcut. Setup itself needs no administrator rights. Windows 10/11 x64 and .NET Framework 4.8 are required. This is an unsigned development build.

1. Open Patchwork. A new installation starts with an empty library.
2. Choose **Patch sources**, paste `https://github.com/MrCool-888/patchwork-patches`, and choose **Add source**. Leave **Include pre-release patch packs** enabled for the experimental Proton packs. Alternatively, choose **Add patch file** to import a separate `.json` or `.patchwork` file.
3. Choose the matching app folder. Compatibility requires exact original SHA-256 fingerprints, not just a matching version number.
4. Select patches, set any color options, and choose **Preview changes**. Review the before/after content or assembly methods. Importing and previewing do not change target files.
5. Close the target application and choose **Apply patches**. Protected folders use a Windows administrator prompt for the apply helper.
6. When a newer pack is imported, preview and choose **Update patches**. An already-patched app with verified Patchwork history updates directly; no manual restore is needed. **History & restore** still restores the original files when you want to remove the patches.

## GitHub patch sources

Paste a public GitHub repository link or a link to one of its releases. Adding a source checks it immediately. Automatic sources check when Patchwork opens if due and about once an hour while it is open. **Check now** and **Check all sources** work on demand; each source has an automatic-update switch and a pre-release option. No GitHub sign-in is needed.

Sources download published `.json` or `.patchwork` release assets, validate their size, SHA-256 and supported patch operations, then update saved definitions by bundle ID and pack version. Unchanged assets are cached. A lower version never replaces a newer saved pack, and changed contents with the same version are refused. Offline, invalid or rate-limited checks keep existing packs usable. The UI shows the source, downloaded pack versions, status and last check.

The check uses the latest 100 releases, choosing the newest release asset for each filename, with at most 20 distinct patch assets and 10 sources. Assets are limited to 1 MB and require `packVersion` and GitHub's SHA-256 metadata. ZIPs and repository source files are not imported. Use a stable asset filename across revisions.

Automatic checks update the library. Review a preview and choose **Update patches** to change the target app. Removing a source keeps downloaded packs and applied history. To replace a source-managed definition manually with different contents, remove that source first.

## Updating applied patches

Patchwork builds each new selection from the previous session's verified original backups, compares it with the currently applied files, and replaces the selected edits in one recoverable transaction. Patches no longer selected are removed as part of that update. Original fingerprints remain mandatory. History records the new applied version and marks the previous session **Superseded**; restore the current session to get the exact original files back.

If an update fails or is interrupted before it commits, recovery restores the previous patch version. Outside edits, damaged backups, unfinished transactions and stale previews block updates. Keep the Patchwork data folder. Sessions from older versions can update using their existing originals; if they do not record patch IDs, select the desired patches again. Folders with multiple legacy applied sessions require those sessions to be restored first. Files patched without Patchwork history need valid originals.

This updates patches for the supported target version. Installing a new Proton version still needs a pack matching that version; changing a label does not make an incompatible app supported.

## Patcher app updates

In **About this build → App updates**, choose **Check for updates**. Patchwork also checks once when it opens; turn off that behavior using the checkbox. Checks use the public GitHub latest-release API for `MrCool-888/patchwork`, require no GitHub login, and exclude drafts. Stable is the default channel. Enable **Include prerelease app updates (experimental)** to check published prereleases too. The option persists and keeps the same digest/size checks. Patchwork 0.6.1 and earlier need this first prerelease installer downloaded once; they cannot discover prerelease app updates. Offline or rate-limit failures leave the app usable.

When a newer version is available, **Download and install** fetches the release's `Patchwork-Setup.exe`, verifies its exact size and GitHub SHA-256 digest, opens setup, and closes Patchwork. Complete the installer to update. Setup releases the inherited working folder and waits for the old app process to exit before replacing files. A custom recognized installation is updated in place. If the app remains open for 30 seconds, setup asks you to close it and retry; it never terminates the process. This is a checksum check against GitHub metadata, not a publisher code signature. No installer runs automatically. Imported patch files, sources and history remain in your separate data folder. Patch sources update independently from patcher releases.

Version 0.4.1 fixed the Windows “process is being used” update error. Current setup also handles the working-folder lock when launched by older Patchwork versions. If an older setup is still open after an error, close it and run the latest `Patchwork-Setup.exe` from the release page. Version 0.5.0 uses Windows native HTTPS with the default Windows proxy and normal certificate validation for source and app downloads.

## Backups and recovery

Backups, history, imported files, sources, update downloads, and preferences live in `%LOCALAPPDATA%\Patchwork\Data`. Updating/uninstalling Patchwork preserves this data and does not undo target patches. Restore targets before uninstalling if you want their original state.

Apply rechecks fingerprints, saves durable originals/journals, replaces files atomically one at a time, and verifies results. Failures trigger rollback; interrupted sessions can recover from History. Restore refuses outside changes. Network shares, symlinks/junctions, mixed native/managed assemblies, and target files over 8 MB are unsupported. New target-app releases need matching new patch files.

## Color options (0.6.0)

Selected appearance patches can expose color fields and a **Choose…** color picker. Colors use #RRGGBB. Preferences persist separately from imported patch definitions, so source updates retain them. Preview embeds the selected colors in the generated resources; apply, elevated worker requests and history keep the exact choices. Changing colors and previewing an already-patched app updates it directly from verified originals. Invalid or incomplete colors block preview.

## Patch files

The library and patch cards show the imported pack version and individual patch versions, separately from the target app version. Preview includes the pack version. History records the versions actually applied and the imported file's SHA-256, so importing a newer definition never relabels an older session. Legacy files and sessions without version metadata show **Unversioned**. Version labels are author declarations; exact original fingerprints still decide compatibility.

See [PATCH-FORMAT.md](PATCH-FORMAT.md). Most operations are declarative. Version 0.7.0 also accepts explicitly declared, hash-verified managed client modules. The library identifies these as executable code and the preview shows their SHA-256. Importing, previewing and applying only validate/embed the module; it runs later inside the target app. A hash establishes integrity, not trust or a sandbox: review the author's source before applying client code. App behavior and server entitlements remain subject to the target app's implementation. Shell commands and download operations are unsupported.

Version 0.4.2 added conditional boolean setter overrides for patch packs that need to change selected rows while preserving the other rows' restrictions. Proton pack 1.2.0 requires this operation. Patch packs remain separate downloads.

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

Version 0.7.0 adds the managed client hooks needed by the separate Proton guest candidate. The installer still includes no patches or guest client code. Custom colors, promotional controls and direct patch upgrades are retained. The separate pack removes AMOLED and can remove its previous edits during an update. See the separate patch repository for guest validation and outstanding connection tests.
