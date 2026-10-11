# Authoring a Patchwork patch pack

This guide is for people who want to change files in an application with Patchwork. A pack is a small JSON recipe. Patchwork checks the recipe against exact original hashes, previews the result, then applies the selected changes with backups so it can update or restore them later.

Patch packs are separate downloads. They are never part of the Patchwork installer. A valid recipe proves that its declared inputs and outputs match; it does not prove that the application's behavior is safe or correct. Test on a disposable copy of the target and publish the source and reproduction steps for executable changes.

## 1. Prepare an isolated target

Copy the supported application files into a disposable folder. Keep the publisher's original files unchanged. Record the target version, source/build identity where available, and the SHA-256 of the `versionFile` that identifies this target build. Do not use a version label as a substitute for fingerprints.

Use PowerShell to hash a file:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath .\Target.exe).Hash.ToLowerInvariant()
```

For every operation, hash the original file bytes. Archive operations also need the exact original member hash. Binary and archive splices use offsets measured in original bytes; calculate the expected result hash from the complete transformed bytes. A recipe should describe one known original build and produce a fully specified result.

For an ordinary ZIP/JAR member, list and hash entries from a disposable copy with PowerShell/.NET:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path .\fixture.jar))
try {
  foreach ($entry in $zip.Entries) {
    $stream = $entry.Open()
    try {
      $sha = [Security.Cryptography.SHA256]::Create()
      try { '{0}  {1}' -f ([BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()), $entry.FullName }
      finally { $sha.Dispose() }
    } finally { $stream.Dispose() }
  }
} finally { $zip.Dispose() }
```

For a managed `.exe` or `.dll`, use the bundled Mono.Cecil library to discover the exact method signature rather than guessing from a decompiler's short display:

```powershell
$cecil = [Reflection.Assembly]::LoadFrom((Resolve-Path .\Mono.Cecil.dll))
$module = [Mono.Cecil.ModuleDefinition]::ReadModule((Resolve-Path .\Target.dll))
try {
  $module.Types | ForEach-Object { $_.Methods | ForEach-Object FullName }
} finally { $module.Dispose() }
```

For ASAR, inspect a disposable copy with the ASAR format parser/extractor shipped or documented by the target application's publisher. This authoring guide and Patchwork's validator do not extract ASAR files for discovery; use the parser to find member names and bytes, then hash the exact member bytes. Do not guess offsets or hashes from screenshots or a different app version.

## 2. Choose the smallest operation

Use `textReplace` for an exact UTF-8 string replacement, `fileAdd` for a file that must not already exist, and `fileReplace` for replacing a whole file. Use `binarySplice` for bounded byte-range changes to any regular file. Use `zipTextReplace`, `zipEntrySplice`, or `zipEntryAdd` for ZIP-family archives. Use `asarTextReplace`, `asarEntryAdd`, or `asarEntryReplace` for packed Electron ASAR members. Use the managed operations for verified IL changes to managed assemblies.

Binary, ZIP and ASAR payloads are data. Patchwork does not load or execute them. Code changed inside the target application may execute later with that application's privileges. Treat DLLs, Java class members, JavaScript and other program resources accordingly; inspect the preview and trust the publisher before applying.

New operations described in this guide require `minimumPatcherVersion: "0.10.0"`. They need Patchwork 0.10.0 prerelease from the `community-patches` branch (or `v0.10.0` after publication); stable 0.9.3 rejects them. Keep this field in the root bundle whenever a pack uses any 0.10.0 capability. Older operations remain usable with their documented minimum version.

## 3. Build a complete schema-1 bundle

Start from this outline and replace every example value with values measured from your fixture:

```json
{
  "schemaVersion": 1,
  "id": "example-tool",
  "appId": "example-tool",
  "appName": "Example Tool",
  "appVersion": "1.2.3",
  "versionFile": "Example.exe",
  "versionSha256": "64-character-original-SHA256",
  "author": "Your name or project",
  "source": "Source repository and revision",
  "packVersion": "1.0.0",
  "minimumPatcherVersion": "0.10.0",
  "patches": [
    {
      "id": "example-change",
      "name": "Example change",
      "description": "Describe the local result and its limits.",
      "dependencies": [],
      "conflicts": [],
      "operations": []
    }
  ]
}
```

IDs use lowercase letters, digits and hyphens. Keep the bundle ID stable when updating the pack; assign a strictly higher three-part `packVersion` for changed contents. Patch IDs should also remain stable so saved selections can be updated. Declare dependencies and conflicts when two patches need a particular selection relationship. Validate that every referenced patch ID exists in the bundle.

The optional `targetLocations` list helps Patchwork suggest a target folder. Each item has `base` (`localAppData`, `appData`, `programFiles`, `programFilesX86`, or `userProfile`) and a safe relative `path`; an empty path means the base itself. Do not put absolute paths in a pack. The optional `processes` list describes processes to check before changing files: each item has an extensionless executable `name` and `scope` of `target` or `all`. Limit each list to 32 entries. If these hints are omitted, Patchwork keeps its existing target detection and process checks.

