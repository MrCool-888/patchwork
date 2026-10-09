# Patchwork 0.4.1 validation

Built and checked on Windows x64 on October 9, 2026. All target-file changes and installer tests used workspace copies or generated fixtures.

## App: 23 scenarios passed

The test build passed 23 scenarios with zero failures: text/JSON preview, apply, exact restoration, unsupported fingerprints, stale previews, outside edits, dependencies/conflicts, path boundaries, unsupported scripts, rollback after partial failure, interrupted recovery, tampered backups, BOM handling, repeat apply/restore, transaction locking, managed operation runtime execution and signature/type checks, and worker apply/restore boundaries.

Four new scenarios cover update version comparison, draft/prerelease exclusion, expected release repository and installer assets, digest/size rejection, tampered and non-executable downloads, and the import-only library. A fresh library contains zero bundles; importing a separate file populates it, reimporting replaces the same ID without duplication, and restarting preserves the import.

Production builds exclude all five self-test/fixture files. The installer ZIP contains exactly seven application/library/guide files, with no patch packs, recipes, patch generator, or Proton application binaries. The app has no bundled-patch loader.

Three additional scenarios execute conditional redirects, boolean guards, collection projections and generic inherited overrides, reject incompatible definitions, and verify version labels/import replacement/history preservation. Legacy definitions remain usable with Unversioned labels.

## Installer: 9 checks passed

Checks cover payload extraction, updating a recognized installation, staging rollback, preserving unrelated files/data on uninstall, refusing unrelated installation/uninstallation folders, and Windows shortcut creation/ownership/removal. These used workspace folders and temporary shortcuts. No real installation, Installed Apps registration, Start menu, desktop shortcut, or installed Proton files were changed.

The UAC worker was tested without elevation on copies. An interactive administrator apply was not performed. Mono.Cecil's compatibility patch and rebuild script compiled successfully from a clean 0.11.6 source archive during the previous build.

## GitHub updater

Checks use the public latest-release API for the fixed patcher repository, require stable semantic version tags, and restrict the installer asset to that repository's release URL. Downloads have HTTPS host/size limits and must match GitHub's SHA-256 metadata before setup is opened. Updates do not download patches. Startup checking can be disabled.

Public release metadata and asset bytes are verified after publication. Node HTTPS verifies the published installer download; the Framework downloader may encounter a certificate trust failure with this workstation's local proxy. TLS verification remains enabled. Verification does not launch an installer or update the user's installation. Checksum verification relies on GitHub repository metadata and is not a publisher signature.

## Visual checks

Rendered the actual WPF empty library, imported Proton catalog, managed-method preview, minimum-size library, updater panel, and setup. Patch definitions remain in the separate patch repository. The Proton pack's earlier file-transformation and isolated method results are documented there.

## Update locks: 10 checks passed

The separate `source/installer/UpdateTests.cs` runner passed ten real-process and filesystem checks. It verified the exact originating process start time, an already-exited process, a reused PID identity, bounded timeouts without terminating a process, and waiting for the matching installed app for older updater callers. Windows reproduced a directory rename failure with the installation as the process working folder; setup released that folder and updated successfully. A file lock refused replacement while retaining the old installation, and retrying after releasing the lock succeeded. These checks did not modify a live installation, registry, user shortcuts, imported packs, or backups.

The app scenario additionally checks that setup starts from its download directory, passes the originating PID/start time, and preserves a custom recognized install location. Both new and older updater callers receive the setup-side working-directory fix. Live in-app replacement of the user's installation was not performed.

## Limits

The work does not validate a live Proton VPN session, DNS leak protection, NetShield filtering, LAN reachability, split routing/kill-switch behavior, profile connections, or server entitlements. Windows 5.1.8's authenticated connection credentials prevent a working no-sign-in patch using only a login-screen edit; no such patch is distributed.
