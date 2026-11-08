#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Enlu.Editor
{
    /// <summary>
    /// Where the Editor keeps the refresh token between sessions: the operating system's credential
    /// store, keyed by server host. Never the project folder, EditorPrefs or PlayerPrefs.
    /// </summary>
    public interface IEnluCredentialStore
    {
        /// <summary>Short description for the UI, e.g. "Windows (DPAPI)".</summary>
        string Description { get; }

        /// <summary>True when values survive an Editor restart.</summary>
        bool IsPersistent { get; }

        /// <summary>Reads the secret for a host, or null.</summary>
        string? Read(string host);

        /// <summary>Stores (or replaces) the secret for a host.</summary>
        void Write(string host, string secret);

        /// <summary>Removes the secret for a host (no error when there is none).</summary>
        void Delete(string host);
    }

    /// <summary>Keeps secrets in memory only. Used by tests and when no secure store is available.</summary>
    public sealed class EnluMemoryCredentialStore : IEnluCredentialStore
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <inheritdoc />
        public string Description => "memory only (sign in again after restarting Unity)";
        /// <inheritdoc />
        public bool IsPersistent => false;
        /// <inheritdoc />
        public string? Read(string host)
        {
            lock (_values) return _values.TryGetValue(host, out var v) ? v : null;
        }
        /// <inheritdoc />
        public void Write(string host, string secret)
        {
            lock (_values) _values[host] = secret;
        }
        /// <inheritdoc />
        public void Delete(string host)
        {
            lock (_values) _values.Remove(host);
        }
    }

    /// <summary>Picks the credential store for this operating system.</summary>
    public static class EnluCredentialStores
    {
        /// <summary>
        /// The OS store: DPAPI on Windows, the login Keychain on macOS, the Secret Service
        /// (<c>secret-tool</c>) on Linux. Null when none is usable.
        /// </summary>
        public static IEnluCredentialStore? CreateSecureStore()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new EnluDpapiCredentialStore();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return new EnluKeychainCredentialStore();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && EnluSecretToolCredentialStore.IsInstalled()) return new EnluSecretToolCredentialStore();
            }
            catch (Exception)
            {
                // Fall through to "none".
            }
            return null;
        }

        /// <summary>
        /// The key for a server: lower-case host plus a non-default port, e.g. <c>api.example.com</c> or
        /// <c>127.0.0.1:7700</c>. Throws for anything that is not an absolute http(s) URL.
        /// </summary>
        public static string HostKey(string baseUrl)
        {
            if (!Uri.TryCreate((baseUrl ?? string.Empty).Trim(), UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https") || u.Host.Length == 0)
                throw new ArgumentException("The server URL must be an absolute http(s) URL.", nameof(baseUrl));
            var host = u.Host.ToLowerInvariant();
            return u.IsDefaultPort ? host : host + ":" + u.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string Sha256Hex(string s)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder(h.Length * 2);
                foreach (var b in h) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }

    /* ------------------------------------ Windows ------------------------------------ */

    /// <summary>
    /// Windows: the secret is encrypted with DPAPI (<c>CryptProtectData</c>, current user scope, with
    /// app-specific entropy) and the blob is written to
    /// <c>%LOCALAPPDATA%\ENLU\UnityEditor\&lt;sha256(host)&gt;.bin</c>. Only the same Windows user can decrypt it.
    /// </summary>
    public sealed class EnluDpapiCredentialStore : IEnluCredentialStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ENLU Unity Editor refresh token v1");
        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
        private readonly string _directory;

        /// <summary>Uses <c>%LOCALAPPDATA%\ENLU\UnityEditor</c>, or <paramref name="directory"/> (tests).</summary>
        public EnluDpapiCredentialStore(string? directory = null)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) throw new PlatformNotSupportedException("DPAPI is Windows only.");
            _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ENLU", "UnityEditor");
        }

        /// <inheritdoc />
        public string Description => "Windows (DPAPI, current user)";
        /// <inheritdoc />
        public bool IsPersistent => true;

        private string PathFor(string host) => Path.Combine(_directory, EnluCredentialStores.Sha256Hex(host) + ".bin");

        /// <inheritdoc />
        public string? Read(string host)
        {
            var file = PathFor(host);
            if (!File.Exists(file)) return null;
            byte[]? plain = null;
            try
            {
                plain = Unprotect(File.ReadAllBytes(file), Entropy);
                return Encoding.UTF8.GetString(plain);
            }
            catch (CryptographicException)
            {
                // Written by another user or corrupted: treat as signed out.
                TryDelete(file);
                return null;
            }
            finally
            {
                if (plain != null) Array.Clear(plain, 0, plain.Length);
            }
        }

        /// <inheritdoc />
        public void Write(string host, string secret)
        {
            Directory.CreateDirectory(_directory);
            var plain = Encoding.UTF8.GetBytes(secret);
            try
            {
                var blob = Protect(plain, Entropy);
                var file = PathFor(host);
                var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, blob);
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        /// <inheritdoc />
        public void Delete(string host) => TryDelete(PathFor(host));

        private static void TryDelete(string file)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        internal static byte[] Protect(byte[] data, byte[] entropy) => Run(data, entropy, true);

        internal static byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, false);

        private static byte[] Run(byte[] data, byte[] entropy, bool protect)
        {
            var dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
            var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
            var output = new DataBlob();
            try
            {
                var input = new DataBlob { cbData = data.Length, pbData = dataHandle.AddrOfPinnedObject() };
                var ent = new DataBlob { cbData = entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };
                var ok = protect
                    ? CryptProtectData(ref input, "ENLU Unity Editor", ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
                if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                if (output.pbData != IntPtr.Zero)
                {
                    if (!protect)
                    {
                        // Wipe the decrypted copy before freeing it.
                        for (var i = 0; i < output.cbData; i++) Marshal.WriteByte(output.pbData, i, 0);
                    }
                    LocalFree(output.pbData);
                }
                dataHandle.Free();
                entropyHandle.Free();
            }
        }
    }

    /* ------------------------------------- macOS ------------------------------------- */

    /// <summary>
    /// macOS: a generic password item in the user's login Keychain (service "ENLU Unity Editor",
    /// account = server host), through Security.framework.
    /// </summary>
    public sealed class EnluKeychainCredentialStore : IEnluCredentialStore
    {
        private const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const int ErrSecSuccess = 0;
        private const int ErrSecItemNotFound = -25300;
        private static readonly byte[] Service = Encoding.UTF8.GetBytes("ENLU Unity Editor");

        /// <summary>Creates the store (macOS only).</summary>
        public EnluKeychainCredentialStore()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) throw new PlatformNotSupportedException("The Keychain is macOS only.");
        }

        /// <inheritdoc />
        public string Description => "macOS Keychain";
        /// <inheritdoc />
        public bool IsPersistent => true;

        [DllImport(SecurityLib)]
        private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, IntPtr itemRef);

        [DllImport(SecurityLib)]
        private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

        [DllImport(SecurityLib)]
        private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

        [DllImport(SecurityLib)]
        private static extern int SecKeychainItemDelete(IntPtr itemRef);

        [DllImport(SecurityLib)]
        private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport(CoreFoundationLib)]
        private static extern void CFRelease(IntPtr cf);

        /// <inheritdoc />
        public string? Read(string host)
        {
            var account = Encoding.UTF8.GetBytes(host);
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                out var length, out var data, out var item);
            if (status == ErrSecItemNotFound) return null;
            if (status != ErrSecSuccess) throw new InvalidOperationException("Keychain read failed (OSStatus " + status + ").");
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, (int)length);
                var s = Encoding.UTF8.GetString(bytes);
                Array.Clear(bytes, 0, bytes.Length);
                return s;
            }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                if (item != IntPtr.Zero) CFRelease(item);
            }
        }

        /// <inheritdoc />
        public void Write(string host, string secret)
        {
            var account = Encoding.UTF8.GetBytes(host);
            var password = Encoding.UTF8.GetBytes(secret);
            try
            {
                var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                    out _, out var data, out var item);
                if (status == ErrSecSuccess)
                {
                    try
                    {
                        var m = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password);
                        if (m != ErrSecSuccess) throw new InvalidOperationException("Keychain update failed (OSStatus " + m + ").");
                    }
                    finally
                    {
                        SecKeychainItemFreeContent(IntPtr.Zero, data);
                        if (item != IntPtr.Zero) CFRelease(item);
                    }
                    return;
                }
                if (status != ErrSecItemNotFound) throw new InvalidOperationException("Keychain lookup failed (OSStatus " + status + ").");
                var add = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                    (uint)password.Length, password, IntPtr.Zero);
                if (add != ErrSecSuccess) throw new InvalidOperationException("Keychain write failed (OSStatus " + add + ").");
            }
            finally
            {
                Array.Clear(password, 0, password.Length);
            }
        }

        /// <inheritdoc />
        public void Delete(string host)
        {
            var account = Encoding.UTF8.GetBytes(host);
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                out _, out var data, out var item);
            if (status != ErrSecSuccess) return;
            try
            {
                SecKeychainItemDelete(item);
            }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                if (item != IntPtr.Zero) CFRelease(item);
            }
        }
    }

    /* ------------------------------------- Linux ------------------------------------- */

    /// <summary>
    /// Linux: the Secret Service (GNOME Keyring, KWallet…) through <c>secret-tool</c>. The secret is
    /// passed on standard input, never on the command line.
    /// </summary>
    public sealed class EnluSecretToolCredentialStore : IEnluCredentialStore
    {
        private const string Attribute = "enlu-unity-editor";

        /// <inheritdoc />
        public string Description => "Secret Service (secret-tool)";
        /// <inheritdoc />
        public bool IsPersistent => true;

        /// <summary>True when <c>secret-tool</c> is on the PATH.</summary>
        public static bool IsInstalled()
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, "secret-tool"))) return true;
            }
            return false;
        }

        private static string SafeHost(string host)
        {
            foreach (var c in host)
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == ':' || c == '[' || c == ']'))
                    throw new ArgumentException("Unexpected character in host.", nameof(host));
            return host;
        }

        /// <inheritdoc />
        public string? Read(string host)
        {
            var r = Run("lookup service " + Attribute + " host " + SafeHost(host), null);
            if (r.ExitCode != 0) return null;
            var s = r.Output.TrimEnd('\n', '\r');
            return s.Length == 0 ? null : s;
        }

        /// <inheritdoc />
        public void Write(string host, string secret)
        {
            var label = "\"ENLU Unity Editor (" + SafeHost(host) + ")\"";
            var r = Run("store --label=" + label + " service " + Attribute + " host " + SafeHost(host), secret);
            if (r.ExitCode != 0) throw new InvalidOperationException("secret-tool store failed (exit " + r.ExitCode + ").");
        }

        /// <inheritdoc />
        public void Delete(string host) => Run("clear service " + Attribute + " host " + SafeHost(host), null);

        private static (int ExitCode, string Output) Run(string args, string? stdin)
        {
            var psi = new ProcessStartInfo("secret-tool", args)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start secret-tool."))
            {
                if (stdin != null) p.StandardInput.Write(stdin);
                p.StandardInput.Close();
                var output = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(15000))
                {
                    try
                    {
                        p.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    return (-1, string.Empty);
                }
                return (p.ExitCode, output);
            }
        }
    }
}
