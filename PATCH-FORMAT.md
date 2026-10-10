# Patch file format (schema 1)

A separate UTF-8 `.json` or `.patchwork` file contains one bundle. **Add patch file** imports it without applying anything. Bundle IDs use lowercase letters, digits, and hyphens, are at most 80 characters, and identify saved definitions; reimporting an ID updates that definition. Files must be under 1 MB, with 1–100 patches and at most 100 operations per patch.

Required bundle fields: `schemaVersion` (1), `id`, `appId`, `appName`, `appVersion`, `versionFile`, `versionSha256`, and `patches`. Optional `author` and `source` identify provenance. `versionFile` and each operation's `file` are relative paths inside the selected target folder. `versionSha256` and `sha256` are full SHA-256 hashes of original bytes. Traversal, rooted paths, alternate data streams, symlinks/junctions, and network shares are rejected.

A patch needs `id`, `name`, `description`, `operations`, and optionally `category`, `dependencies` and `conflicts` (arrays of patch IDs). `status` defaults to `ready`, meaning its operations are available to apply; this is not a claim of live feature validation. `planned` patches have an empty operations array and are disabled.

For `appId: "proton-vpn"`, `versionFile` must be `ProtonVPN.Client.exe` and available operations must be managed edits. Compatible Proton definitions live in the separate patch repository; the patcher includes none.

Optional bundle `packVersion` and patch `version` use three nonnegative numbers, e.g. `1.1.0`. Missing patch versions inherit the pack version; files without either show **Unversioned**. Optional `minimumPatcherVersion` rejects imports into an older patcher (default `0.3.0`). Patchwork 0.4.0 displays these labels and freezes applied pack/patch versions plus a SHA-256 of the UTF-8 imported definition in each history journal. Keep a bundle ID stable across revisions to replace the imported definition. Patchwork 0.5.0 can update an applied session directly using its verified original backups. Select the desired patches and review the update preview; keep the original fingerprints and bundle ID stable.

## Text operations

`jsonSet` supports an existing scalar property in a `.json` object. Fields: `kind`, `file`, `sha256`, `path` (1–16 property names), `expected`, `value`. It rejects missing properties and unexpected values. Arrays are not traversed. JSON is formatted on write; restore preserves original bytes.

`textReplace` uses `kind`, `file`, `sha256`, `find`, `replacement`, and `count` (1–10000 exact ordinal matches). Supported extensions: `.json`, `.txt`, `.css`, `.xml`, `.ini`, `.yaml`, `.yml`, `.toml`, `.conf`, `.config`. UTF-8 BOMs are preserved.

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

Identical managed operations shared by two patches merge. Different replacements of one method or one call conflict. A full-body replacement conflicts with call edits in that method. Different call targets in one method can compose. Short branches are expanded before writing. No arbitrary IL, imported code, commands, executable payloads, or downloads are accepted.

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

## Conditional enumerable factories (0.6.0)

managedEnumerableFactory targets a parameterless instance method returning IEnumerable<TOut>. condition, source, filter, factory and argument are arrays of 1–8 exact field/getter signatures starting at this. The boolean condition retains the original implementation when true. Otherwise source must end with an accessible parameterless method returning IEnumerable<TIn>. The filter chain returns a string. elementGetter is a public string getter on TIn; its value is compared to the filter with ordinal case-insensitive equality. factoryMethod is a public instance method taking (TIn, bool) and returning a type assignable to TOut. The factory chain supplies its receiver, and argument supplies the boolean parameter. No additional parameters, index-aware delegates or value-type receiver chains are supported.

Fields must be accessible. The one exception is a sole condition member reading an inherited private boolean in the edited assembly; a same-assembly getter is added to that field's own type, with generic ancestors bound correctly. Typed private predicate/projector helpers and existing target-runtime LINQ references implement the operation. It conflicts with another full replacement of the same method. It accepts no scripts, IL or binary payloads.
