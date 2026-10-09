# Patchwork 0.4.0 validation

Built and checked on Windows x64 on October 8, 2026 (local date). All target-file changes and installer tests used workspace copies or generated fixtures.

## App: 22 scenarios passed

The test build passed 22 scenarios with zero failures: text/JSON preview, apply, exact restoration, unsupported fingerprints, stale previews, outside edits, dependencies/conflicts, path boundaries, unsupported scripts, rollback after partial failure, interrupted recovery, tampered backups, BOM handling, repeat apply/restore, transaction locking, managed operation runtime execution and signature/type checks, and worker apply/restore boundaries.

Four new scenarios cover update version comparison, draft/prerelease exclusion, expected release repository and installer assets, digest/size rejection, tampered and non-executable downloads, and the import-only library. A fresh library contains zero bundles; importing a separate file populates it, reimporting replaces the same ID without duplication, and restarting preserves the import.

Production builds exclude all five self-test/fixture files. The installer ZIP contains exactly seven application/library/guide files, with no patch packs, recipes, patch generator, or Proton application binaries. The app has no bundled-patch loader.

Three additional scenarios execute conditional redirects, boolean guards, collection projections and generic inherited overrides, reject incompatible definitions, and verify version labels/import replacement/history preservation. Legacy definitions remain usable with Unversioned labels.

## Installer: 9 checks passed

Checks cover payload extraction, updating a recognized installation, staging rollback, preserving unrelated files/data on uninstall, refusing unrelated installation/uninstallation folders, and Windows shortcut creation/ownership/removal. These used workspace folders and temporary shortcuts. No real installation, Installed Apps registration, Start menu, desktop shortcut, or installed Proton files were changed.

The UAC worker was tested without elevation on copies. An interactive administrator apply was not performed. Mono.Cecil's compatibility patch and rebuild script compiled successfully from a clean 0.11.6 source archive during the previous build.

## GitHub updater

Checks use the public latest-release API for the fixed patcher repository, require stable semantic version tags, and restrict the installer asset to that repository's release URL. Downloads have HTTPS host/size limits and must match GitHub's SHA-256 metadata before setup is opened. Updates do not download patches. Startup checking can be disabled.

The actual public release check and installer download are verified after publication. The verification does not launch an installer or update the user's installation. Checksum verification relies on GitHub repository metadata and is not a publisher signature.

## Visual checks

Rendered the actual WPF empty library, imported Proton catalog, managed-method preview, minimum-size library, updater panel, and setup. Patch definitions remain in the separate patch repository. The Proton pack's earlier file-transformation and isolated method results are documented there.

## Limits

The work does not validate a live Proton VPN session, DNS leak protection, NetShield filtering, LAN reachability, split routing/kill-switch behavior, profile connections, or server entitlements. Windows 5.1.8's authenticated connection credentials prevent a working no-sign-in patch using only a login-screen edit; no such patch is distributed.
