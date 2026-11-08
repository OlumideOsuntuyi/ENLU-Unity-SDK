#nullable enable

namespace Enlu
{
    public sealed partial class EnluServerApi
    {
        /// <summary>
        /// Creates the server API from settings (base URL and app) and an API key. With
        /// <paramref name="apiKey"/> null the key is read from the <c>ENLU_API_KEY</c> environment variable.
        /// Uses <c>UnityWebRequest</c>; call on the main thread.
        /// </summary>
        /// <remarks>Only use this in a dedicated server build or trusted tool. Never ship an API key in a client.</remarks>
        public EnluServerApi(EnluSettings? settings, string? apiKey = null)
            : this(settings != null ? settings.ToOptions() : new EnluClientOptions(), apiKey, new UnityWebRequestTransport(), new EnluUnityScheduler())
        {
#if !UNITY_SERVER && !UNITY_EDITOR && !ENLU_SERVER
            EnluLog.Warn("EnluServerApi is running in a client build. API keys must never ship to players; use it only in dedicated server builds.");
#endif
        }
    }
}
