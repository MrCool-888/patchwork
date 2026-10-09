using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Patchwork
{
    internal sealed class GitHubHttpException : IOException
    {
        public readonly int StatusCode;
        public GitHubHttpException(int status) : base("GitHub returned HTTP " + status + ". Existing files were kept.") { StatusCode = status; }
    }
    // Windows' native HTTP stack uses the OS proxy and certificate trust configuration.
    // Redirects are followed explicitly; certificate validation is never disabled.
    internal static class GitHubHttp
    {
        sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public Handle() : base(true) { }
            protected override bool ReleaseHandle() { return WinHttpCloseHandle(handle); }
        }
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern Handle WinHttpOpen(string agent, uint access, string proxy, string bypass, uint flags);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern Handle WinHttpConnect(Handle session, string host, ushort port, uint reserved);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern Handle WinHttpOpenRequest(Handle connection, string method, string path, string version, string referer, IntPtr accepts, uint flags);
        [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool WinHttpSetTimeouts(Handle session, int resolve, int connect, int send, int receive);
        [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool WinHttpSetOption(Handle request, uint option, ref uint value, uint length);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool WinHttpSendRequest(Handle request, string headers, uint headerLength, IntPtr optional, uint optionalLength, uint totalLength, IntPtr context);
        [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool WinHttpReceiveResponse(Handle request, IntPtr reserved);
        [DllImport("winhttp.dll", EntryPoint = "WinHttpQueryHeaders", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool Status(Handle request, uint query, string name, out uint value, ref uint length, IntPtr index);
        [DllImport("winhttp.dll", EntryPoint = "WinHttpQueryHeaders", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool Header(Handle request, uint query, string name, StringBuilder value, ref uint length, IntPtr index);
        [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool WinHttpReadData(Handle request, byte[] buffer, uint length, out uint read);
        [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool WinHttpCloseHandle(IntPtr handle);
        static void Check(bool success)
        {
            if (success) return;
            int error = Marshal.GetLastWin32Error();
            throw new IOException(error == 12175 ? "Windows could not verify the secure GitHub connection. Existing files were kept." : "Could not reach GitHub using Windows networking. Existing files were kept.", new Win32Exception(error));
        }
        internal static Uri Address(string url)
        {
            Uri address;
            if (!Uri.TryCreate(url, UriKind.Absolute, out address) || address.Scheme != "https" || address.Port != 443 || address.UserInfo.Length != 0 || address.Fragment.Length != 0 ||
                !new[] { "api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com" }.Contains(address.Host))
                throw new InvalidDataException("Unexpected GitHub download host.");
            return address;
        }
        static string ReadHeader(Handle request, uint query)
        {
            uint length = 0;
            if (!Header(request, query, null, null, ref length, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 12150) return null; // Header not present.
                if (error != 122) Check(false);
            }
            if (length == 0) return "";
            if (length > 32768) throw new InvalidDataException("GitHub sent an oversized response header.");
            var value = new StringBuilder((int)length / 2 + 1);
            Check(Header(request, query, null, value, ref length, IntPtr.Zero)); return value.ToString();
        }
        internal static byte[] Fetch(string url, int maximum)
        {
            if (maximum <= 0 || maximum > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException("maximum");
            using (var session = WinHttpOpen("Patchwork/" + Updates.DisplayVersion, 0, null, null, 0)) // Default Windows proxy.
            {
                Check(!session.IsInvalid); Check(WinHttpSetTimeouts(session, 15000, 15000, 15000, 15000));
                uint protocols = 0x800; // TLS 1.2, including on older Framework hosts.
                Check(WinHttpSetOption(session, 84, ref protocols, 4));
                for (int redirects = 0; redirects < 6; redirects++)
                {
                    Uri address = Address(url);
                    using (var connection = WinHttpConnect(session, address.Host, 443, 0))
                    {
                        Check(!connection.IsInvalid);
                        using (var request = WinHttpOpenRequest(connection, "GET", address.PathAndQuery, null, null, IntPtr.Zero, 0x00800000)) // HTTPS.
                        {
                            Check(!request.IsInvalid);
                            uint disabled = 7; // No cookies, automatic redirects or automatic authentication.
                            Check(WinHttpSetOption(request, 63, ref disabled, 4));
                            string headers = address.Host == "api.github.com" ? "Accept: application/vnd.github+json\r\nX-GitHub-Api-Version: 2022-11-28\r\n" : "Accept: application/octet-stream\r\n";
                            Check(WinHttpSendRequest(request, headers, (uint)headers.Length, IntPtr.Zero, 0, 0, IntPtr.Zero));
                            Check(WinHttpReceiveResponse(request, IntPtr.Zero));
                            uint status, length = 4; Check(Status(request, 19 | 0x20000000, null, out status, ref length, IntPtr.Zero));
                            if (status >= 300 && status < 400)
                            {
                                string location = ReadHeader(request, 33);
                                if (String.IsNullOrEmpty(location)) throw new IOException("GitHub sent an invalid redirect.");
                                url = new Uri(address, location).AbsoluteUri; continue;
                            }
                            if (status != 200) throw new GitHubHttpException((int)status);
                            long contentLength;
                            if (Int64.TryParse(ReadHeader(request, 5), out contentLength) && contentLength > maximum) throw new IOException("The GitHub download exceeds its size limit.");
                            using (var output = new MemoryStream())
                            {
                                byte[] buffer = new byte[16384]; uint read;
                                while (true)
                                {
                                    Check(WinHttpReadData(request, buffer, (uint)buffer.Length, out read));
                                    if (read == 0) return output.ToArray();
                                    if (output.Length + read > maximum) throw new IOException("The GitHub download exceeds its size limit.");
                                    output.Write(buffer, 0, (int)read);
                                }
                            }
                        }
                    }
                }
            }
            throw new IOException("GitHub sent too many redirects.");
        }
    }
}
