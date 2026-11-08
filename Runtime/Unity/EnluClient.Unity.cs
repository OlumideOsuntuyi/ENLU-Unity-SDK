#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Enlu
{
    public sealed partial class EnluClient : IDisposable
    {
        private const string DeviceIdPrefsKey = "enlu.deviceId";
        private static EnluClient? _default;

        private float _flushInterval;
        private float _nextFlushAt;
        private Action? _ticker;
        private Action? _suspendHook;

        /// <summary>
        /// A shared client built from <c>Resources/EnluSettings</c>. Assign your own to override it.
        /// Main thread only.
        /// </summary>
        /// <exception cref="InvalidOperationException">No settings asset was found in a Resources folder.</exception>
        public static EnluClient Default
        {
            get
            {
                if (_default != null) return _default;
                var settings = EnluSettings.Load();
                if (settings == null)
                    throw new InvalidOperationException("No ENLU settings found. Create one (Assets > Create > ENLU > Settings), name it 'EnluSettings' and put it in a Resources folder, or construct new EnluClient(settings).");
                _default = new EnluClient(settings);
                return _default;
            }
            set => _default = value;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetDefault() => _default = null;

        /// <summary>
        /// Creates a Unity client from a settings asset: <c>UnityWebRequest</c> transport (WebGL-safe),
        /// frame-based retry delays, ticket persistence and periodic event flushing as configured.
        /// Call on the main thread.
        /// </summary>
        public EnluClient(EnluSettings settings)
            : this(OptionsFrom(settings), new UnityWebRequestTransport(), new EnluUnityScheduler(), StoreFrom(settings))
        {
            _flushInterval = settings.EventFlushIntervalSeconds;
            _nextFlushAt = Time.realtimeSinceStartup + _flushInterval;
            var runner = EnluRunner.Instance;
            _ticker = Tick;
            runner.AddTicker(_ticker);
            _suspendHook = () =>
            {
                if (PendingEventCount > 0 && IsLoggedIn) FlushInBackground();
            };
            EnluRunner.Suspending += _suspendHook;
        }

        private static EnluClientOptions OptionsFrom(EnluSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var problem = settings.Validate();
            if (problem != null && problem.StartsWith("Use https://", StringComparison.Ordinal)) EnluLog.Warn(problem);
            return settings.ToOptions();
        }

        private static IEnluKeyValueStore? StoreFrom(EnluSettings settings)
        {
            if (settings == null || !settings.PersistSessionTicket) return null;
            if (!settings.UseSecureStorage) return new PlayerPrefsStore();
            if (SecureStore != null) return SecureStore;
            EnluLog.Warn("Use secure storage is on but EnluClient.SecureStore is not assigned: the session ticket is kept in memory only.");
            return null;
        }

        private void Tick()
        {
            if (_flushInterval <= 0) return;
            var now = Time.realtimeSinceStartup;
            if (now < _nextFlushAt) return;
            _nextFlushAt = now + _flushInterval;
            if (PendingEventCount > 0 && IsLoggedIn) FlushInBackground();
        }

        /// <summary>
        /// Logs in with this device's identifier (<c>SystemInfo.deviceUniqueIdentifier</c>; on platforms
        /// without one, such as WebGL, a random id stored in PlayerPrefs). Good for guest accounts; the
        /// account is tied to the device, so offer email registration for players who switch devices.
        /// </summary>
        public Task<LoginResult> LoginWithDeviceIdAsync(bool createAccount = true, CancellationToken cancellationToken = default) =>
            LoginWithDeviceIdAsync(GetDeviceId(), createAccount, cancellationToken);

        /// <summary>
        /// The id used by <see cref="LoginWithDeviceIdAsync(bool, CancellationToken)"/>:
        /// <c>SystemInfo.deviceUniqueIdentifier</c>, or a generated GUID persisted in PlayerPrefs when the
        /// platform does not provide one.
        /// </summary>
        public static string GetDeviceId()
        {
            var id = SystemInfo.deviceUniqueIdentifier;
            if (!string.IsNullOrEmpty(id) && id != SystemInfo.unsupportedIdentifier && EnluIds.DeviceCustomId(id) != null) return id;
            var stored = PlayerPrefs.GetString(DeviceIdPrefsKey, string.Empty);
            if (!string.IsNullOrEmpty(stored)) return stored;
            stored = Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(DeviceIdPrefsKey, stored);
            PlayerPrefs.Save();
            return stored;
        }

        /// <summary>Stops the periodic flush timer. The session is kept (see <see cref="LogoutAsync"/>).</summary>
        public void Dispose()
        {
            if (_ticker != null)
            {
                var runner = EnluRunner.Current;
                if (runner != null) runner.RemoveTicker(_ticker);
                _ticker = null;
            }
            if (_suspendHook != null)
            {
                EnluRunner.Suspending -= _suspendHook;
                _suspendHook = null;
            }
            if (ReferenceEquals(_default, this)) _default = null;
        }
    }
}
