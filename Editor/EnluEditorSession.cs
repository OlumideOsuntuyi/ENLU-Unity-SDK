#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using UnityEditor;

namespace Enlu.Editor
{
    /// <summary>
    /// Keeps the access token, its expiry and the user's name in <c>SessionState</c>: memory of the
    /// Editor process that survives script reloads and is gone when Unity quits. Nothing is written
    /// to the project or to EditorPrefs.
    /// </summary>
    internal sealed class EnluSessionStateTokenCache : IEnluTokenCache
    {
        private static string Key(string host) => "ENLU.Auth.Session." + host;

        public string? Load(string hostKey)
        {
            var v = SessionState.GetString(Key(hostKey), string.Empty);
            return v.Length == 0 ? null : v;
        }

        public void Save(string hostKey, string? json)
        {
            if (json == null) SessionState.EraseString(Key(hostKey));
            else SessionState.SetString(Key(hostKey), json);
        }
    }

    /// <summary>
    /// Fallback when the computer has no usable secure store: the refresh token lives in
    /// <c>SessionState</c> only, so the developer signs in again after restarting Unity.
    /// </summary>
    internal sealed class EnluSessionStateCredentialStore : IEnluCredentialStore
    {
        private static string Key(string host) => "ENLU.Auth.Refresh." + host;

        public string Description => "this Unity session only (no secure credential store found)";
        public bool IsPersistent => false;

        public string? Read(string host)
        {
            var v = SessionState.GetString(Key(host), string.Empty);
            return v.Length == 0 ? null : v;
        }

        public void Write(string host, string secret) => SessionState.SetString(Key(host), secret);

        public void Delete(string host) => SessionState.EraseString(Key(host));
    }

    /// <summary>One <see cref="EnluEditorAuth"/> per server for the whole Editor.</summary>
    public static class EnluEditorSession
    {
        private static readonly Dictionary<string, EnluEditorAuth> ByHost = new Dictionary<string, EnluEditorAuth>(StringComparer.Ordinal);
        private static HttpClient? _http;
        private static IEnluCredentialStore? _store;

        /// <summary>The credential store in use (OS store, or this session only).</summary>
        public static IEnluCredentialStore Store => _store ??= EnluCredentialStores.CreateSecureStore() ?? new EnluSessionStateCredentialStore();

        /// <summary>Returns the sign-in for a server URL (created on first use).</summary>
        public static EnluEditorAuth For(string baseUrl)
        {
            var normalized = EnluUrl.NormalizeBaseUrl(baseUrl);
            var key = EnluCredentialStores.HostKey(normalized) + "|" + normalized;
            if (ByHost.TryGetValue(key, out var auth)) return auth;
            _http ??= new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            auth = new EnluEditorAuth(normalized, Store, new EnluSessionStateTokenCache(), _http);
            ByHost[key] = auth;
            return auth;
        }

        /// <summary>
        /// True when the URL uses https, or http to this computer only (local development).
        /// </summary>
        public static bool IsSafeUrl(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var u)) return false;
            if (u.Scheme == "https") return true;
            return u.Scheme == "http" && (u.IsLoopback || u.Host == "localhost");
        }
    }
}
