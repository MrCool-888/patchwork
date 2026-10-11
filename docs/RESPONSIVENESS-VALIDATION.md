# Patchwork 0.9.3 responsiveness validation

Validated on Windows x64, October 11, 2026. All patch operations used isolated targets or generated fixtures; installed Lunar files were not changed.

## Cause and change

Inspection established three dispatcher-bound operations: synchronous preview generation, complete-file fingerprint/applied-session verification, and assigning complete wrapped before/after strings to WPF TextBoxes. The reported user's pause was not instrumented, so its precise stage remains unknown. An independent real-controller probe reproduced a 20.7-second UI pause when displaying Lunar's JavaScript resource in 0.9.2.

Preview now captures the pack, target, selections and options before background work. Busy state rejects duplicate operations and protects window closure. Deferred source refreshes invalidate old previews. Target inspection coalesces requests and ignores stale results, including errors, after target/pack changes or closure. Verification uses the engine transaction lock. History and applied-selection metadata load in the background; controls update on the dispatcher. Preview, apply and restore show an indeterminate indicator and phase text.

Before/after contents remain complete inside Patchwork, in paired pages of at most 32,768 UTF-16 code units. Surrogate pairs stay intact at boundaries. Page counts support unequal lengths and reset when changing files. Paging never changes patch bytes or hashes. No patch schema, compatibility checks, backup format, or restoration rules changed. Production app and setup builds exclude test hooks and fixtures; installer test builds use `-Tests`.

## Regression suites

- **51 patcher scenarios passed, zero failures**, including all existing suites and two new paging/controller scenarios.
- The actual WPF controller/dispatcher scenario passed **40 assertions**. Paused preview, target inspection, history and restore workers remained incomplete while dispatcher timers ticked. Checks cover duplicate preview requests, error/retry, deferred source refresh, stale target results/errors, coalescing, close protection, ignored closed-window results, post-apply/post-restore checks, and pack selection behind a held transaction lock.
- Paging checks reconstruct full contents, bound every page, preserve surrogate pairs, cover unequal sides and 8.7-million-character content, and exercise actual controls and file switching.
- **9 installer checks** and **10 update-lock checks** passed. An additional isolated upgrade using the original 0.9.2 installer and the production 0.9.3 installer verified both version markers and retention of an unrelated file. Registry and normal shortcuts were disabled; the installer self-test used a temporary shortcut.
- Reflection confirmed production app/setup contain no test mutation/gate fields, controller test classes, or installer self-test entry point.

## Full-sized Lunar comparisons

The existing Lunar validator ran against frozen 0.9.2 and candidate builds, for both launcher 3.7.24 and 3.7.17, using Lunar pack 0.1.6 and each matching 0.1.5 predecessor. All four runs passed complete preview/deterministic JAR checks, injected partial-write rollback, direct upgrades, updater/startup/promotion deselection and reselection, independent promotions apply/restore, cosmetics selection removal, and exact restoration of all five files. Baseline and candidate staged hashes matched **5/5 for each launcher**.

| Patched file | SHA-256, 3.7.24 |
| --- | --- |
| Lunar Client.exe | `1c50e0d18e2d1032036ee364f72bf00ead7ffe3232346b1d04edc1242f9876ba` |
| app.asar | `18e6e53724df1959c18a6b198941d5b87771ea0e417293bfdb3de68e3fb47959` |
| genesis-0.1.0-SNAPSHOT-all.jar | `ec31b0ec46acce89e3b5b73ce678262595865b92494b4872f6ae8fd2e59b864f` |
| index.js | `df1545446f216a1c293cde8fadbfbc7a7ab42ea79dc032ac5ece4e5875de610f` |
| lunar.jar | `7b8dadc6a32637bc7a22bf253e9282a37ae5719e4caae88ff9a34ec32ffe8961` |

For 3.7.17, the executable was `3e2f073b777a976f64e44cbb78b7e482951554b024c6452edb1799134b9983d3` and archive `79f471c3a961372f1279db33b8f7f2772162a74cb54f02c52ef132459d62fb73`; the other three hashes matched 3.7.24. Lunar pack files require no edits.

An additional actual WPF-controller probe used full-sized 3.7.24 copies:

| Operation | 0.9.2 elapsed / UI timer ticks | Candidate elapsed / UI timer ticks |
| --- | --- | --- |
| Construct and initial verification | 1,062 ms / 0 | 1,003 ms / 46 |
| Complete preview and verification | 2,989 ms / 0 | 3,375 ms / 193 |
| Display index.js and layout | 20,733 ms / 0 | 61 ms / 3 |
| Apply and follow-up inspection | — | 19,546 ms / 1,073 |
| Update preview and inspection | — | 4,685 ms / 275 |
| Update apply and inspection | — | 13,405 ms / 765 |
| Restore and inspection | — | 10,365 ms / 623 |

The candidate controller completed the full apply/update/restore cycle and restored every original hash. Its JavaScript TextBoxes contained 32,768 units per side instead of 8,680,552/8,680,156. The worst measured candidate timer gap across operations was 343 ms. Timing includes queued verification and layout, varies with disk/cache/GC/load, and is not a speed guarantee; the fix removes long dispatcher work rather than promising faster patch transactions.

## Delivery and limits

The prerelease contains the production installer, committed source archive and this report. Release assets are checked against GitHub sizes and SHA-256 digests and downloaded again after publication. Stable/latest releases remain unchanged.

No live Lunar install was patched for this task. Low-space and overlong temporary fixture paths caused preliminary test failures; final isolated runs passed after bounded scratch cleanup and shorter fixture paths. Existing runtime/game limitations of Lunar 0.1.6 are unchanged. Large patches still need sufficient disk space for verified backups and rollback; UI responsiveness does not remove that requirement.
