using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Patchwork
{
    public sealed class SourcePack
    {
        public string BundleId, Version, AssetName, AssetDigest, ContentHash, ReleaseUrl;
    }
    public sealed class PatchSource
    {
        public string Repository, LastCheckUtc, Status;
        public bool Automatic = true, IncludePrereleases;
        public List<SourcePack> Packs = new List<SourcePack>();
        public string Link { get { return "https://github.com/" + Repository; } }
    }
    public sealed class PatchAsset
    {
        public string Name, Url, Sha256, ReleaseUrl;
        public long Size;
        public DateTime PublishedUtc;
    }
    public sealed class SourceDownload
    {
        public string Repository;
        public bool IncludePrereleases;
        public List<Tuple<PatchAsset, PatchBundle>> Packs = new List<Tuple<PatchAsset, PatchBundle>>();
    }
    public static class PatchSources
    {
        public const int MaximumPack = 1024 * 1024;
        public static string Repository(string link)
        {
            Uri url;
            if (link == null || link.Length > 2048 || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out url) ||
                url.Scheme != "https" || url.Host != "github.com" || url.Port != 443 || url.UserInfo.Length != 0 || url.Query.Length != 0)
                throw new InvalidDataException("Paste an HTTPS github.com repository or release link without a query string.");
            var parts = url.AbsolutePath.Trim('/').Split('/');
            if (parts.Length < 2) throw new InvalidDataException("The link needs a GitHub owner and repository.");
            string owner = parts[0], repo = parts[1];
            if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo.Substring(0, repo.Length - 4);
            if (!Regex.IsMatch(owner, @"^[A-Za-z0-9][A-Za-z0-9-]{0,38}$") || !Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]{1,100}$") || repo == "." || repo == "..")
                throw new InvalidDataException("Invalid GitHub repository link.");
            if (parts.Length > 2 && !new[] { "releases", "tree", "blob" }.Contains(parts[2]))
                throw new InvalidDataException("Paste the repository homepage or one of its release links.");
            return owner + "/" + repo;
        }
        static bool Flag(Dictionary<string, object> value, string name)
        {
            object raw;
            if (!value.TryGetValue(name, out raw) || !(raw is bool)) throw new InvalidDataException("Missing release flag: " + name);
            return (bool)raw;
        }
        static void CheckUrl(string url, string repository, string suffix)
        {
            Uri address;
            if (!Uri.TryCreate(url, UriKind.Absolute, out address) || address.Scheme != "https" || address.Host != "github.com" || address.Port != 443 || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
                throw new InvalidDataException("Unexpected patch release location.");
            string prefix = "/" + repository + "/releases/";
            string path = Uri.UnescapeDataString(address.AbsolutePath);
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Substring(prefix.Length) != suffix)
                throw new InvalidDataException("The patch asset belongs to a different repository or release.");
        }
        public static List<PatchAsset> ParseReleases(string json, string repository, bool includePrereleases)
        {
            repository = Repository("https://github.com/" + repository);
            if (json.Length > 4 * 1024 * 1024) throw new InvalidDataException("The release list is too large.");
            var releases = Json.Array(Json.Parse("{\"releases\":" + json + "}"), "releases");
            if (releases.Count > 100) throw new InvalidDataException("The release list exceeds its limit.");
            var candidates = new List<PatchAsset>();
            foreach (var entry in releases)
            {
                var release = Json.Object(entry);
                if (Flag(release, "draft") || (Flag(release, "prerelease") && !includePrereleases)) continue;
                string tag = Json.String(release, "tag_name"), page = Json.String(release, "html_url");
                if (String.IsNullOrWhiteSpace(tag) || tag.Length > 200 || tag.IndexOfAny(new[] { '\\', '?', '#', '\0' }) >= 0) throw new InvalidDataException("Unsupported release tag.");
                CheckUrl(page, repository, "tag/" + tag);
                DateTimeOffset date;
                if (!DateTimeOffset.TryParse(Json.String(release, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date))
                    throw new InvalidDataException("Missing release publication date.");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rawAsset in Json.Array(release, "assets"))
                {
                    var asset = Json.Object(rawAsset); string name = Json.String(asset, "name");
                    if (!(name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".patchwork", StringComparison.OrdinalIgnoreCase))) continue;
                    if (!Regex.IsMatch(name, @"^[A-Za-z0-9_.-]{1,150}$") || !names.Add(name)) throw new InvalidDataException("Invalid or duplicate patch asset name.");
                    if (Json.String(asset, "state", "uploaded") != "uploaded") continue;
                    string digest = Json.String(asset, "digest", ""), url = Json.String(asset, "browser_download_url");
                    if (!Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$")) throw new InvalidDataException("Patch assets need GitHub's SHA-256 digest.");
                    object rawSize; long size;
                    if (!asset.TryGetValue("size", out rawSize) || !(rawSize is int || rawSize is long) || !Int64.TryParse(rawSize.ToString(), out size) || size <= 0 || size > MaximumPack)
                        throw new InvalidDataException("Patch assets must be at most 1 MB.");
                    CheckUrl(url, repository, "download/" + tag + "/" + name);
                    candidates.Add(new PatchAsset { Name = name, Url = url, Sha256 = digest.Substring(7).ToLowerInvariant(), Size = size, ReleaseUrl = page, PublishedUtc = date.UtcDateTime });
                }
            }
            var newest = candidates.OrderByDescending(x => x.PublishedUtc).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
            if (newest.Count > 20) throw new InvalidDataException("This source publishes more than 20 distinct patch files. Use a smaller patch repository.");
            return newest;
        }
        public static PatchBundle Verify(PatchAsset asset, byte[] bytes)
        {
            if (bytes.LongLength != asset.Size || bytes.Length > MaximumPack || PatchEngine.Hash(bytes) != asset.Sha256)
                throw new InvalidDataException("The patch download failed its size or SHA-256 check. The saved pack was kept.");
            var bundle = PatchBundle.Parse(PatchEngine.Decode(bytes));
            if (bundle.PackVersion == "Unversioned") throw new InvalidDataException("Automatic patch sources require a packVersion in each patch file.");
            return bundle;
        }
        public static SourceDownload Download(PatchSource source, string dataRoot)
        {
            return Download(source, dataRoot, Updates.Fetch);
        }
        internal static SourceDownload Download(PatchSource source, string dataRoot, Func<string, int, byte[]> fetch)
        {
            try
            {
                string api = "https://api.github.com/repos/" + source.Repository + "/releases?per_page=100";
                string feed = PatchEngine.Decode(fetch(api, 4 * 1024 * 1024));
                var assets = ParseReleases(feed, source.Repository, source.IncludePrereleases);
                if (assets.Count == 0) throw new IOException("No JSON patch release assets found. This source may need Include pre-releases enabled.");
                var result = new SourceDownload { Repository = source.Repository, IncludePrereleases = source.IncludePrereleases };
                foreach (var asset in assets)
                {
                    var tracked = source.Packs.SingleOrDefault(x => x.AssetName.Equals(asset.Name, StringComparison.OrdinalIgnoreCase));
                    if (tracked != null && tracked.AssetDigest == asset.Sha256)
                    {
                        string recipe = PatchEngine.Resolve(dataRoot, "recipes/" + tracked.BundleId + ".json");
                        if (File.Exists(recipe) && PatchEngine.Hash(File.ReadAllBytes(recipe)) == tracked.ContentHash) continue;
                    }
                    result.Packs.Add(Tuple.Create(asset, Verify(asset, fetch(asset.Url, MaximumPack))));
                }
                return result;
            }
            catch (GitHubHttpException error)
            {
                if (error.StatusCode == 404) throw new IOException("The public repository or its releases could not be found.");
                if (error.StatusCode == 403 || error.StatusCode == 429) throw new IOException("GitHub limited source checks. Existing patches were kept; try again later.");
                throw;
            }
            catch (WebException error)
            {
                var response = error.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) throw new IOException("The public repository or its releases could not be found.");
                if (response != null && ((int)response.StatusCode == 403 || (int)response.StatusCode == 429)) throw new IOException("GitHub limited source checks. The existing patches were kept; try again later.");
                throw new IOException("Could not reach GitHub. Existing patches are still available.", error);
            }
        }
        internal static Version PackVersion(string version)
        {
            var parsed = Version.Parse(version);
            return new Version(parsed.Major, parsed.Minor, parsed.Build, Math.Max(0, parsed.Revision));
        }
    }
    public sealed class PatchSourceStore
    {
        readonly string root, file;
        public List<PatchSource> Sources { get; private set; }
        public PatchSourceStore(string dataRoot)
        {
            root = Path.GetFullPath(dataRoot); Directory.CreateDirectory(root);
            file = PatchEngine.Resolve(root, "patch-sources.json"); Reload();
        }
        void Reload()
        {
            Sources = File.Exists(file) ? Json.Serializer.Deserialize<List<PatchSource>>(PatchEngine.Decode(PatchEngine.Read(file))) : new List<PatchSource>();
            if (Sources == null || Sources.Count > 10) throw new InvalidDataException("The saved patch source list is invalid.");
            var repositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in Sources)
            {
                if (source.Repository != PatchSources.Repository("https://github.com/" + source.Repository) || !repositories.Add(source.Repository) || source.Packs == null || source.Packs.Count > 100)
                    throw new InvalidDataException("Invalid or duplicate saved patch source.");
                foreach (var pack in source.Packs)
                {
                    PatchBundle.ValidateId(pack.BundleId); PatchBundle.ValidateHash(pack.AssetDigest); PatchBundle.ValidateHash(pack.ContentHash);
                    if (!ids.Add(pack.BundleId) || String.IsNullOrEmpty(pack.AssetName)) throw new InvalidDataException("Duplicate source ownership.");
                }
            }
        }
        IDisposable Lock()
        {
            try { return new FileStream(PatchEngine.Resolve(root, "source-catalog.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new IOException("Another Patchwork instance is updating the patch library. Try again later."); }
        }
        static void AtomicWrite(string destination, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        void Save() { AtomicWrite(file, Encoding.UTF8.GetBytes(Json.Pretty(Sources))); }
        PatchSource Find(string repository) { return Sources.Single(x => x.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase)); }
        public PatchSource Add(string link, bool prereleases)
        {
            string repository = PatchSources.Repository(link);
            using (Lock())
            {
                Reload();
                if (Sources.Any(x => x.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("This patch source is already added.");
                if (Sources.Count == 10) throw new InvalidDataException("Up to ten patch sources are supported.");
                var source = new PatchSource { Repository = repository, IncludePrereleases = prereleases, Status = "Added. Waiting for the first source check." };
                Sources.Add(source); Save(); return source;
            }
        }
        public void Options(string repository, bool automatic, bool prereleases)
        {
            using (Lock()) { Reload(); var source = Find(repository); source.Automatic = automatic; source.IncludePrereleases = prereleases; Save(); }
        }
        public void Remove(string repository)
        {
            using (Lock()) { Reload(); Sources.Remove(Find(repository)); Save(); }
        }
        public string Owner(string bundleId)
        {
            var source = Sources.SingleOrDefault(x => x.Packs.Any(p => p.BundleId == bundleId));
            return source == null ? null : source.Repository;
        }
        public void ImportLocal(PatchBundle bundle)
        {
            using (Lock())
            {
                Reload(); string destination = PatchEngine.Resolve(root, "recipes/" + bundle.Id + ".json");
                byte[] bytes = Encoding.UTF8.GetBytes(bundle.Content); string owner = Owner(bundle.Id);
                if (owner != null && (!File.Exists(destination) || PatchEngine.Hash(File.ReadAllBytes(destination)) != PatchEngine.Hash(bytes)))
                    throw new InvalidDataException("This pack is managed by " + owner + ". Remove that source before replacing it with a different local file.");
                AtomicWrite(destination, bytes);
            }
        }
        public int Apply(SourceDownload download)
        {
            using (Lock())
            {
                Reload(); var source = Json.Serializer.Deserialize<PatchSource>(Json.Pretty(Find(download.Repository)));
                if (source.IncludePrereleases != download.IncludePrereleases) throw new InvalidOperationException("Source preferences changed while checking. Check again.");
                var incoming = new List<Tuple<PatchAsset, PatchBundle>>();
                foreach (var group in download.Packs.GroupBy(x => x.Item2.Id))
                {
                    var ordered = group.OrderByDescending(x => PatchSources.PackVersion(x.Item2.PackVersion)).ToList();
                    if (ordered.Count > 1 && ordered[0].Item2.PackVersion == ordered[1].Item2.PackVersion && ordered[0].Item2.Content != ordered[1].Item2.Content)
                        throw new InvalidDataException("Two release assets claim the same pack version with different content.");
                    incoming.Add(ordered[0]);
                }
                var writes = new Dictionary<string, byte[]>();
                int changed = 0;
                foreach (var item in incoming)
                {
                    var asset = item.Item1; var bundle = item.Item2;
                    string owner = Owner(bundle.Id);
                    if (owner != null && !owner.Equals(source.Repository, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Pack " + bundle.Id + " is already managed by " + owner + ". No saved packs were changed.");
                    string destination = PatchEngine.Resolve(root, "recipes/" + bundle.Id + ".json");
                    if (source.Packs.Any(x => x.AssetName.Equals(asset.Name, StringComparison.OrdinalIgnoreCase) && x.BundleId != bundle.Id)) throw new InvalidDataException("A tracked release asset changed its bundle ID. Remove and re-add the source to adopt it.");
                    byte[] bytes = Encoding.UTF8.GetBytes(bundle.Content);
                    if (File.Exists(destination))
                    {
                        byte[] existing = File.ReadAllBytes(destination);
                        var tracked = source.Packs.SingleOrDefault(x => x.BundleId == bundle.Id);
                        if (tracked != null && PatchEngine.Hash(existing) != tracked.ContentHash) throw new InvalidDataException("A source-managed patch file changed outside Patchwork. Remove and re-add its source to adopt the file.");
                        var saved = PatchBundle.Parse(PatchEngine.Decode(existing));
                        if (saved.PackVersion != "Unversioned")
                        {
                            int comparison = PatchSources.PackVersion(bundle.PackVersion).CompareTo(PatchSources.PackVersion(saved.PackVersion));
                            if (comparison == 0 && saved.Content != bundle.Content) throw new InvalidDataException("Pack " + bundle.Id + " changed without a version increase. The saved pack was kept.");
                            if (comparison < 0) { bytes = Encoding.UTF8.GetBytes(saved.Content); bundle = saved; }
                        }
                        if (!bytes.SequenceEqual(existing)) { writes[destination] = bytes; changed++; }
                    }
                    else { writes[destination] = bytes; changed++; }
                    source.Packs.RemoveAll(x => x.BundleId == bundle.Id);
                    source.Packs.Add(new SourcePack { BundleId = bundle.Id, Version = bundle.PackVersion, AssetName = asset.Name, AssetDigest = asset.Sha256, ContentHash = PatchEngine.Hash(bytes), ReleaseUrl = asset.ReleaseUrl });
                }
                source.LastCheckUtc = DateTime.UtcNow.ToString("o");
                source.Status = changed == 0 ? "Up to date. " + source.Packs.Count + " saved pack(s)." : "Updated " + changed + " patch pack(s). Preview to update applied patches.";
                var originals = writes.Keys.ToDictionary(x => x, x => File.Exists(x) ? File.ReadAllBytes(x) : null);
                Sources[Sources.FindIndex(x => x.Repository.Equals(source.Repository, StringComparison.OrdinalIgnoreCase))] = source;
                try { foreach (var write in writes) AtomicWrite(write.Key, write.Value); Save(); }
                catch
                {
                    foreach (var original in originals) { if (original.Value != null) AtomicWrite(original.Key, original.Value); else if (File.Exists(original.Key)) File.Delete(original.Key); }
                    Reload(); throw;
                }
                return changed;
            }
        }
        public void RecordError(string repository, string message)
        {
            using (Lock()) { Reload(); var source = Find(repository); source.LastCheckUtc = DateTime.UtcNow.ToString("o"); source.Status = message; Save(); }
        }
    }
}
