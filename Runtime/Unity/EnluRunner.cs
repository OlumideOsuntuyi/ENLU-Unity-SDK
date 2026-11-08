#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Enlu
{
    /// <summary>
    /// A hidden, persistent GameObject that runs SDK work on Unity's main thread: callbacks posted from
    /// background threads (game server heartbeats), frame-based delays that work on WebGL, and periodic
    /// event flushing. Created automatically; you never need to add it yourself.
    /// </summary>
    [AddComponentMenu("")]
    [DefaultExecutionOrder(-1000)]
    public sealed class EnluRunner : MonoBehaviour
    {
        private static EnluRunner? _instance;
        private static readonly object PostLock = new object();
        private static readonly Queue<Action> Posted = new Queue<Action>();
        private static int _mainThreadId = -1;

        private readonly List<PendingDelay> _delays = new List<PendingDelay>();
        private readonly List<Action> _tickers = new List<Action>();
        private readonly List<Action> _runNow = new List<Action>();

        private sealed class PendingDelay
        {
            public float Due;
            public TaskCompletionSource<bool> Tcs = new TaskCompletionSource<bool>();
            public CancellationTokenRegistration Registration;
        }

        /// <summary>Raised on the main thread when the application pauses or quits (used to flush events).</summary>
        internal static event Action? Suspending;

        /// <summary>True when called on Unity's main thread.</summary>
        public static bool IsMainThread => _mainThreadId == -1 || Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            Suspending = null;
            lock (PostLock) Posted.Clear();
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>Returns the runner, creating it if needed. Main thread only.</summary>
        internal static EnluRunner Instance
        {
            get
            {
                if (_instance != null) return _instance;
                if (_mainThreadId == -1) _mainThreadId = Thread.CurrentThread.ManagedThreadId;
                var go = new GameObject("[ENLU]");
                if (Application.isPlaying) DontDestroyOnLoad(go);
                else go.hideFlags = HideFlags.HideAndDontSave; // never saved into an edited scene
                _instance = go.AddComponent<EnluRunner>();
                return _instance;
            }
        }

        /// <summary>The runner if it exists (never creates one).</summary>
        internal static EnluRunner? Current => _instance;

        /// <summary>Makes sure the runner exists. Call from the main thread.</summary>
        public static void Ensure() => _ = Instance;

        /// <summary>Queues an action to run on the main thread on the next frame. Thread-safe.</summary>
        public static void Post(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            lock (PostLock) Posted.Enqueue(action);
        }

        /// <summary>Waits <paramref name="seconds"/> of real time without threads (works on WebGL).</summary>
        internal Task Delay(double seconds, CancellationToken ct)
        {
            if (seconds <= 0) return Task.CompletedTask;
            var d = new PendingDelay { Due = Time.realtimeSinceStartup + (float)seconds };
            if (ct.CanBeCanceled)
            {
                d.Registration = ct.Register(() => Post(() => d.Tcs.TrySetCanceled()));
            }
            _delays.Add(d);
            return d.Tcs.Task;
        }

        /// <summary>Adds a callback that runs every frame (main thread).</summary>
        internal void AddTicker(Action tick) => _tickers.Add(tick);

        /// <summary>Removes a per-frame callback.</summary>
        internal void RemoveTicker(Action tick) => _tickers.Remove(tick);

        private void Update()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            lock (PostLock)
            {
                while (Posted.Count > 0) _runNow.Add(Posted.Dequeue());
            }
            for (var i = 0; i < _runNow.Count; i++)
            {
                try
                {
                    _runNow[i]();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
            _runNow.Clear();

            if (_delays.Count > 0)
            {
                var now = Time.realtimeSinceStartup;
                for (var i = _delays.Count - 1; i >= 0; i--)
                {
                    var d = _delays[i];
                    if (d.Tcs.Task.IsCompleted)
                    {
                        d.Registration.Dispose();
                        _delays.RemoveAt(i);
                    }
                    else if (now >= d.Due)
                    {
                        d.Registration.Dispose();
                        _delays.RemoveAt(i);
                        d.Tcs.TrySetResult(true);
                    }
                }
            }

            for (var i = 0; i < _tickers.Count; i++)
            {
                try
                {
                    _tickers[i]();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) RaiseSuspending();
        }

        private void OnApplicationQuit() => RaiseSuspending();

        private static void RaiseSuspending()
        {
            try
            {
                Suspending?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            foreach (var d in _delays) d.Tcs.TrySetCanceled();
            _delays.Clear();
        }
    }

    /// <summary>The Unity scheduler: frame-based delays on the main thread (WebGL-safe).</summary>
    public sealed class EnluUnityScheduler : IEnluScheduler
    {
        /// <inheritdoc />
        public Task Delay(double seconds, CancellationToken cancellationToken) => EnluRunner.Instance.Delay(seconds, cancellationToken);
    }
}
