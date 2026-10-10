# Patch file format (schema 1)

A separate UTF-8 `.json` or `.patchwork` file contains one bundle. **Add patch file** imports it without applying anything. Bundle IDs use lowercase letters, digits, and hyphens, are at most 80 characters, and identify saved definitions; reimporting an ID updates that definition. Files must be under 1 MB, with 1–100 patches and at most 100 operations per patch.

Required bundle fields: `schemaVersion` (1), `id`, `appId`, `appName`, `appVersion`, `versionFile`, `versionSha256`, and `patches`. Optional `author` and `source` identify provenance. `versionFile` and each operation's `file` are relative paths inside the selected target folder. `versionSha256` and `sha256` are full SHA-256 hashes of original bytes. Traversal, rooted paths, alternate data streams, symlinks/junctions, and network shares are rejected.

A patch needs `id`, `name`, `description`, `operations`, and optionally `category`, `dependencies` and `conflicts` (arrays of patch IDs). `status` defaults to `ready`, meaning its operations are available to apply; this is not a claim of live feature validation. `planned` patches have an empty operations array and are disabled.

For `appId: "proton-vpn"`, `versionFile` must be `ProtonVPN.Client.exe` and available operations must be managed edits. Compatible Proton definitions live in the separate patch repository; the patcher includes none.

Optional bundle `packVersion` and patch `version` use three nonnegative numbers, e.g. `1.1.0`. Missing patch versions inherit the pack version; files without either show **Unversioned**. Optional `minimumPatcherVersion` rejects imports into an older patcher (default `0.3.0`). Patchwork 0.4.0 displays these labels and freezes applied pack/patch versions plus a SHA-256 of the UTF-8 imported definition in each history journal. Keep a bundle ID stable across revisions to replace the imported definition. Patchwork 0.5.0 can update an applied session directly using its verified original backups. Select the desired patches and review the update preview; keep the original fingerprints and bundle ID stable.

## Text operations

`jsonSet` supports an existing scalar property in a `.json` object. Fields: `kind`, `file`, `sha256`, `path` (1–16 property names), `expected`, `value`. It rejects missing properties and unexpected values. Arrays are not traversed. JSON is formatted on write; restore preserves original bytes.

`textReplace` uses `kind`, `file`, `sha256`, `find`, `replacement`, and `count` (1–10000 exact ordinal matches). Supported extensions: `.json`, `.txt`, `.css`, `.xml`, `.ini`, `.yaml`, `.yml`, `.toml`, `.conf`, `.config`. UTF-8 BOMs are preserved.

## Electron ASAR text operations (0.8.0)

`asarTextReplace` uses `kind`, `file`, original archive `sha256`, `entry`, original member `entrySha256`, nonempty `find`, `replacement`, and an explicit integer `count` (1–10000 exact ordinal matches). Set `minimumPatcherVersion: "0.8.0"`. The target must end in `.asar`; `entry` is a relative, case-sensitive path using forward slashes. Supported packed member extensions are `.js`, `.mjs`, `.json`, `.css`, `.html` and `.txt`. Linked and unpacked members cannot be edited. Rooted paths, traversal, empty segments and alternate data streams are rejected.

```json
{
  "kind": "asarTextReplace",
  "file": "resources/app.asar",
  "sha256": "FULL_64_CHARACTER_ORIGINAL_ARCHIVE_SHA256",
  "entry": "src/createWindow.js",
  "entrySha256": "FULL_64_CHARACTER_ORIGINAL_MEMBER_SHA256",
  "find": "const MIN_WIDTH = 1075;",
  "replacement": "const MIN_WIDTH = 940;",
  "count": 1
}
```

Archives are bounded to 64 MiB; ordinary text and managed files retain their 8 MiB limit. Edited members must be valid UTF-8 and at most 8 MiB before and after replacement. UTF-8 BOMs are preserved. Headers are bounded to 4 MiB, depth 32 and 50,000 tree nodes; aggregate packed member contents are bounded to 64 MiB. Integrity metadata must use SHA256 with a positive block size and no more than 65,536 blocks per member. Invalid Pickle headers, bounds, integrity hashes and overlapping payload regions are refused. Identical shared payload regions are accepted within these limits.