## 4. Understand operation limits

All paths are relative to the selected target and must remain inside it. Use forward slashes for archive member names. Each file and member hash refers to the original bytes, unless the field explicitly says `resultSha256`. `fileAdd` requires that the path is absent and intentionally has no original `sha256`; `fileReplace` requires the original `sha256`. Both carry `dataBase64` and `resultSha256`, and inline file data is limited to 256 KiB.

`binarySplice` handles any regular target file up to 256 MiB. It accepts at most 256 non-overlapping edits, each with at most 256 KiB of replacement data and at most 512 KiB total. Each edit gives an original-byte `offset`, removed-byte `length`, and `dataBase64`. The operation requires the original `sha256` and whole-file `resultSha256`.

Managed `.exe` and `.dll` targets are limited to 8 MiB. `textReplace` accepts valid UTF-8 text with any extension up to 8 MiB; `.js` files retain their 16 MiB limit. Ordinary ZIP, JAR, WAR and EAR files are limited to 128 MiB and each edited member to 8 MiB. `zipEntrySplice` requires archive `sha256`, member `entrySha256`, member `resultSha256`, and bounded edits. `zipEntryAdd` requires an absent member and uses `dataBase64` plus `resultSha256`. `zipTextReplace` requires member `entrySha256`, exact `find` and `replacement` strings, and an exact positive `count`. Existing `jarEntrySplice` and `jarEntryAdd` remain available for Java class members under their existing rules.

ASAR archives are limited to 64 MiB. `asarEntryAdd` uses an absent member, `dataBase64`, and `resultSha256`. `asarEntryReplace` additionally requires the original member's `entrySha256`. Inline payloads and edited members use the documented 256 KiB and 8 MiB bounds respectively. Existing ASAR integrity and checksum handling still applies; Patchwork validates and rebuilds the archive without extracting it into the application folder.

The full bundle must remain below 1 MiB. Inline Base64 expands binary data, so larger payloads need another distribution/design approach and are rejected. Pack sources accept published release assets, not repository commits or source ZIPs; each source supports at most 20 distinct assets, and each asset must be under 1 MiB. Publish the `.json` or `.patchwork` file as a GitHub release asset with a stable filename, increasing `packVersion`, and GitHub's release asset digest metadata.

## 5. Check the pack and exercise its lifecycle

Build Patchwork from the matching source revision, then run its read-only validator against the exact original fixture and selected patch IDs:

```powershell
.\Patchwork.exe --validate-patch .\Example.patchwork.json --target .\fixture --select example-change | Out-Host
```

`--target` requires `--select`. Validation checks the recipe against the supplied isolated target. It does not import the pack into the user's library, apply it to an installed application, or contact a network service. A nonzero exit means the selected recipe did not validate; inspect the diagnostic and correct the fixture or recipe. To save a GUI-friendly report, add `--report .\validation-report.txt`; the report path must be new and outside both the recipe and target tree. Validation always uses original fixtures and isolated preview history; it cannot validate an already-applied installation by using that user's Patchwork history.

For each pack version, check five things on disposable copies with the same production binary: validate the unmodified original; apply the full intended selection; update to a changed selection and confirm deselected edits disappear; update to the next pack version from verified originals; and restore, then compare every target file byte-for-byte with the starting copy. Repeat with missing, already-present, wrong-hash, malformed and conflicting inputs where those cases apply. Keep the fixtures and commands alongside the recipe so another author can reproduce the result.

Patchwork 0.10.0 can read legacy journals, but installations with new `fileAdd`-based transactions should be restored while using 0.10.0 before returning to stable 0.9.3. Keep the matching Patchwork version available for exact restoration.

## 6. Publish a reviewable release

Keep the recipe JSON, source code, target-fixture generator, build instructions and smoke commands in the patch repository. Do not publish toy fixture packs as official compatibility packs. Do not commit vendor application binaries, user data, credentials or backups. A small synthetic fixture is appropriate for demonstrating the format; label it clearly and make no live-application claims from it.

For GitHub source discovery, attach the final pack file as a release asset. Repository JSON files and the GitHub source ZIP are not imported. Use GitHub's asset digest, keep the asset filename stable across versions, and increment `packVersion` for every changed payload. Publish prereleases for experimental packs and explain the supported target build, known limitations, required Patchwork version, process names, source revision and exact restore procedure in the release notes.

See [the five fixture examples](https://github.com/MrCool-888/patchwork-patches/tree/main/examples) for runnable synthetic recipes. They demonstrate the format only; they do not target a real product.

For a PowerShell terminal, piping to `Out-Host` makes PowerShell wait for the GUI executable and capture its diagnostics. For scripted calls, use `Start-Process -Wait -PassThru` with redirected output, or a new `--report` file outside the target. Check the process exit code: 0 means success, 1 means failure.
