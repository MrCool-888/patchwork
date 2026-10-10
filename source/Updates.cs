using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Patchwork
{
    public sealed class AppRelease
    {
        public Version Version;
        public string Tag, PageUrl, InstallerUrl, Sha256;
        public long Size;
        public bool Prerelease;
    }
    public static class Updates
    {
        public const string Repository = "MrCool-888/patchwork";
        public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
        public const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
        public const string AllReleasesApi = "https://api.github.com/repos/" + Repository + "/releases?per_page=100";
        public static Version InstalledVersion { get { return Assembly.GetExecutingAssembly().GetName().Version; } }
        public static string DisplayVersion { get { return InstalledVersion.ToString(3); } }
        const int MaximumInstaller = 16 * 1024 * 1024;

        public static ProcessStartInfo SetupStartInfo(string installer, string appRoot, int processId, long startTicks)
        {
            installer = Path.GetFullPath(installer);
            appRoot = Path.GetFullPath(appRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (processId <= 0 || startTicks <= 0) throw new ArgumentException("Invalid update process identity.");
            // Never let setup inherit an installation directory that it must rename.
            string folder = Path.GetDirectoryName(installer);
            if (folder.Equals(appRoot, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(appRoot + "\\", StringComparison.OrdinalIgnoreCase))
                throw new IOException("The update installer must be outside the program folder.");
            string args = "--wait-for-process " + processId + " --wait-for-start-ticks " + startTicks;
            // Setup independently checks this marker before replacing an existing folder.
            if (File.Exists(Path.Combine(appRoot, "patchwork-install.json"))) args += " --install-root \"" + appRoot + "\"";
            return new ProcessStartInfo(installer, args) { UseShellExecute = true, WorkingDirectory = folder };
        }

        public static AppRelease Check() { return Check(false); }
        public static AppRelease Check(bool includePrereleases)
        {
            try {
                string content = PatchEngine.Decode(Fetch(includePrereleases ? AllReleasesApi : LatestApi, 1024 * 1024));
                return includePrereleases ? ParseChannel(content, InstalledVersion, true) : Parse(content, InstalledVersion);
            }
            catch (GitHubHttpException error)
            {
                if (error.StatusCode == 404) throw new IOException("No published patcher release was found on GitHub.");
                if (error.StatusCode == 403 || error.StatusCode == 429) throw new IOException("GitHub temporarily limited update requests. Try again later.");
                throw;
            }
            catch (WebException error)
            {
                var response = error.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) throw new IOException("No published patcher release was found on GitHub.");
                if (response != null && (int)response.StatusCode == 403) throw new IOException("GitHub temporarily limited update requests. Try again later.");
                throw new IOException("Could not reach GitHub. Check your connection and try again.", error);
            }
        }
        public static AppRelease Parse(string json, Version installed) { return Parse(json, installed, false); }
        public static AppRelease ParseChannel(string json, Version installed, bool includePrereleases)
        {
            var releases = Json.Serializer.DeserializeObject(json) as object[];
            if (releases == null || releases.Length > 100) throw new InvalidDataException("Invalid app release list.");
            var candidates = new List<AppRelease>();
            foreach (var item in releases) {
                var value = Json.Object(item); object draft, prerelease;
                if (!value.TryGetValue("draft", out draft) || !(draft is bool) || !value.TryGetValue("prerelease", out prerelease) || !(prerelease is bool)) throw new InvalidDataException("Invalid app release channel.");
                if ((bool)draft || (!includePrereleases && (bool)prerelease)) continue;
                if (!Regex.IsMatch(Json.String(value, "tag_name"), @"^v\d+\.\d+\.\d+(\.\d+)?$")) continue;
                var candidate = Parse(Json.Pretty(value), installed, includePrereleases);
                if (candidate != null) candidates.Add(candidate);
            }
            return candidates.OrderByDescending(x => x.Version).FirstOrDefault();
        }
        public static AppRelease Parse(string json, Version installed, bool includePrereleases)
        {
            var value = Json.Parse(json);
            object flag;
            if (!value.TryGetValue("draft", out flag) || !(flag is bool) || (bool)flag ||
                !value.TryGetValue("prerelease", out flag) || !(flag is bool) || ((bool)flag && !includePrereleases))
                throw new InvalidDataException("This release is not published in the selected update channel.");
            bool prerelease = (bool)flag;
            string tag = Json.String(value, "tag_name");
            Version version;
            if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+(\.\d+)?$") || !Version.TryParse(tag.Substring(1), out version))
                throw new InvalidDataException("The release version is unsupported.");
            string pageUrl = Json.String(value, "html_url");
            if (pageUrl != ReleasesUrl + "/tag/" + tag) throw new InvalidDataException("Unexpected release repository.");
            // Normalize three-part tags so 0.3.0 equals assembly version 0.3.0.0.
            version = new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
            if (version <= installed) return null;
            var assets = Json.Array(value, "assets").Select(Json.Object).Where(x => Json.String(x, "name", "") == "Patchwork-Setup.exe").ToList();
            if (assets.Count != 1) throw new InvalidDataException("The release needs one Patchwork-Setup.exe installer.");
            var asset = assets[0];
            string url = Json.String(asset, "browser_download_url");
            if (url != ReleasesUrl + "/download/" + tag + "/Patchwork-Setup.exe") throw new InvalidDataException("Unexpected installer download location.");
            string digest = Json.String(asset, "digest", "");
            if (!Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$")) throw new InvalidDataException("The release installer is missing GitHub's SHA-256 digest. View the release for details.");
            object sizeValue; long size;
            if (!asset.TryGetValue("size", out sizeValue) || !(sizeValue is int || sizeValue is long) || !Int64.TryParse(sizeValue.ToString(), out size) || size <= 0 || size > MaximumInstaller)
                throw new InvalidDataException("The release installer size is unsupported.");
            return new AppRelease { Version = version, Tag = tag, PageUrl = pageUrl, InstallerUrl = url, Sha256 = digest.Substring(7).ToLowerInvariant(), Size = size, Prerelease = prerelease };
        }
        public static string Download(AppRelease release, string dataRoot)
        {
            byte[] bytes = Fetch(release.InstallerUrl, MaximumInstaller);
            Verify(release, bytes);
            string file = PatchEngine.Resolve(dataRoot, "updates/" + Guid.NewGuid().ToString("N") + "/Patchwork-Setup.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllBytes(file, bytes);
            if (PatchEngine.Hash(File.ReadAllBytes(file)) != release.Sha256) throw new IOException("The downloaded installer changed on disk. It was not launched.");
            return file;
        }
        public static void Verify(AppRelease release, byte[] bytes)
        {
            if (bytes.LongLength != release.Size || PatchEngine.Hash(bytes) != release.Sha256)
                throw new InvalidDataException("The installer failed its size or SHA-256 check. It was not launched.");
            if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z') throw new InvalidDataException("The release asset is not a Windows executable.");
        }
        internal static byte[] Fetch(string url, int maximum) { return GitHubHttp.Fetch(url, maximum); }
    }
}
