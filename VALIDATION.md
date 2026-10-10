# Patchwork validation

## Local 0.8.0 prerelease: Blitz ASAR support

Built on Windows x64 on October 10, 2026 from upstream commit `e777596adc92f888a25aca938fd03984245060ee`. The final test build passes **44 scenarios, zero failures**, including all 41 existing scenarios and three new ASAR/client-closure scenarios. New coverage exercises parsing, changed lengths, UTF-8 BOMs, full/block integrity, composed edits, conflicts, original archive/member hashes, exact counts, rejected paths, linked/unpacked edit refusal, size limits, previews without writes, selection updates, rollback, interrupted recovery, corrupt backups and byte-exact restoration. A generated child process verifies the closed-Blitz transaction guard.

The separate Blitz pack targets installed client 3.0.7.134 and frontend 3.0.8-ota.0. All apply/update/restore and worker checks use isolated copies of the actual 18,257,553-byte archive. Both selections and each individual selection preserve all 3,051 entries. An independent Node reader compares untouched contents/metadata, checks changed-member integrity and parses the resulting JavaScript. The installed Blitz archive is never changed.

Desktop lifecycle/request fixtures exercise startup, reload, navigation, BrowserView recreation, one request-filter registration per session, desktop ID scoping and unknown-version fallback. A real headless Chromium layout fixture checks the verified selectors, reclaimed rail width, collapsed hit targets, dynamic elements and ordinary controls. A separate isolated Electron 31.2.1 runtime checks native ASAR reads and JavaScript syntax. Its local HTTPS fixture seeds memory/disk caches in an unrelated live view, then verifies supported startup, reload, full/SPA navigation and view recreation: ten ad requests are cancelled, one filter is registered, and the existing response handler runs 38 times. Unknown versions and unrelated views retain their behavior. Supported desktop views bypass cache to cover memory-cached ad scripts; this can increase downloads on full loads. Native 940 x 500 minimum sizing passes.

Window-sizing fixtures execute the changed code for logged-out/free/premium labels and small/normal screens. These are controlled fixtures; live Blitz startup, real account sign-ins, login/settings/game flows, overlay behavior and the update service remain untested. The installer passes ten update-lock checks on workspace installations with registry/shortcut integration disabled. See the separate `VALIDATION-Blitz.md` delivery notes for final evidence and limits.

Production builds exclude test fixtures. The installer remains separate from patch packs and includes seven app/library/guide files. Version 0.8.0 is an experimental prerelease and has no publisher signature.

## Experimental 0.7.0 prerelease

The app test build passes **41 scenarios, zero failures**. Managed client tests verify actual fallback/after execution, preservation of original behavior, one embedded resource per payload, and refusal of invalid hashes, entries and target signatures. Client modules are identified automatically in the library and with their SHA-256 in preview; they are never executed during import, preview or application. The installer still excludes patch definitions and client modules. This is a prerelease; Proton guest tunnel validation remains pending. App updater tests cover opt-in prereleases, draft exclusion, no downgrade and unchanged integrity checks.

## Earlier published checks

Built and checked on Windows x64 on October 9, 2026. Target-file changes and installer tests used workspace copies or generated fixtures. The user's installed Proton files, account settings, registry and shortcuts were not changed.

## App: 37 scenarios passed

The test build passed 37 scenarios with zero failures. Existing coverage includes text/JSON and managed-method preview/apply/exact restore; original fingerprints; stale previews and outside edits; dependencies/conflicts; path and operation boundaries; interrupted transactions; corrupted backups; BOM handling; transaction locking; managed signatures and runtime behavior; worker requests; updater metadata/digests; empty libraries; conditional projections/inherited setters; and version labels preserved in history.

Source checks cover URL normalization/rejection, persistence, hourly eligibility, stable/pre-release channels, repository and asset URL matching, size/digest/schema rejection, unchanged-asset caching, invalid batch refusal without writes, ownership conflicts, version increases, no automatic downgrade, removal preserving packs/history, and outside catalog edits. The WPF test checks source cards, imported versions, the preserved applied selection, update status, CURRENTLY APPLIED preview label and Update patches button without writing targets. The Windows transport rejects unapproved/insecure hosts, credentials, fragments, nonstandard ports and invalid maximum sizes.

Direct patch update scenarios cover successive versions, removed selections and multiple files; byte-exact final original restore; outside edits, corrupted originals, changed target versions and stale parents; rollback after a partial write; recovery before and after a durable commit; patched version files; old journals without patch IDs; worker update requests; and version-only updates retaining originals.

Production builds exclude all eight self-test/fixture files. The installer ZIP contains exactly seven application/library/guide files, with no patch packs, recipes, patch generator or Proton binaries. The app has no bundled-patch loader.

## Actual Proton copy upgrade

Applied the earlier pack 1.2.0 to a complete workspace copy of Proton Windows 5.1.8.0, retained the older journal format without patch IDs, then upgraded directly to pack 1.3.0 without manually restoring first. The six tracked assemblies matched the new preview hashes; the previous journal became Superseded and the new version became Applied. Preview left targets unchanged.

After the direct upgrade, the copied assemblies passed 40 free-selector checks, 13 settings/Accelerator checks and 19 earlier method checks under .NET 8, using in-memory fixtures (72 method checks total). Restoring the new session recovered all six exact original fingerprints. A separate actual WPF upgrade preview was rendered against the earlier applied pack and then that copy was restored.

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

## Color and WinUI operations

Three theme scenarios test composed resources, normalized choices, history, direct updates, exact restoration, worker tamper refusal and WPF preference fields. The native probe verified 74 resources and five lookups. Full Proton page rendering remains untested.

## Version 0.6.1 presentation checks

All 39 app scenarios passed. The two additional scenarios execute enum collection filters/lookup guards and WinUI visibility hooks, testing nulls, multiple returns, original object/list identity, paid preservation and restoring available controls. Invalid UI receivers/fields, hook arguments and undeclared enum values are refused. Conditional restriction chains bind inherited generic types and support expected-false conditions and exclusions.

Proton pack 1.3.0 to 1.4.0 direct upgrade passed on a complete copy, including an older journal without patch IDs, eight tracked assemblies, deterministic previews, color history and exact restoration. Separate probes passed 87 actual method checks and seven real WinUI promotional-control checks. Normal account plan, maintenance and server eligibility checks remain active. The Android-identity guest API experiment obtained a session, VPN credentials and a Windows-identity certificate, then revoked both sessions; client integration and a real tunnel remain unimplemented.
