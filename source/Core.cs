using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Patchwork
{
    public static class Json
    {
        public static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 64 };
        public static Dictionary<string, object> Object(object value)
        {
            var result = value as Dictionary<string, object>;
            if (result == null) throw new InvalidDataException("Expected a JSON object.");
            return result;
        }
        public static string String(Dictionary<string, object> value, string key, string fallback = null)
        {
            object item;
            if (!value.TryGetValue(key, out item))
            {
                if (fallback != null) return fallback;
                throw new InvalidDataException("Missing field: " + key);
            }
            if (!(item is string)) throw new InvalidDataException("Expected text in " + key + ".");
            return (string)item;
        }
        public static List<object> Array(Dictionary<string, object> value, string key, bool optional = false)
        {
            object item;
            if (!value.TryGetValue(key, out item))
            {
                if (optional) return new List<object>();
                throw new InvalidDataException("Missing array: " + key);
            }
            var array = item as System.Collections.IEnumerable;
            if (array == null || item is string || item is Dictionary<string, object>) throw new InvalidDataException("Expected an array in " + key + ".");
            return array.Cast<object>().ToList();
        }
        public static Dictionary<string, object> Parse(string text) { return Object(Serializer.DeserializeObject(text)); }
        public static string Pretty(object value)
        {
            string compact = Serializer.Serialize(value);
            var output = new StringBuilder();
            bool quoted = false, escaped = false;
            int indent = 0;
            for (int i = 0; i < compact.Length; i++)
            {
                char c = compact[i];
                if (quoted)
                {
                    output.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') { quoted = true; output.Append(c); }
                else if (c == '{' || c == '[')
                {
                    output.Append(c);
                    if (i + 1 < compact.Length && (compact[i + 1] == '}' || compact[i + 1] == ']')) output.Append(compact[++i]);
                    else { indent++; output.Append("\r\n" + new string(' ', indent * 2)); }
                }
                else if (c == '}' || c == ']') { indent--; output.Append("\r\n" + new string(' ', indent * 2) + c); }
                else if (c == ',') output.Append(",\r\n" + new string(' ', indent * 2));
                else if (c == ':') output.Append(": ");
                else output.Append(c);
            }
            return output.ToString() + "\r\n";
        }
        public static bool Equal(object left, object right) { return Serializer.Serialize(left) == Serializer.Serialize(right); }
    }

    public class PatchOperation
    {
        public string Kind, File, Sha256, Find, Replacement, Method, CalledMethod, Type, ReturnType;
        public string Entry, EntrySha256;
        public string Archive, ArchiveSha256;
        public string BinaryData, ResultSha256;
        public List<BinaryEdit> BinaryEdits = new List<BinaryEdit>();
        public int Offset;
        public List<string> Path = new List<string>();
        public object Expected, Value;
        public int Count;
        public string ReplacementMethod, SourceMethod, SetterMethod;
        public List<string> Condition = new List<string>();
        public bool ConditionExpected = true;
        public List<List<string>> Exclusions = new List<List<string>>();
        public string UiField;
        public bool UiFromParameter;
        public List<int> EnumValues = new List<int>();
        public string ModuleData, ModuleSha256, EntryType, EntryMethod, HookMode;
        public List<Dictionary<string, string>> Mappings = new List<Dictionary<string, string>>();
        public List<string> SourceChain = new List<string>(), FilterChain = new List<string>(), FactoryChain = new List<string>(), ArgumentChain = new List<string>();
        public string ElementGetter, FactoryMethod;
        public List<ThemeResource> Resources = new List<ThemeResource>();
    }
    public class PatchDefinition
    {
        public string Id, Name, Description, Category, Status, Version;
        public List<string> Dependencies = new List<string>();
        public List<string> Conflicts = new List<string>();
        public List<PatchOperation> Operations = new List<PatchOperation>();
        public List<PatchColorOption> Options = new List<PatchColorOption>();
        public bool Ready { get { return Status == "ready"; } }
    }
    public class PatchBundle
    {
        public string Id, AppId, AppName, AppVersion, Author, Source, VersionFile, VersionSha256, PackVersion, MinimumPatcherVersion;
        [ScriptIgnore] public string Content;
        public List<TargetLocation> TargetLocations;
        public List<ProcessRule> Processes;
        public List<PatchDefinition> Patches = new List<PatchDefinition>();
        public static PatchBundle Parse(string content)
        {
            if (Encoding.UTF8.GetByteCount(content) > 1024 * 1024) throw new InvalidDataException("Patch bundles must be smaller than 1 MB.");
            var root = Json.Parse(content);
            object schema;
            if (!root.TryGetValue("schemaVersion", out schema) || Convert.ToInt32(schema) != 1) throw new InvalidDataException("Unsupported bundle schema. This app supports schema 1.");
            var bundle = new PatchBundle {
                Id = Json.String(root, "id"), AppId = Json.String(root, "appId"),
                AppName = Json.String(root, "appName"), AppVersion = Json.String(root, "appVersion"),
                Author = Json.String(root, "author", "Unknown author"), Source = Json.String(root, "source", "Local recipe"),
                VersionFile = Json.String(root, "versionFile"), VersionSha256 = Json.String(root, "versionSha256"), Content = content,
                PackVersion = ReadVersion(root, "packVersion", "Unversioned"), MinimumPatcherVersion = ReadVersion(root, "minimumPatcherVersion", "0.3.0")
            };
            if (System.Version.Parse(bundle.MinimumPatcherVersion) > System.Reflection.Assembly.GetExecutingAssembly().GetName().Version) throw new InvalidDataException("This patch pack requires Patchwork " + bundle.MinimumPatcherVersion + " or newer. Update the patcher first.");
            Targeting.Parse(bundle, root);
            ValidateId(bundle.Id); ValidateId(bundle.AppId); ValidateRelative(bundle.VersionFile); ValidateHash(bundle.VersionSha256);
            if (bundle.AppId == "proton-vpn" && bundle.VersionFile != "ProtonVPN.Client.exe") throw new InvalidDataException("Proton bundles must fingerprint ProtonVPN.Client.exe.");
            bool community = root.ContainsKey("targetLocations") || root.ContainsKey("processes");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (object entry in Json.Array(root, "patches"))
            {
                var raw = Json.Object(entry);
                var patch = new PatchDefinition { Id = Json.String(raw, "id"), Name = Json.String(raw, "name"), Description = Json.String(raw, "description"), Category = Json.String(raw, "category", "General"), Status = Json.String(raw, "status", "ready"), Version = ReadVersion(raw, "version", bundle.PackVersion) };
                if (patch.Status != "ready" && patch.Status != "planned") throw new InvalidDataException("Unknown patch status.");
                ValidateId(patch.Id);
                if (!ids.Add(patch.Id)) throw new InvalidDataException("Duplicate patch ID: " + patch.Id);
                patch.Dependencies = Strings(Json.Array(raw, "dependencies", true));
                patch.Conflicts = Strings(Json.Array(raw, "conflicts", true));
                patch.Options = ThemeOptions.ParseOptions(raw);
                int operationIndex = 0;
                foreach (object operation in Json.Array(raw, "operations"))
                {
                    operationIndex++;
                    try
                    {
                    var opRaw = Json.Object(operation);
                    var op = new PatchOperation { Kind = Json.String(opRaw, "kind"), File = Json.String(opRaw, "file") };
                    ValidateRelative(op.File);
                    if (op.Kind == "fileAdd") { if (opRaw.ContainsKey("sha256")) throw new InvalidDataException("fileAdd requires an absent original; omit sha256."); }
                    else { op.Sha256 = Json.String(opRaw, "sha256"); ValidateHash(op.Sha256); }
                    bool managed = op.Kind.StartsWith("managed", StringComparison.Ordinal);
                    bool asar = AsarPatches.IsOperation(op.Kind);
                    bool checksum = op.Kind == "asarChecksum";
                    bool binary = BinaryPatches.IsOperation(op.Kind);
                    if (managed) ManagedPatches.Parse(op, opRaw);
                    else if (asar) AsarPatches.Parse(op, opRaw);
                    else if (checksum) AsarChecksum.Parse(op, opRaw);
                    else if (binary) BinaryPatches.Parse(op, opRaw);
                    community |= op.Kind == "fileAdd" || op.Kind == "fileReplace" || op.Kind.StartsWith("zip", StringComparison.Ordinal) || op.Kind == "asarEntryAdd" || op.Kind == "asarEntryReplace" ||
                        op.Kind == "binarySplice" && !op.File.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || managed && op.File.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || op.Kind == "textReplace" && !AllowedExtension(op.File);
                    if (managed || asar || checksum || binary) { }
                    else if (op.Kind == "jsonSet")
                    {
                        if (!op.File.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("jsonSet requires a .json file.");
                        op.Path = Strings(Json.Array(opRaw, "path"));
                        if (op.Path.Count == 0 || op.Path.Count > 16 || op.Path.Any(String.IsNullOrWhiteSpace)) throw new InvalidDataException("JSON paths must contain 1 to 16 property names.");
                        if (!opRaw.TryGetValue("expected", out op.Expected) || !opRaw.TryGetValue("value", out op.Value)) throw new InvalidDataException("jsonSet requires expected and value fields.");
                        if (!Scalar(op.Expected) || !Scalar(op.Value)) throw new InvalidDataException("jsonSet supports scalar values only.");
                    }
                    else if (op.Kind == "textReplace")
                    {
                        op.Find = Json.String(opRaw, "find"); op.Replacement = Json.String(opRaw, "replacement");
                        object count;
                        if (!opRaw.TryGetValue("count", out count) || !(count is int)) throw new InvalidDataException("textReplace requires an exact integer count.");
                        op.Count = (int)count;
                        if (op.Find.Length == 0 || op.Count < 1 || op.Count > 10000) throw new InvalidDataException("Invalid text replacement or match count.");
                    }
                    else throw new InvalidDataException("Unknown operation: " + op.Kind);
                    patch.Operations.Add(op);
                    }
                    catch (Exception error) { throw new InvalidDataException("Patch " + patch.Id + ", operation " + operationIndex + ": " + error.Message, error); }
                }
                if (patch.Ready && patch.Operations.Count == 0 || patch.Operations.Count > 100 || !patch.Ready && patch.Operations.Count != 0) throw new InvalidDataException("Available patches need 1 to 100 operations; planned patches must have none.");
                ThemeOptions.Validate(patch);
                bundle.Patches.Add(patch);
            }
            if (bundle.Patches.Count == 0 || bundle.Patches.Count > 100) throw new InvalidDataException("Bundles need 1 to 100 patches.");
            foreach (var patch in bundle.Patches)
                foreach (string id in patch.Dependencies.Concat(patch.Conflicts))
                    if (!ids.Contains(id) || id == patch.Id) throw new InvalidDataException("Invalid dependency or conflict: " + id);
            if (bundle.AppId == "proton-vpn" && bundle.Patches.SelectMany(x => x.Operations).Any(x => !x.Kind.StartsWith("managed", StringComparison.Ordinal))) throw new InvalidDataException("Proton bundles require managed operations.");
            if (community && System.Version.Parse(bundle.MinimumPatcherVersion) < new System.Version(0, 10, 0)) throw new InvalidDataException("Community capabilities require minimumPatcherVersion: 0.10.0 or newer.");
            return bundle;
        }
        static bool Scalar(object value) { return value == null || value is string || value is bool || value is int || value is long || value is decimal || value is double; }
        static string ReadVersion(Dictionary<string, object> raw, string key, string fallback)
        {
            if (!raw.ContainsKey(key)) return fallback;
            string value = Json.String(raw, key); System.Version parsed;
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^(0|[1-9][0-9]{0,5})\\.(0|[1-9][0-9]{0,5})\\.(0|[1-9][0-9]{0,5})\\z") || !System.Version.TryParse(value, out parsed)) throw new InvalidDataException("Use a three-part version number in " + key + ", such as 1.1.0.");
            return value;
        }
        static List<string> Strings(List<object> input)
        {
            if (input.Any(x => !(x is string))) throw new InvalidDataException("Expected an array of strings.");
            return input.Cast<string>().ToList();
        }
        internal static void ValidateId(string id)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{0,79}$")) throw new InvalidDataException("IDs must use lowercase letters, numbers, and hyphens.");
        }
        public static void ValidateHash(string hash)
        {
            if (hash == null || !System.Text.RegularExpressions.Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("A full SHA-256 fingerprint is required.");
        }
        public static void ValidateRelative(string path)
        {
            ValidateRelative(path, true);
        }
        public static void ValidateArchiveRelative(string path)
        {
            ValidateRelative(path, false);
        }
        static void ValidateRelative(string path, bool fileSystem)
        {
            if (path != null && path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0) throw new InvalidDataException("Unsafe file path: " + path);
            if (String.IsNullOrWhiteSpace(path) || System.IO.Path.IsPathRooted(path) || path.IndexOf(':') >= 0 || path.IndexOf('\0') >= 0)
                throw new InvalidDataException("Use a relative file path: " + path);
            foreach (string part in path.Replace('/', '\\').Split('\\'))
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") || fileSystem && (part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
                    System.Text.RegularExpressions.Regex.IsMatch(part, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))) throw new InvalidDataException("Unsafe file path: " + path);
        }
        static bool AllowedExtension(string path)
        {
            return new[] { ".json", ".txt", ".css", ".xml", ".ini", ".yaml", ".yml", ".toml", ".conf", ".config", ".js" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
        }
    }

    public class FileChange
    {
        public string RelativePath, BeforeHash, AfterHash, BeforeText, AfterText;
        public byte[] BeforeBytes, AfterBytes, OriginalBeforeBytes;
        public string OriginalBeforeHash;
        public string TransformKind;
        public int SizeLimit;
        public bool BeforeExists = true, AfterExists = true, OriginalBeforeExists = true;
        public List<string> Details = new List<string>();
        [ScriptIgnore] public List<PatchOperation> ManagedOperations = new List<PatchOperation>();
        [ScriptIgnore] public List<PatchOperation> AsarOperations = new List<PatchOperation>();
        [ScriptIgnore] public PatchOperation ChecksumOperation;
        [ScriptIgnore] public List<PatchOperation> BinaryOperations = new List<PatchOperation>();
    }
    public class PatchPlan
    {
        public Dictionary<string, string> Options = new Dictionary<string, string>();
        public string TargetRoot, AppName, AppVersion, BundleId, VersionFile, VersionSha256, PackVersion, BundleSha256, VersionCurrentSha256, PreviousJournalId;
        public int VersionSizeLimit;
        public List<ProcessRule> Processes;
        public DateTime CreatedUtc;
        public List<string> PatchIds = new List<string>();
        public List<string> PatchNames = new List<string>();
        public List<FileChange> Files = new List<FileChange>();
    }
    public class JournalFile
    {
        public JournalFile() { BeforeExists = AfterExists = PreviousExists = true; }
        public bool BeforeExists { get; set; }
        public bool AfterExists { get; set; }
        public bool PreviousExists { get; set; }
        public string TransformKind { get; set; }
        public int SizeLimit { get; set; }
        public string RelativePath { get; set; }
        public string BeforeHash { get; set; }
        public string AfterHash { get; set; }
        public string BackupFile { get; set; }
        public string PreviousHash { get; set; }
        public string PreviousBackupFile { get; set; }
    }
    public class Journal
    {
        public Dictionary<string, string> Options { get; set; }
        public string Id { get; set; }
        public string AppName { get; set; }
        public string AppVersion { get; set; }
        public string BundleId { get; set; }
        public string PackVersion { get; set; }
        public string BundleSha256 { get; set; }
        public string TargetRoot { get; set; }
        public string CreatedUtc { get; set; }
        public string State { get; set; }
        public string Error { get; set; }
        public List<string> PatchNames { get; set; }
        public List<string> PatchIds { get; set; }
        public string PreviousJournalId { get; set; }
        public string SupersededBy { get; set; }
        public bool RestoreToPrevious { get; set; }
        public List<JournalFile> Files { get; set; }
        public List<ProcessRule> Processes { get; set; }
        public List<string> CreatedDirectories { get; set; }
        [ScriptIgnore] public string DirectoryPath { get; set; }
    }

    public partial class PatchEngine
    {
        public readonly string DataRoot;
        readonly object transactionLock = new object();
#if TEST_BUILD
        public Action<int> BeforeWriteForTest;
#endif
        public PatchEngine(string dataRoot) { DataRoot = Path.GetFullPath(dataRoot); Directory.CreateDirectory(DataRoot); }
        public static string Hash(byte[] bytes)
        {
            if (bytes == null) return null;
            using (var algorithm = SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        public static byte[] Read(string file)
        {
            return Read(file, TargetLimit(file));
        }
        static int TargetLimit(string file) { return file.EndsWith(".asar", StringComparison.OrdinalIgnoreCase) ? AsarPatches.MaximumSize : file.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ? AsarChecksum.MaximumSize : file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? JarPatches.MaximumSize : file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? BinaryPatches.MaximumSize : file.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? 16 * 1024 * 1024 : 8 * 1024 * 1024; }
        static byte[] ReadBackup(string file, string relative) { return Read(file, TargetLimit(relative)); }
        public static byte[] Read(string file, int maximum)
        {
            var info = new FileInfo(file);
            if (!info.Exists) throw new FileNotFoundException("Required file is missing: " + file);
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > maximum) throw new InvalidDataException("File exceeds " + (maximum / 1024 / 1024) + " MB: " + file);
                var bytes = new byte[(int)stream.Length]; int offset = 0, count;
                while (offset < bytes.Length && (count = stream.Read(bytes, offset, bytes.Length - offset)) > 0) offset += count;
                if (offset != bytes.Length || stream.ReadByte() != -1) throw new IOException("File changed while reading: " + file);
                return bytes;
            }
        }
        public static string Decode(byte[] bytes)
        {
            int offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            string text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            if (text.IndexOf('\0') >= 0) throw new InvalidDataException("Only UTF-8 text files are supported.");
            return text;
        }
        static byte[] Encode(string text, byte[] original, string relative)
        {
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
            if (original.Length >= 3 && original[0] == 0xef && original[1] == 0xbb && original[2] == 0xbf)
                bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(bytes).ToArray();
            if (bytes.Length > TargetLimit(relative)) throw new InvalidDataException("The patched file would exceed " + (TargetLimit(relative) / 1024 / 1024) + " MB.");
            return bytes;
        }
        public static string Resolve(string root, string relative)
        {
            PatchBundle.ValidateRelative(relative);
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (root.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException("Choose a local folder, not a network share.");
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\')));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("File escapes the selected folder.");
            string current = full;
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Symlinks and directory junctions are not supported: " + current);
                string parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
            return full;
        }
        PatchPlan PreviewOriginal(PatchBundle bundle, string root, IEnumerable<string> selection, Dictionary<string, byte[]> originals)
        {
            lock (transactionLock)
            {
                if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Choose an existing target folder.");
                root = Path.GetFullPath(root);
                var ids = new HashSet<string>(selection, StringComparer.Ordinal);
                if (ids.Count == 0) throw new InvalidOperationException("Select at least one ready patch.");
                var selected = bundle.Patches.Where(x => ids.Contains(x.Id)).ToList();
                if (selected.Count != ids.Count || selected.Any(x => !x.Ready)) throw new InvalidOperationException("A selected patch is unavailable.");
                foreach (var patch in selected)
                {
                    if (patch.Dependencies.Any(x => !ids.Contains(x))) throw new InvalidOperationException(patch.Name + " requires: " + String.Join(", ", patch.Dependencies.Where(x => !ids.Contains(x))));
                    if (patch.Conflicts.Any(ids.Contains)) throw new InvalidOperationException("Conflicting patch selection: " + patch.Name);
                }
                string versionPath = Resolve(root, bundle.VersionFile);
                if (!String.Equals(Hash(ReadOriginal(versionPath, originals, VersionLimit(bundle))), bundle.VersionSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Target fingerprint does not match " + bundle.AppName + " " + bundle.AppVersion + ".");
                var plan = new PatchPlan { TargetRoot = root, AppName = bundle.AppName, AppVersion = bundle.AppVersion, BundleId = bundle.Id, PackVersion = bundle.PackVersion, BundleSha256 = Hash(Encoding.UTF8.GetBytes(bundle.Content)), VersionFile = bundle.VersionFile, VersionSha256 = bundle.VersionSha256, VersionCurrentSha256 = Hash(Read(versionPath, VersionLimit(bundle))), VersionSizeLimit = VersionLimit(bundle), Processes = bundle.Processes, CreatedUtc = DateTime.UtcNow, PatchIds = selected.Select(x => x.Id).ToList(), PatchNames = selected.Select(x => x.Name + (x.Version == "Unversioned" ? " (unversioned)" : " (v" + x.Version + ")")).ToList() };
                var files = new Dictionary<string, FileChange>(StringComparer.OrdinalIgnoreCase);
                var jsonTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var patch in selected)
                    foreach (var op in patch.Operations)
                    {
                        string full = Resolve(root, op.File);
                        FileChange change;
                        if (!files.TryGetValue(full, out change))
                        {
                            string kind = FileFormats.Kind(op); int limit = FileFormats.Limit(kind, op.File);
                            byte[] before = op.Kind == "fileAdd" ? ReadOptionalOriginal(full, originals, limit) : ReadOriginal(full, originals, limit);
                            change = new FileChange { RelativePath = op.File, TransformKind = kind, SizeLimit = limit, BeforeBytes = before, BeforeExists = before != null, BeforeHash = Hash(before), BeforeText = kind == "text" ? Decode(before) : "" };
                            change.AfterText = change.BeforeText;
                            files.Add(full, change);
                        }
                        if (change.TransformKind != FileFormats.Kind(op)) throw new InvalidDataException("Incompatible transformations for " + op.File);
                        if (op.Kind == "fileAdd" && change.BeforeExists) throw new InvalidOperationException("Added file already exists: " + op.File);
                        if (!String.Equals(change.BeforeHash, op.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsupported or already modified file: " + op.File + ". Restore the original or use a matching recipe.");
                        if (op.Kind.StartsWith("managed", StringComparison.Ordinal))
                        {
                            change.ManagedOperations.Add(op);
                            change.Details.Add(patch.Name + ": " + op.Kind + " · " + op.Method);
                        }
                        else if (AsarPatches.IsOperation(op.Kind))
                        {
                            change.AsarOperations.Add(op);
                            change.Details.Add(patch.Name + ": executable ASAR text edit · " + op.Entry);
                        }
                        else if (op.Kind == "asarChecksum")
                        {
                            if (change.ChecksumOperation != null && (change.ChecksumOperation.Archive != op.Archive || change.ChecksumOperation.ArchiveSha256 != op.ArchiveSha256 || change.ChecksumOperation.Offset != op.Offset)) throw new InvalidOperationException("Conflicting ASAR companion checksums.");
                            change.ChecksumOperation = op;
                            change.Details.Add(patch.Name + ": update four-byte ASAR checksum · " + op.Archive);
                        }
                        else if (BinaryPatches.IsOperation(op.Kind))
                        {
                            change.BinaryOperations.Add(op);
                            change.Details.Add(patch.Name + ": executable binary edit · " + (op.Entry ?? op.File));
                        }
                        else if (op.Kind == "jsonSet")
                        {
                            string key = full + "|" + Json.Serializer.Serialize(op.Path);
                            if (!jsonTargets.Add(key)) throw new InvalidOperationException("Two operations modify the same JSON property: " + String.Join(".", op.Path));
                            var document = Json.Parse(change.AfterText);
                            var cursor = document;
                            for (int i = 0; i < op.Path.Count - 1; i++)
                            {
                                object next;
                                if (!cursor.TryGetValue(op.Path[i], out next)) throw new InvalidOperationException("Missing JSON property: " + String.Join(".", op.Path));
                                cursor = Json.Object(next);
                            }
                            string leaf = op.Path.Last(); object existing;
                            if (!cursor.TryGetValue(leaf, out existing) || !Json.Equal(existing, op.Expected)) throw new InvalidOperationException("Unexpected value at " + String.Join(".", op.Path) + ".");
                            cursor[leaf] = op.Value;
                            change.AfterText = Json.Pretty(document);
                            change.Details.Add(patch.Name + ": " + String.Join(".", op.Path) + "  " + Json.Serializer.Serialize(existing) + " → " + Json.Serializer.Serialize(op.Value));
                        }
                        else
                        {
                            int count = 0, offset = 0;
                            while ((offset = change.AfterText.IndexOf(op.Find, offset, StringComparison.Ordinal)) >= 0) { count++; offset += op.Find.Length; }
                            if (count != op.Count) throw new InvalidOperationException("Expected " + op.Count + " text matches in " + op.File + "; found " + count + ".");
                            change.AfterText = change.AfterText.Replace(op.Find, op.Replacement);
                            change.Details.Add(patch.Name + ": replace " + count + " exact text match(es)");
                        }
                    }
                foreach (var change in files.Values.OrderBy(x => x.ChecksumOperation != null))
                {
                    if (change.ManagedOperations.Count > 0)
                    {
                        string beforeText, afterText;
                        change.AfterBytes = ManagedPatches.Transform(change.BeforeBytes, root, change.ManagedOperations, out beforeText, out afterText);
                        change.BeforeText = beforeText; change.AfterText = afterText;
                        foreach (var client in change.ManagedOperations.Where(x => x.Kind == "managedEmbeddedHook").GroupBy(x => x.ModuleSha256).Select(x => x.First()))
                            change.AfterText += "\nExecutable client module SHA-256: " + client.ModuleSha256 + "\nEntry type: " + client.EntryType + "\nRuns inside the target app when its hooked methods execute. Review its source before applying.\n";
                    }
                    else if (change.AsarOperations.Count > 0)
                    {
                        string beforeText, afterText;
                        change.AfterBytes = AsarPatches.Transform(change.BeforeBytes, change.AsarOperations, out beforeText, out afterText);
                        change.BeforeText = beforeText; change.AfterText = afterText;
                    }
                    else if (change.ChecksumOperation != null)
                    {
                        FileChange archive;
                        if (!files.TryGetValue(Resolve(root, change.ChecksumOperation.Archive), out archive) || archive.AsarOperations.Count == 0 || archive.AfterBytes == null) throw new InvalidDataException("A companion checksum requires selected edits to its archive.");
                        change.AfterBytes = AsarChecksum.Transform(change.BeforeBytes, archive, change.ChecksumOperation);
                        change.BeforeText = AsarChecksum.Describe(change.BeforeBytes); change.AfterText = AsarChecksum.Describe(change.AfterBytes);
                    }
                    else if (change.BinaryOperations.Count > 0)
                    {
                        string beforeText, afterText;
                        change.AfterBytes = change.TransformKind == "zip" ? JarPatches.Transform(change.BeforeBytes, change.BinaryOperations, out beforeText, out afterText) : BinaryPatches.Transform(change.BeforeBytes, change.BinaryOperations, out beforeText, out afterText);
                        change.BeforeText = beforeText; change.AfterText = afterText;
                    }
                    else change.AfterBytes = Encode(change.AfterText, change.BeforeBytes, change.RelativePath);
                    if (change.AfterBytes.Length > change.SizeLimit) throw new InvalidDataException("Transformed file exceeds its size limit: " + change.RelativePath);
                    change.AfterHash = Hash(change.AfterBytes);
                    if (change.AfterHash != change.BeforeHash) plan.Files.Add(change);
                }
                if (plan.Files.Count == 0) throw new InvalidOperationException("These patches make no changes.");
                return plan;
            }
        }
        public Journal Apply(PatchPlan plan)
        {
            Worker.CheckClientClosed(plan.TargetRoot, plan.Processes);
            lock (transactionLock)
            using (AcquireTransaction())
            {
                EnsureNoRecoveryPending(plan.TargetRoot);
                var previous = ActiveSession(plan.TargetRoot);
                if ((previous == null ? null : previous.Id) != plan.PreviousJournalId) throw new InvalidOperationException("The applied session changed after preview. Preview again.");
                if (previous != null) VerifyOriginals(previous);
                if (!Hash(Read(Resolve(plan.TargetRoot, plan.VersionFile), plan.VersionSizeLimit == 0 ? TargetLimit(plan.VersionFile) : plan.VersionSizeLimit)).Equals(plan.VersionCurrentSha256 ?? plan.VersionSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The target version changed after preview. Preview again.");
                foreach (var change in plan.Files)
                    if (Hash(ReadState(Resolve(plan.TargetRoot, change.RelativePath), change.SizeLimit)) != change.BeforeHash) throw new InvalidOperationException("A file changed after preview: " + change.RelativePath + ". Preview again.");
                var journal = new Journal {
                    Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8), AppName = plan.AppName, AppVersion = plan.AppVersion,
                    BundleId = plan.BundleId, PackVersion = plan.PackVersion, BundleSha256 = plan.BundleSha256, TargetRoot = plan.TargetRoot, CreatedUtc = DateTime.UtcNow.ToString("o"), State = "Prepared", Error = "", PatchNames = plan.PatchNames.ToList(), PatchIds = plan.PatchIds.ToList(), Options = new Dictionary<string, string>(plan.Options), PreviousJournalId = plan.PreviousJournalId, RestoreToPrevious = previous != null, Processes = plan.Processes, CreatedDirectories = previous == null || previous.CreatedDirectories == null ? new List<string>() : previous.CreatedDirectories.ToList(), Files = new List<JournalFile>()
                };
                journal.DirectoryPath = Path.Combine(DataRoot, "history", journal.Id);
                Directory.CreateDirectory(journal.DirectoryPath);
                for (int i = 0; i < plan.Files.Count; i++)
                {
                    var change = plan.Files[i];
                    string backup = i.ToString("D3") + ".original";
                    byte[] original = previous == null ? change.BeforeBytes : change.OriginalBeforeBytes;
                    string originalHash = previous == null ? change.BeforeHash : change.OriginalBeforeHash;
                    bool originalExists = previous == null ? change.BeforeExists : change.OriginalBeforeExists;
                    VerifyBytes(original, originalHash, originalExists); VerifyBytes(change.BeforeBytes, change.BeforeHash, change.BeforeExists); VerifyBytes(change.AfterBytes, change.AfterHash, change.AfterExists);
                    if (originalExists) DurableWrite(Path.Combine(journal.DirectoryPath, backup), original);
                    var entry = new JournalFile { RelativePath = change.RelativePath, TransformKind = change.TransformKind, SizeLimit = change.SizeLimit, BeforeExists = originalExists, AfterExists = change.AfterExists, BeforeHash = originalHash, AfterHash = change.AfterHash, BackupFile = originalExists ? backup : null };
                    if (previous != null) { entry.PreviousExists = change.BeforeExists; entry.PreviousHash = change.BeforeHash; entry.PreviousBackupFile = change.BeforeExists ? i.ToString("D3") + ".previous" : null; if (change.BeforeExists) DurableWrite(Path.Combine(journal.DirectoryPath, entry.PreviousBackupFile), change.BeforeBytes); }
                    journal.Files.Add(entry);
                }
                Save(journal);
                try
                {
                    for (int i = 0; i < plan.Files.Count; i++)
                    {
#if TEST_BUILD
                        if (BeforeWriteForTest != null) BeforeWriteForTest(i);
#endif
                        var change = plan.Files[i];
                        if (change.BeforeHash == change.AfterHash) continue;
                        string destination = Resolve(plan.TargetRoot, change.RelativePath);
                        EnsureParentDirectories(plan.TargetRoot, destination, journal);
                        WriteState(destination, change.AfterBytes, change.BeforeHash, change.SizeLimit);
                        if (Hash(ReadState(destination, change.SizeLimit)) != change.AfterHash) throw new IOException("Verification failed after writing " + change.RelativePath);
                    }
                    journal.State = "Applied"; journal.RestoreToPrevious = false; Save(journal);
                    if (previous != null) { previous.State = "Superseded"; previous.SupersededBy = journal.Id; Save(previous); }
                    return journal;
                }
                catch (Exception error)
                {
                    journal.Error = error.Message;
                    journal.State = "RecoveryRequired"; journal.RestoreToPrevious = previous != null; Save(journal);
                    try { if (previous != null) RestorePreviousInternal(journal); else { RestoreInternal(journal, true); journal.State = "RolledBack"; Save(journal); } }
                    catch (Exception rollback) { journal.Error += " Recovery: " + rollback.Message; Save(journal); }
                    throw new IOException("Apply failed. " + (journal.State == "RolledBack" ? previous == null ? "Original files were restored. " : "The previous patch version was restored. " : "Review the recovery entry in History. ") + error.Message, error);
                }
            }
        }
        public void Restore(Journal journal)
        {
            lock (transactionLock)
            using (AcquireTransaction())
            {
                journal = History().Single(x => x.Id == journal.Id);
                Worker.CheckClientClosed(journal.TargetRoot, journal.Processes);
                if (journal.State == "Superseded") throw new InvalidOperationException("A newer patch session replaced this one. Restore the current session instead.");
                if (History().Any(x => x.Id != journal.Id && SameRoot(x.TargetRoot, journal.TargetRoot) && (x.State == "Prepared" || x.State == "Restoring" || x.State == "RecoveryRequired"))) throw new InvalidOperationException("Recover the unfinished transaction for this folder first.");
                if (journal.State != "Applied" && journal.State != "Prepared" && journal.State != "RecoveryRequired" && journal.State != "Restoring") throw new InvalidOperationException("This backup has already been restored or rolled back.");
                if (journal.RestoreToPrevious) RestorePreviousInternal(journal);
                else RestoreInternal(journal, journal.State != "Applied");
            }
        }
        void RestoreInternal(Journal journal, bool recovery)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in journal.Files)
            {
                string destination = Resolve(journal.TargetRoot, entry.RelativePath);
                if (!paths.Add(destination)) throw new InvalidDataException("Duplicate backup file entry.");
                VerifyJournalState(entry.BeforeExists, entry.BeforeHash); VerifyJournalState(entry.AfterExists, entry.AfterHash);
                ReadJournalBackup(journal, entry, false);
                string current = Hash(ReadState(destination, FileFormats.TrackedLimit(entry)));
                if (current != entry.AfterHash && (!recovery || current != entry.BeforeHash)) throw new InvalidOperationException("Restore blocked: " + entry.RelativePath + " was changed outside Patchwork. No files were restored.");
            }
            journal.State = "Restoring"; Save(journal);
            try
            {
                foreach (var entry in journal.Files)
                {
                    string destination = Resolve(journal.TargetRoot, entry.RelativePath);
                    int limit = FileFormats.TrackedLimit(entry);
                    string current = Hash(ReadState(destination, limit));
                    if (current == entry.BeforeHash) continue;
                    if (current != entry.AfterHash) throw new InvalidOperationException("File changed during restore: " + entry.RelativePath);
                    WriteState(destination, ReadJournalBackup(journal, entry, false), entry.AfterHash, limit);
                    if (Hash(ReadState(destination, limit)) != entry.BeforeHash) throw new IOException("Restore verification failed: " + entry.RelativePath);
                }
                CleanupDirectories(journal);
                journal.State = "Restored"; Save(journal);
            }
            catch (Exception error) { journal.State = "RecoveryRequired"; journal.Error = error.Message; Save(journal); throw; }
        }
        public List<Journal> History()
        {
            lock (transactionLock)
            {
                string history = Path.Combine(DataRoot, "history");
                var result = new List<Journal>();
                if (!Directory.Exists(history)) return result;
                foreach (string directory in Directory.GetDirectories(history))
                {
                    try
                    {
                        string path = Resolve(directory, "journal.json");
                        var journal = Json.Serializer.Deserialize<Journal>(Decode(Read(path)));
                        if (journal == null || journal.Files == null || journal.PatchNames == null || journal.Id != Path.GetFileName(directory)) continue;
                        journal.DirectoryPath = directory;
                        result.Add(journal);
                    }
                    catch { /* Ignore unreadable entries; do not mutate an unknown journal. */ }
                }
                // Also derive predecessor state if a process stopped after committing the child journal.
                foreach (var child in result.Where(x => !String.IsNullOrEmpty(x.PreviousJournalId) && !x.RestoreToPrevious && (x.State == "Applied" || x.State == "Restored" || x.State == "Restoring" || x.State == "RecoveryRequired")))
                {
                    var parent = result.SingleOrDefault(x => x.Id == child.PreviousJournalId);
                    if (parent != null && parent.State == "Applied") { parent.State = "Superseded"; parent.SupersededBy = child.Id; }
                }
                return result.OrderByDescending(x => x.CreatedUtc, StringComparer.Ordinal).ToList();
            }
        }
        void EnsureNoRecoveryPending(string root)
        {
            if (History().Any(x => String.Equals(x.TargetRoot, root, StringComparison.OrdinalIgnoreCase) && (x.State == "Prepared" || x.State == "RecoveryRequired" || x.State == "Restoring")))
                throw new InvalidOperationException("This folder has an unfinished transaction. Open History and restore it before applying another patch.");
        }
        IDisposable AcquireTransaction()
        {
            try { return new FileStream(Path.Combine(DataRoot, "transaction.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new InvalidOperationException("Another Patchwork instance is applying or restoring patches. Try again after it finishes."); }
        }
        static void DurableWrite(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
        static void AtomicReplace(string destination, byte[] bytes, string expectedHash)
        {
            string temporary = destination + ".patchwork-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                DurableWrite(temporary, bytes);
                if (Hash(Read(destination)) != expectedHash) throw new IOException("File changed before replacement: " + destination);
                File.Replace(temporary, destination, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        void Save(Journal journal)
        {
            string path = Path.Combine(journal.DirectoryPath, "journal.json");
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Pretty(journal));
            if (File.Exists(path)) AtomicReplace(path, bytes, Hash(Read(path)));
            else DurableWrite(path, bytes);
        }
    }
}
