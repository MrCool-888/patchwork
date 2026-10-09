# Patchwork 0.5.0 validation

Built and checked on Windows x64 on October 9, 2026. Target-file changes and installer tests used workspace copies or generated fixtures. The user's installed Proton files, account settings, registry and shortcuts were not changed.

## App: 34 scenarios passed

The test build passed 34 scenarios with zero failures. Existing coverage includes text/JSON and managed-method preview/apply/exact restore; original fingerprints; stale previews and outside edits; dependencies/conflicts; path and operation boundaries; interrupted transactions; corrupted backups; BOM handling; transaction locking; managed signatures and runtime behavior; worker requests; updater metadata/digests; empty libraries; conditional projections/inherited setters; and version labels preserved in history.

Source checks cover URL normalization/rejection, persistence, hourly eligibility, stable/pre-release channels, repository and asset URL matching, size/digest/schema rejection, unchanged-asset caching, invalid batch refusal without writes, ownership conflicts, version increases, no automatic downgrade, removal preserving packs/history, and outside catalog edits. The WPF test checks source cards, imported versions, the preserved applied selection, update status, CURRENTLY APPLIED preview label and Update patches button without writing targets. The Windows transport rejects unapproved/insecure hosts, credentials, fragments, nonstandard ports and invalid maximum sizes.

Direct patch update scenarios cover successive versions, removed selections and multiple files; byte-exact final original restore; outside edits, corrupted originals, changed target versions and stale parents; rollback after a partial write; recovery before and after a durable commit; patched version files; old journals without patch IDs; worker update requests; and version-only updates retaining originals.

Production builds exclude all seven self-test/fixture files. The installer ZIP contains exactly seven application/library/guide files, with no patch packs, recipes, patch generator or Proton binaries. The app has no bundled-patch loader.

## Actual Proton copy upgrade

Applied the earlier pack 1.1.0 to a complete workspace copy of Proton Windows 5.1.8.0, retained the older journal format without patch IDs, then upgraded directly to pack 1.2.0 without manually restoring first. The six tracked assemblies matched the new preview hashes; the previous journal became Superseded and the new version became Applied. Preview left targets unchanged.

After the direct upgrade, the copied assemblies passed 34 free-selector checks, 13 settings/Accelerator checks and 19 earlier method checks under .NET 8, using in-memory fixtures (66 checks total). Restoring the new session recovered all six exact original fingerprints. A separate actual WPF upgrade preview was rendered against the earlier applied pack and then that copy was restored.

## Live GitHub downloads

The production source downloader fetched the real public patchwork-patches releases, downloaded pack 1.2.0, verified its size and GitHub SHA-256 digest, imported it, and skipped the unchanged patch asset on the next check. The production app updater queried the public latest patcher release and downloaded its installer with exact size/hash verification; no installer was launched by this check.

HTTPS uses native Windows WinHTTP, the default Windows proxy configuration, TLS 1.2, normal certificate validation, bounded timeouts and streamed size limits. Redirects are validated explicitly against GitHub download hosts; cookies and automatic authentication are disabled. This replaced the older Framework transport that failed with this workstation's proxy. Certificate validation was never bypassed. Checksums use GitHub metadata and are not publisher code signatures.

## Installer and update locks

The installer passed nine checks for payload extraction, replacing a recognized installation, staging rollback, preserving unrelated files/data, rejecting unrelated roots, and temporary shortcut ownership/removal. The separate source/installer/UpdateTests.cs runner passed ten real-process/filesystem checks: exact process start time, exited/reused process identity, bounded waits without termination, older updater callers, releasing the working-directory lock, refusing replacement under a file lock while retaining the old installation, and succeeding after the lock is released.

These checks used workspace folders and temporary shortcuts. Live replacement of the user's Patchwork installation and interactive administrator/UAC patch application were not performed. The worker was tested without elevation on copies. Mono.Cecil's compatibility rebuild was validated in the earlier release.

## Visual checks and limits

Rendered the actual WPF source page at normal and minimum window sizes and an applied-Proton upgrade preview. Earlier releases also checked the empty library, imported catalog, updater, method preview and setup.

Automatic source checks update saved definitions only; the user previews and applies target changes explicitly. Exact original fingerprints and a verified applied journal remain required for direct updates. New target-app releases need matching new packs. Multiple legacy applied sessions in one folder require restore first.

No live Proton sign-in, VPN tunnel, Accelerator throughput, DNS leak protection, NetShield filtering, LAN reachability, split routing, profiles or server entitlement was tested. Guest/no-sign-in sessions are not implemented in the separate Windows patch pack. Patches remain in their separate repository.