Every replacement is matched against the original member; different non-overlapping edits compose independently of selection order. Identical operations merge and overlapping edits conflict. The entire archive fingerprint and each edited member fingerprint must match; changing a version label does not establish compatibility.

Patchwork rebuilds in memory without extracting members to the target directory. Untouched packed contents and member metadata are retained, including unpacked and link metadata. Packed offsets and sizes are recalculated, edited members receive full/block integrity hashes, and the rebuilt archive is parsed and its contents verified. The external `.asar.unpacked` tree is not modified. Preview shows changed member paths, hashes and full before/after text. JavaScript edits are marked as executable client code: Patchwork does not evaluate them, but they run with the target app's permissions later.

Apply, selection/pack updates, failure rollback, interrupted recovery and restore use the existing durable original backups and journal transactions. Original bytes are retained; restore reproduces the exact original archive rather than reversing replacements. Close Blitz before apply, update or restore. A different original archive requires a matching pack. Some Electron apps enforce signed or embedded ASAR header hashes; `asarTextReplace` does not change that enforcement or the executable signature. Check the target's actual runtime before declaring compatibility.

## ASAR companion checksum (0.8.2)

`asarChecksum` updates the final four bytes of an exact-fingerprinted `.dat` companion file from a selected rebuilt archive. Set `minimumPatcherVersion: "0.8.2"`. Required fields are `kind`, companion `file`/`sha256`, `archive`/`archiveSha256`, `algorithm: "xxhash32"` (seed zero), and integer `offset`. Both paths follow the same safe-relative-path rules. The companion is limited to 16 MiB; the archive retains its 64 MiB limit. The offset must equal the companion's length minus four.

```json
{
  "kind": "asarChecksum",
  "file": "icudtl.dat",
  "sha256": "FULL_64_CHARACTER_ORIGINAL_COMPANION_SHA256",
  "archive": "resources/app.asar",
  "archiveSha256": "FULL_64_CHARACTER_ORIGINAL_ARCHIVE_SHA256",
  "algorithm": "xxhash32",
  "offset": 10468208
}
```

The selected operations must include text edits to that archive. Preview verifies both original fingerprints and the original little-endian footer, composes all archive edits first, then computes the new footer. Identical companion operations merge; conflicting declarations are refused. All preceding companion bytes remain unchanged. The binary preview shows the offset and before/after checksum values. Apply, update, rollback, recovery and byte-exact restore cover both files, including upgrades from older ASAR-only history. This supports Blitz's verified companion format; it does not disable a native check or modify executable signatures. XXH32 follows the [published specification](https://github.com/Cyan4973/xxHash/blob/dev/doc/xxhash_spec.md).

## Managed operations

Managed operations target IL-only `.dll` assemblies. Every operation needs `file`, `sha256`, and `method` with the **complete Cecil signature**, such as `System.Boolean Example.Widget::get_IsRestricted()`. A missing or ambiguous signature refuses preview. The preview shows affected method IL, and the generated assembly is re-read before any write.

- `managedReturn`: replaces a method body. `returnType` is `boolean`, `int32`, `enum`, `string`, `void`, or `timeSpanZero`. `value` must have the matching JSON scalar type; `void` and `timeSpanZero` omit it. Enum targets must resolve to an enum type.
- `managedBooleanCall`: replaces a specific parameterless boolean call's result, preserving evaluation of its receiver. Fields: `calledMethod` (complete signature), exact `count`, and boolean `value`.
- `managedSuppressCall`: suppresses a void call, preserving argument/receiver evaluation and consuming them correctly. Fields: `calledMethod` and exact `count`. Variable-argument, generic and prefixed calls are unsupported.
- `managedOverrideBoolean`: adds a boolean getter/method override to `type` (complete derived type name) with boolean `value`. `method` identifies an inherited, virtual, parameterless boolean method in the same assembly. The derived type must not already define that method.
- `managedOverrideBooleanArgument`: adds an override of an inherited virtual void method with one boolean parameter. It calls the base method with the specified boolean `value`. Fields include the complete derived `type` and base `method` signatures.

Additional operations in Patchwork 0.4.0:

