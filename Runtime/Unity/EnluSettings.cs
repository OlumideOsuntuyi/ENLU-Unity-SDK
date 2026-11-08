#nullable enable
using UnityEngine;

namespace Enlu
{
    /// <summary>
    /// Project settings for the ENLU SDK. Create one with <b>Assets &gt; Create &gt; ENLU &gt; Settings</b>.
    /// Put it in a <c>Resources</c> folder and name it <c>EnluSettings</c> so <see cref="Load"/> and
    /// <see cref="EnluClient.Default"/> find it, or reference it from a component.
    /// </summary>
    /// <remarks>Never put an API key in this asset: it ships inside your game build.</remarks>
    public sealed class EnluSettings : ScriptableObject
    {
        /// <summary>Resource name looked up by <see cref="Load"/>.</summary>
        public const string ResourceName = "EnluSettings";

        [Tooltip("Control node URL, e.g. https://api.example.com (no trailing path).")]
        [SerializeField] private string baseUrl = "https://";

        [Tooltip("App id (app_…) or app name, as shown in the ENLU console.")]
        [SerializeField] private string app = "";

        [Tooltip("Per-request timeout in seconds.")]
        [Range(2, 120)]
        [SerializeField] private int requestTimeoutSeconds = 15;

        [Tooltip("Retries for transient failures (rate limits, no standby server, network errors on safe calls).")]
        [Range(0, 10)]
        [SerializeField] private int maxRetries = 3;

        [Tooltip("Keep the player logged in between launches by storing the session ticket on the device.")]
        [SerializeField] private bool persistSessionTicket = true;

        [Tooltip("Store the ticket through EnluClient.SecureStore (e.g. Keychain/Keystore) instead of PlayerPrefs. " +
                 "If no SecureStore is assigned, the ticket stays in memory only.")]
        [SerializeField] private bool useSecureStorage;

        [Tooltip("Send queued events every N seconds (0 = only when 50 are waiting or on FlushAsync).")]
        [Range(0, 300)]
        [SerializeField] private float eventFlushIntervalSeconds = 10;

        [Tooltip("Log one line per request (method, path, status). Tickets, keys and bodies are never logged.")]
        [SerializeField] private bool logRequests;

        /// <summary>Control node URL.</summary>
        public string BaseUrl { get => baseUrl; set => baseUrl = value; }
        /// <summary>App id or name.</summary>
        public string App { get => app; set => app = value; }
        /// <summary>Per-request timeout in seconds.</summary>
        public int RequestTimeoutSeconds { get => requestTimeoutSeconds; set => requestTimeoutSeconds = value; }
        /// <summary>Retries for transient failures.</summary>
        public int MaxRetries { get => maxRetries; set => maxRetries = value; }
        /// <summary>Persist the session ticket between launches.</summary>
        public bool PersistSessionTicket { get => persistSessionTicket; set => persistSessionTicket = value; }
        /// <summary>Persist through <see cref="EnluClient.SecureStore"/> rather than PlayerPrefs.</summary>
        public bool UseSecureStorage { get => useSecureStorage; set => useSecureStorage = value; }
        /// <summary>Automatic event flush interval in seconds (0 disables the timer).</summary>
        public float EventFlushIntervalSeconds { get => eventFlushIntervalSeconds; set => eventFlushIntervalSeconds = value; }
        /// <summary>Log one line per request.</summary>
        public bool LogRequests { get => logRequests; set => logRequests = value; }

        /// <summary>Converts to plain options.</summary>
        public EnluClientOptions ToOptions() => new EnluClientOptions
        {
            BaseUrl = baseUrl,
            App = app,
            TimeoutSeconds = requestTimeoutSeconds,
            MaxRetries = maxRetries,
            LogRequests = logRequests,
        };

        /// <summary>
        /// Checks the settings and returns a problem description, or null when they look usable.
        /// </summary>
        public string? Validate()
        {
            try
            {
                EnluUrl.NormalizeBaseUrl(baseUrl);
            }
            catch (System.ArgumentException e)
            {
                return e.Message;
            }
            if (string.IsNullOrWhiteSpace(app)) return "Set the app id or name.";
            if (baseUrl.Trim().StartsWith("http://", System.StringComparison.OrdinalIgnoreCase) &&
                !baseUrl.Contains("localhost") && !baseUrl.Contains("127.0.0.1"))
                return "Use https:// outside local testing: session tickets and passwords travel in these requests.";
            return null;
        }

        /// <summary>Loads <c>Resources/EnluSettings</c>, or null when there is none.</summary>
        public static EnluSettings? Load() => Resources.Load<EnluSettings>(ResourceName);

        /// <summary>Creates settings in code (no asset needed).</summary>
        public static EnluSettings Create(string baseUrl, string app)
        {
            var s = CreateInstance<EnluSettings>();
            s.baseUrl = baseUrl;
            s.app = app;
            return s;
        }
    }
}
