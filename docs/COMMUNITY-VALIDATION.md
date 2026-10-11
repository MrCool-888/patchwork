# Patchwork 1.0.0 prerelease validation

Date: 2026-10-11. Scope: portable Windows recipes, author tools and synthetic examples. Stable latest remains 0.9.3.

## Engine and recovery

The final TEST_BUILD runner passed **54 scenarios with 0 failures**, including **40 actual WPF controller assertions**. Existing source, updater, managed IL, theme, archive, transaction and preview paging checks remain included.

New checks cover arbitrary native DLL/data splices, IL-only EXE methods, UTF-8 resources with nonstandard extensions, file additions/replacements, zero-byte files versus absence, payload fingerprints and bounds, conflicting transforms, occupied destinations, unsafe filesystem paths, direct pack upgrades, selection removal/reselection, outside-change refusal, durable rollback and prepared-journal recovery. Old journals without existence/format fields restore their original bytes. Worker requests re-preview nullable fingerprints and reject forged state; apply/restore round trips include new additions.

ZIP/ASAR checks cover text and binary members, additions/replacements/splices, unchanged member contents, malformed/signed archives, path and namespace conflicts, additions beneath linked/unpacked parents, binary previews, and streaming comparison of eight unchanged 7 MiB ZIP members. Legacy JAR splice/add output bytes and preview strings match the original implementation.

A real-pack comparison found and fixed an ASAR regression involving valid `unpacked: true` directory metadata in Blitz. Existing unpacked trees are preserved; new packed-member operations cannot write beneath them. Archive member names remain separate from Windows filesystem creation rules.

## Existing application packs

Separate 0.9.3 and 1.0.0 processes operated only on isolated copies of exact original app files. Both versions passed deterministic preview, apply, metadata/selection update, deselection and byte-exact original restore.

| Pack | Compared target files | Result |
| --- | ---: | --- |
| Proton VPN 5.1.8 / pack 1.5.1 | 10 | Initial and selection-update output SHA-256 values match 0.9.3. |
| Blitz 3.0.7 / pack 1.1.1 | 2 | Initial and selection-update output SHA-256 values match 0.9.3, including the companion checksum. |
| Lunar Client 3.7.24-ow / pack 0.1.6 | 5 | Every output SHA-256 matches 0.9.3; exact restore passed. |
| Lunar Client 3.7.17-ow / pack 0.1.6 | 5 | Every output SHA-256 matches 0.9.3; exact restore passed. |

No installed target application, account, cosmetics state or user patch history was modified. These are file compatibility checks, not new live application/network behavior tests.

## Author examples and command line

Five previously unknown app IDs use one production executable: configuration/resources; managed DLL and EXE; Electron ASAR plus checksum; Java/ZIP; and raw data splices. All 15 version/selection combinations validate, and each example completes apply, update, deselection and exact restore. The generated Java class passes `java -Xverify:all` in the host-runtime run. Fixture code and generators accompany the packs; no vendor app binaries are included.

The GUI production executable also returns actionable redirected diagnostics and exit codes 0/1. Validation does not import recipes, run update checks or write target files. Unsafe report paths cannot overwrite recipes or target files; reports use create-new semantics.

## Installer and remaining limits

The installer passed **9 self-checks** and **10 update-lock checks**. An actual isolated 0.9.3-to-1.0.0 installer upgrade preserved an unrelated note; registry entries and shortcuts were disabled for that test. Production binaries are checked for version and absence of test fixtures/hooks. The production worker protocol is checked separately; a real UAC dialog is not automated.

This prerelease supports bounded recipes, not arbitrary patcher plugins. Native binary edits and Java classes require author runtime validation. Signature-enforced or encrypted packaging, mixed native/managed IL rewriting, Android, and novel formats are not claimed. Use 1.0.0 to restore its new file-add transactions before returning to an older patcher. Existing stable packs require no edits.

Release delivery checks verify installer/source/example assets, their SHA-256 digests, prerelease discovery and preservation of stable latest. Source and examples are published separately from normal patch-source JSON assets.
