# Patch file format (schema 1)

A separate UTF-8 `.json` or `.patchwork` file contains one bundle. **Add patch file** imports it without applying anything. Bundle IDs use lowercase letters, digits, and hyphens, are at most 80 characters, and identify saved definitions; reimporting an ID updates that definition. Files must be under 1 MB, with 1–100 patches and at most 100 operations per patch.

Required bundle fields: `schemaVersion` (1), `id`, `appId`, `appName`, `appVersion`, `versionFile`, `versionSha256`, and `patches`. Optional `author` and `source` identify provenance. `versionFile` and each operation's `file` are relative paths inside the selected target folder. `versionSha256` and `sha256` are full SHA-256 hashes of original bytes. Traversal, rooted paths, alternate data streams, symlinks/junctions, and network shares are rejected.

A patch needs `id`, `name`, `description`, `operations`, and optionally `category`, `dependencies` and `conflicts` (arrays of patch IDs). `status` defaults to `ready`, meaning its operations are available to apply; this is not a claim of live feature validation. `planned` patches have an empty operations array and are disabled.

For `appId: "proton-vpn"`, `versionFile` must be `ProtonVPN.Client.exe` and available operations must be managed edits. Compatible Proton definitions live in the separate patch repository; the patcher includes none.

Optional bundle `packVersion` and patch `version` use three nonnegative numbers, e.g. `1.1.0`. Missing patch versions inherit the pack version; files without either show **Unversioned**. Optional `minimumPatcherVersion` rejects imports into an older patcher (default `0.3.0`). Patchwork 0.4.0 displays these labels and freezes applied pack/patch versions plus a SHA-256 of the UTF-8 imported definition in each history journal. Keep a bundle ID stable across revisions to replace the imported definition. Restore an applied session before applying a newer pack.

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

Restoration is based on verified original bytes, not inverse operations. Restore before choosing a different patch selection. An updated app needs a new matching pack; changing the version label or bypassing fingerprints is not a compatibility upgrade.
