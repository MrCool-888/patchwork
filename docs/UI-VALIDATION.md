# Patchwork 1.1.0 UI validation

Date: 2026-10-11. Windows x64 / .NET Framework 4.8. Release channel: prerelease; stable latest remains 0.9.3.

The final test build passed **55 scenarios with 0 failures**, including **65 assertions against actual WPF controllers/windows**. Existing managed, resource, archive, source, updater, transaction, worker, recovery and responsiveness checks remain included.

New checks cover all eight combinations of Dark/Light and Violet/Blue/Mint/Amber. Normal, secondary and accent text, primary-button text and status colors meet measured 4.5:1 contrast against their intended surfaces. Input outlines meet 3:1 against inputs. Selection colors are checked against white selected text. These are palette checks, not a claim of complete accessibility certification.

Theme brushes remain live when actual WPF styles load; an integration failure involving frozen brushes was found and fixed with bound palette colors. Appearance is independent per window, persists in a separate bounded preferences file, and falls back safely for corrupt preferences. Changing theme/accent preserves selected patches, target folder and the same verified preview object. Patch-pack color swatches retain literal recipe colors.

Real-window tests cover actionable missing-folder/empty-selection guidance, visible folder/review controls at the minimum size, community-source prefilling without automatic importing, retained pack/source metadata, and all existing asynchronous stale-result/busy/restore protections.

Off-screen WPF renders were visually reviewed for the welcome screen, imported Proton pack, compact selection screen, generic two-file preview and light Settings screen. The compact review action is pinned; preview paging and apply/back actions use separate rows. Renders use 96-DPI capture. Live screen-reader, high-DPI/multiple-monitor and real UAC-dialog interaction are not automated.

The patch engine/schema and pack minimum versions remain unchanged from 1.0.0. Community operations still require `minimumPatcherVersion: "1.0.0"`. Existing pack assets are not republished or altered by this UI release. No installed target app was patched during UI testing.

All five published community examples passed against the 1.1.0 production build: 15 CLI version/selection combinations and apply/update/deselection/exact-restore lifecycles. The optional Java runtime check was skipped in this run because it was unavailable or did not return; Java verification from 1.0.0 is not claimed as a new 1.1.0 run.

The installer passed 9 self-checks and 10 update-lock checks. An isolated actual 1.0.0-to-1.1.0 upgrade preserved an unrelated note with registry and shortcuts disabled. The installer is built from extracted committed source and includes the new user guide. App and setup metadata are 1.1.0.0; production builds exclude test fixtures/hooks. Release delivery verifies asset hashes, public downloads and prerelease updater discovery without launching the downloaded installer.