- `managedConditionalCall`: when `condition` is false, redirects `calledMethod` to `replacementMethod`; otherwise preserves the original call. Both must be parameterless instance methods on the same receiver type with identical return types. Requires exact `count`.
- `managedConditionalBooleanCall`: preserves a parameterless boolean instance call when `condition` is true; otherwise consumes the receiver and returns boolean `value`. Requires `calledMethod` and exact `count`.
- `managedConditionalProjection`: preserves the original parameterless instance read-only-list getter when `condition` is true. Otherwise calls `sourceMethod` (another getter returning `IReadOnlyList<T>`) and projects its elements into the destination element class. `mappings` has 1–16 objects containing complete `getter` and `setter` signatures with matching types. The destination needs a public parameterless constructor. The backend generates and verifies a bounded LINQ projector; the file contains no executable payload.
- `managedOverrideBooleanSetter`: adds an inherited virtual void(bool) override on `type`. It forwards the original argument to the nearest inherited implementation, preserving child propagation, then invokes the boolean `setterMethod` on this object with `value`. The derived type must not already implement that method.

Conditional operations use `condition`, a chain of 1–8 complete instance field/property-getter signatures starting at `this` and ending in a boolean. Value-type getters are loaded by address. Each member and receiver type is checked. A false condition selects the alternate behavior; missing/null runtime dependencies are not fabricated. Use `minimumPatcherVersion: "0.4.0"` for packs containing these new operations.

Additional operation in Patchwork 0.4.2:

- `managedOverrideConditionalBooleanSetter`: adds an inherited virtual void(bool) override on `type`, forwards the original argument to the nearest inherited implementation, then invokes boolean `setterMethod` with `value` only when `condition` is true. When false, the inherited result is preserved. The bounded condition chain starts at the derived object. This supports enabling one category of rows while retaining other rows' original restrictions. Requires `minimumPatcherVersion: "0.4.2"`.

Example operation (replace the hash and signature with verified values):

```json
{
  "kind": "managedReturn",
  "file": "Example.Client.dll",
  "sha256": "FULL_64_CHARACTER_ORIGINAL_SHA256",
  "method": "System.Boolean Example.Widget::get_IsPromotionVisible()",
  "returnType": "boolean",
  "value": false
}
```

Identical managed operations shared by two patches merge. Different replacements of one method or one call conflict. A full-body replacement conflicts with call edits in that method. Different call targets in one method can compose. Short branches are expanded before writing. Declarative operations do not accept arbitrary IL, commands or downloads. The explicit managed client hook below accepts executable library code.

Restoration is based on verified original bytes, not inverse operations. Patchwork 0.5.0 updates a selection from verified backups without a manual restore, including removing edits no longer selected. An updated app needs a new matching pack; changing the version label or bypassing fingerprints is not a compatibility upgrade.

## GitHub patch sources (0.5.0)

Publish the separate JSON or .patchwork file as a GitHub release asset with a stable filename, a strictly increasing packVersion and GitHub SHA-256 metadata. The app checks the latest 100 releases, takes the newest eligible asset per filename, and supports at most 20 distinct patch files per source. Pre-release inclusion is selectable; drafts are excluded. Source ZIPs and repository files are ignored. No patch payload is bundled in Patchwork.

Source imports replace saved definitions only after integrity and format validation. Different sources cannot own the same bundle ID. Versions never downgrade automatically; changing contents under an existing version refuses replacement. Source checks update the library and do not apply target changes. Already-applied history keeps its original version until a reviewed patch transaction commits.

## Color options and WinUI resources (0.6.0)

A ready patch may have at most eight options, each with type=color, an ID, label (1–80 characters), and a default #RRGGBB value. An operation resource value such as $accent references that patch's option ID. Each option must be used. Submitted keys use patchId.optionId, must belong to selected patches, and values must be complete six-digit hex colors. Omitted values use defaults. Options are resolved on a copy; original source contents and hashes are retained. Preview plans, worker requests and journals record normalized choices.

