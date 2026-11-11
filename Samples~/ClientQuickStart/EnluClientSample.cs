using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enlu;
using UnityEngine;

namespace Enlu.Samples
{
    /// <summary>
    /// ENLU client quick start. Add this component to any GameObject, assign an EnluSettings asset
    /// (or keep one at Resources/EnluSettings) and press Play. It logs in with the device id, saves
    /// data, submits a score, shows the leaderboard, calls a cloud function and asks for a match server.
    /// Each step is independent, so one failing (e.g. no cloud script yet) does not stop the others.
    /// </summary>
    public sealed class EnluClientSample : MonoBehaviour
    {
        [Tooltip("Leave empty to use Resources/EnluSettings.")]
        [SerializeField] private EnluSettings settings;

        [Tooltip("A statistic defined in the console with 'client writable' on.")]
        [SerializeField] private string statistic = "score";

        [Tooltip("A cloud function name (handlers.<name> in your cloud script).")]
        [SerializeField] private string functionName = "grantDailyReward";

        [Tooltip("A game-server workload name. Requests need 'client allocations' enabled for the app.")]
        [SerializeField] private string workload = "arena";

        [Tooltip("Preferred regions, best first. Empty = any.")]
        [SerializeField] private string[] regions = new string[0];

        /// <summary>Saved with JsonUtility: any [Serializable] class works as a data value.</summary>
        [Serializable]
        public sealed class SaveGame
        {
            public int level = 1;
            public int coins;
            public List<string> unlocked = new List<string>();
        }

        private EnluClient _client;
        private readonly List<string> _lines = new List<string>();
        private Vector2 _scroll;
        private bool _busy;

        private async void Start()
        {
            try
            {
                _client = settings != null ? new EnluClient(settings) : EnluClient.Default;
                _client.SessionExpired += () => Log("Session expired: log in again.");
            }
            catch (Exception e)
            {
                Log("Setup failed: " + e.Message);
                return;
            }
            await RunAll();
        }

        private async Task RunAll()
        {
            _busy = true;
            await Step("Log in with device id", LoginAsync);
            await Step("Save player data", SaveAsync);
            await Step("Load player data", LoadAsync);
            await Step("Submit a score", SubmitScoreAsync);
            await Step("Show the leaderboard", ShowLeaderboardAsync);
            await Step("Call a cloud function", CallFunctionAsync);
            await Step("Request a match server", RequestMatchAsync);
            _busy = false;
        }

        private async Task Step(string title, Func<Task> action)
        {
            Log("> " + title);
            try
            {
                await action();
            }
            catch (EnluException e)
            {
                // Code is machine-readable (unauthorized, forbidden, not_found, unavailable, network_error…).
                Log("  failed: " + e.Code + " (HTTP " + e.HttpStatus + "): " + e.Message);
            }
            catch (Exception e)
            {
                Log("  failed: " + e.Message);
            }
        }

        private async Task LoginAsync()
        {
            if (_client.IsLoggedIn)
            {
                Log("  already logged in as " + _client.PlayerId + " (restored session)");
                return;
            }
            var login = await _client.LoginWithDeviceIdAsync();
            Log("  player " + login.PlayerId + (login.NewlyCreated ? " (new account)" : ""));
            if (login.NewlyCreated) await _client.UpdateDisplayNameAsync("Player " + login.PlayerId.Substring(Math.Max(0, login.PlayerId.Length - 4)));
        }

        private async Task SaveAsync()
        {
            var save = new SaveGame { level = 3, coins = 120 };
            save.unlocked.Add("bow");
            var written = await _client.SetDataAsync(new Dictionary<string, object>
            {
                ["save"] = save, // [Serializable] class, written through JsonUtility
                ["settings"] = new Dictionary<string, object> { ["music"] = 0.8, ["language"] = "en" },
                ["lastPlayedUtc"] = DateTime.UtcNow,
            });
            await _client.SetDataAsync("title", "Desert Explorer", DataVisibility.Public); // other players can read this one
            Log("  wrote " + written + " keys");
        }

        private async Task LoadAsync()
        {
            var data = await _client.GetDataAsync("save", "settings");
            if (data.TryGetValue("save", out var entry))
            {
                var save = entry.GetValue<SaveGame>();
                Log("  level " + save.level + ", coins " + save.coins + ", unlocked " + string.Join(", ", save.unlocked));
            }
            var title = await _client.GetTitleDataAsync();
            Log("  title data keys: " + string.Join(", ", title.Keys));
        }

        private async Task SubmitScoreAsync()
        {
            var score = UnityEngine.Random.Range(100, 5000);
            var stored = await _client.UpdateStatisticAsync(statistic, score);
            Log("  submitted " + score + ", stored " + stored[statistic]);
            _client.QueueEvent("score_submitted", new Dictionary<string, object> { ["score"] = score }); // sent in batches
        }

        private async Task ShowLeaderboardAsync()
        {
            var top = await _client.GetLeaderboardAsync(statistic, 10);
            foreach (var e in top.Entries) Log("  " + e);
            var me = await _client.GetLeaderboardAsync(statistic, 5, aroundMe: true);
            Log("  around me: " + string.Join(" | ", me.Entries));
        }

        private async Task CallFunctionAsync()
        {
            var res = await _client.ExecuteFunctionAsync<Dictionary<string, object>>(functionName,
                new Dictionary<string, object> { ["source"] = "sample" });
            if (!res.Ok)
            {
                Log("  the function failed: " + res.Error);
                return;
            }
            Log("  result " + EnluJson.Serialize(res.RawResult) + " in " + res.Ms + " ms");
        }

        private async Task RequestMatchAsync()
        {
            var session = await _client.RequestSessionAsync(workload, regions,
                new Dictionary<string, string> { ["mode"] = "duel" });
            // Connect your network transport (Netcode, Mirror, FishNet…) to this address and port.
            Log("  connect to " + (session.GetEndpoint("game") ?? session.GetEndpoint() ?? "(no public address)") +
                " in " + session.Region + ", session " + session.SessionId);
        }

        private void Log(string line)
        {
            _lines.Add(line);
            Debug.Log("[ENLU sample] " + line);
            _scroll.y = float.MaxValue;
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, Mathf.Min(Screen.width - 20, 720), Screen.height - 20), GUI.skin.box);
            GUILayout.Label("ENLU client sample");
            using (new GUILayout.HorizontalScope())
            {
                GUI.enabled = !_busy && _client != null;
                if (GUILayout.Button("Run all again")) _ = RunAll();
                if (GUILayout.Button("Submit score")) _ = Step("Submit a score", SubmitScoreAsync);
                if (GUILayout.Button("Request match")) _ = Step("Request a match server", RequestMatchAsync);
                if (GUILayout.Button("Flush events")) _ = Step("Flush events", async () => Log("  sent " + await _client.FlushAsync()));
                if (GUILayout.Button("Log out")) _ = Step("Log out", () => _client.LogoutAsync());
                GUI.enabled = true;
            }
            _scroll = GUILayout.BeginScrollView(_scroll);
            foreach (var l in _lines) GUILayout.Label(l);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
