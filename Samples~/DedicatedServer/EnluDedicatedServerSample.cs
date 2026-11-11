using System;
using System.Collections.Generic;
using Enlu;
using UnityEngine;

namespace Enlu.Samples
{
    /// <summary>
    /// A dedicated server lifecycle on an ENLU game-server fleet, using the game server SDK (GSDK).
    /// Put it in the first scene of your Linux Dedicated Server build. In the Editor (no ENLU_GSDK_URL)
    /// it runs in local mode and is "allocated" about two seconds after it is ready.
    /// <para>Wire your network transport (Netcode for GameObjects, Mirror, FishNet…) to the marked
    /// places: listen on <see cref="EnluGameServer.GetContainerPort"/>, and call <see cref="PlayerJoined"/>
    /// and <see cref="PlayerLeft"/> from its connection callbacks.</para>
    /// </summary>
    public sealed class EnluDedicatedServerSample : MonoBehaviour
    {
        [Tooltip("Port name from the workload spec (ports[].name).")]
        [SerializeField] private string portName = "game";

        [Tooltip("Port used when not running on ENLU (Editor, local tests).")]
        [SerializeField] private int localPort = 7777;

        [Tooltip("End the match after this many seconds.")]
        [SerializeField] private float maxMatchSeconds = 900;

        [Tooltip("End the match when nobody has been connected for this long after allocation.")]
        [SerializeField] private float emptyTimeoutSeconds = 120;

        [Tooltip("Server frame rate (lower = less CPU per server).")]
        [SerializeField] private int serverFrameRate = 30;

        private readonly List<string> _players = new List<string>();
        private bool _listening;
        private bool _ending;
        private float _allocatedAt = -1;
        private float _lastPlayerSeenAt;

        private void Awake()
        {
            Application.targetFrameRate = serverFrameRate;
            DontDestroyOnLoad(gameObject);

            EnluGameServer.OnAllocated += OnAllocated;
            EnluGameServer.OnShutdown += OnShutdown;
            EnluGameServer.OnHealthCheck += () => _listening; // report Unhealthy if the transport is down

            // 1. Start the GSDK first: the agent expects heartbeats within the health grace period.
            EnluGameServer.Start(new Dictionary<string, string> { ["map"] = "desert", ["mode"] = "duel" });
            Debug.Log("[server] instance " + EnluGameServer.InstanceId + " in " + EnluGameServer.Region +
                      (EnluGameServer.IsLocalMode ? " (local mode)" : ""));
        }

        private void Start()
        {
            // 2. Load what you need, then start listening on the container port.
            var port = EnluGameServer.GetContainerPort(portName, localPort);
            // TODO: start your network transport on `port` here, e.g. for Netcode for GameObjects:
            //   transport.SetConnectionData("0.0.0.0", (ushort)port, "0.0.0.0"); NetworkManager.Singleton.StartServer();
            _listening = true;
            Debug.Log("[server] listening on " + port + "; players connect to " +
                      EnluGameServer.PublicAddress + ":" + EnluGameServer.GetPublicPort(portName, port));

            // 3. Ready: the fleet may now hand this server to a match.
            EnluGameServer.ReadyForPlayers();
        }

        private void OnAllocated(SessionInfo session)
        {
            // 4. A matchmaker (or a client) allocated this server.
            _allocatedAt = Time.realtimeSinceStartup;
            _lastPlayerSeenAt = _allocatedAt;
            Debug.Log("[server] allocated to " + session.SessionId + ", map " + session.GetMetadata("map", "default") +
                      ", expecting " + string.Join(", ", session.InitialPlayers));
            // TODO: load the map / mode from session.Metadata.
        }

        /// <summary>Call from your transport when a player connects (use their ENLU player id).</summary>
        public void PlayerJoined(string playerId)
        {
            if (_players.Contains(playerId)) return;
            var expected = EnluGameServer.Session?.InitialPlayers;
            if (expected != null && expected.Count > 0 && !Contains(expected, playerId))
                Debug.LogWarning("[server] " + playerId + " was not in the allocation's player list");
            _players.Add(playerId);
            EnluGameServer.UpdateConnectedPlayers(_players);
        }

        /// <summary>Call from your transport when a player disconnects.</summary>
        public void PlayerLeft(string playerId)
        {
            if (_players.Remove(playerId)) EnluGameServer.UpdateConnectedPlayers(_players);
        }

        private void Update()
        {
            if (_allocatedAt < 0 || _ending) return;
            var now = Time.realtimeSinceStartup;
            if (_players.Count > 0) _lastPlayerSeenAt = now;
            if (now - _allocatedAt > maxMatchSeconds) EndMatch("time limit");
            else if (now - _lastPlayerSeenAt > emptyTimeoutSeconds) EndMatch("everyone left");
        }

        private async void EndMatch(string reason)
        {
            _ending = true;
            Debug.Log("[server] match over: " + reason);
#if UNITY_SERVER || UNITY_EDITOR || ENLU_SERVER
            // 5. Optional: record results with server authority. The API key comes from the ENLU_API_KEY
            // environment variable (map it from an ENLU secret in the workload spec). Never ship a key in a client.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnluServerApi.ApiKeyEnv)))
            {
                try
                {
                    var api = new EnluServerApi((EnluSettings)null); // base URL from ENLU_API_URL, app from ENLU_APP_ID
                    foreach (var p in _players)
                        await api.UpdatePlayerStatisticsAsync(p, new Dictionary<string, double> { ["matches_played"] = 1 });
                    await api.WriteEventAsync("match_end", new Dictionary<string, object>
                    {
                        ["session"] = EnluGameServer.Session?.SessionId,
                        ["reason"] = reason,
                        ["players"] = _players.Count,
                    });
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[server] could not record results: " + e.Message);
                }
            }
#endif
            // 6. Exit. The fleet starts a fresh standby server to replace this one.
            EnluGameServer.Shutdown(0);
        }

        private void OnShutdown()
        {
            // The platform is stopping this server (drain, scale-down, session ended from the backend).
            Debug.Log("[server] shutdown requested; telling players and saving state");
            // TODO: notify connected clients and flush anything important. Keep it short.
        }

        private static bool Contains(IReadOnlyList<string> list, string value)
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i] == value) return true;
            return false;
        }
    }
}