managedThemeResources targets a parameterless instance void method that already merges Microsoft.UI.Xaml resource dictionaries. Its resources array has 1–100 entries with theme (Light or Dark), key (an ASCII letter followed by up to 79 letters, digits or underscores), type (color or brush), and value (#RRGGBB or $optionId). It generates only Color and SolidColorBrush elements and merges them on the method's normal exits. It accepts no XAML input, resource URIs, classes, event handlers, opacity expressions or arbitrary types. HighContrast cannot be supplied. Duplicate theme/key edits conflict. Distinct keys compose on the same hook. Use a hook before windows are built.

Example option and resource entries:

```json
"options": [{ "id": "accent", "label": "Accent", "type": "color", "default": "#8A66FF" }],
"resources": [{ "theme": "Dark", "key": "PrimaryColorBrush", "type": "brush", "value": "$accent" }]
```

## Presentation operations (0.6.1)

`managedEnumFilter` preserves a parameterless instance collection method and filters its returned List/IEnumerable/IReadOnlyList through a public enum `elementGetter` and declared integer `value`. Null collections remain null and null elements are excluded. An optional boolean `condition` chain preserves the original collection when true, for example for paid accounts. Original objects and source collections are retained.

`managedEnumGuardNull` accepts `values`, one to sixteen declared integer enum constants. It returns null for those values of a single enum argument, retaining the original reference-returning method for other values. Generic, value-type, pointer and array results are rejected.

`managedUiVisibility` appends a hide action to normal exits of an instance void hook. It targets the WinUI element itself or an exact same-type instance `field`. Hidden controls have Collapsed visibility, zero width/height and no hit target. With `fromParameter: true`, the hook must instead be a single boolean-argument method: true collapses the element and disables hit testing, false restores Visible and hit testing without altering its dimensions. Null fields are skipped. Receivers must expose the expected public WinUI UIElement/FrameworkElement setters.

`managedOverrideConditionalBooleanSetter` additionally supports `conditionExpected` (boolean, default true) and up to eight `exclusions` (boolean field/getter chains). Any true exclusion retains the inherited restriction. Conditions resolve inherited interface members and bind generic receiver types explicitly.

These presentation operations retain exact original file hashes, full method/member signatures, bounded values/chains and conflict checks. They accept no executable payloads.

## Conditional enumerable factories (0.6.0)

managedEnumerableFactory targets a parameterless instance method returning IEnumerable<TOut>. condition, source, filter, factory and argument are arrays of 1–8 exact field/getter signatures starting at this. The boolean condition retains the original implementation when true. Otherwise source must end with an accessible parameterless method returning IEnumerable<TIn>. The filter chain returns a string. elementGetter is a public string getter on TIn; its value is compared to the filter with ordinal case-insensitive equality. factoryMethod is a public instance method taking (TIn, bool) and returning a type assignable to TOut. The factory chain supplies its receiver, and argument supplies the boolean parameter. No additional parameters, index-aware delegates or value-type receiver chains are supported.

Fields must be accessible. The one exception is a sole condition member reading an inherited private boolean in the edited assembly; a same-assembly getter is added to that field's own type, with generic ancestors bound correctly. Typed private predicate/projector helpers and existing target-runtime LINQ references implement the operation. It conflicts with another full replacement of the same method. It accepts no scripts, IL or binary payloads.

## Managed client hooks (0.7.0)

`managedEmbeddedHook` is executable client code, explicitly identified in the library and preview. In addition to `file`, original `sha256` and full `method`, supply `moduleBase64`, exact `moduleSha256`, `entryType`, `entryMethod`, and `mode`. The payload must be an IL-only managed library, 512–262144 bytes, without a module initializer or application entry point. The entry is one public static `object Entry(object receiver, object[] arguments)` on a public non-generic class, without overloads or generic parameters.

`mode: "fallback"` calls the entry before an instance reference-returning method. A non-null result is cast and returned; null runs the original method. `mode: "after"` calls the entry at normal exits of an instance void method, including constructors, and ignores its result. Generic targets, value-type receivers, pointers, by-reference parameters and more than eight arguments are unsupported. Hooks conflict with other edits of the same method; identical hooks merge. A payload is embedded once per edited assembly and loaded lazily inside the target process.

Import, preview and apply validate metadata and bytes with Mono.Cecil, without loading or running client code in Patchwork. This is not a code sandbox or publisher signature: the library can use the target process's permissions, networking and native APIs. Authors should publish the corresponding source and reproducible build instructions. Exact original target fingerprints, transactional backups, direct updates and restore remain mandatory.
