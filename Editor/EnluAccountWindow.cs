#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Enlu.Editor
{
    /// <summary>
    /// <b>Tools &gt; ENLU &gt; Account</b>: sign in to ENLU from the Editor through your browser and
    /// manage apps, statistics, title data, cloud scripts and game-server fleets without leaving Unity.
    /// </summary>
    public sealed class EnluAccountWindow : EditorWindow
    {
        private const string UrlPrefsKey = "ENLU.Account.ServerUrl"; // the server URL is not a secret
        private static readonly string[] AppKinds = { "game", "website", "webapp", "mobile", "social", "other" };
        private static readonly string[] Aggregations = { "last", "max", "min", "sum" };
        private static readonly string[] ResetPeriods = { "never", "hourly", "daily", "weekly", "monthly" };
        private static readonly string[] Sorts = { "desc", "asc" };
        private static readonly string[] Protocols = { "udp", "tcp" };

        [SerializeField] private string serverUrl = string.Empty;
        [SerializeField] private string selectedApp = string.Empty;
        [SerializeField] private Vector2 scroll;
        [SerializeField] private bool showWorkloads = true, showStats, showTitleData, showScripts, showServer, showEconomy;

        // Everything below is per-window memory only: never serialized, never written to disk.
        [NonSerialized] private EnluEditorAuth? _auth;
        [NonSerialized] private CancellationTokenSource? _cts;
        [NonSerialized] private string? _busy;
        [NonSerialized] private string? _error;
        [NonSerialized] private string? _info;
        [NonSerialized] private Dictionary<string, object?>? _account;
        [NonSerialized] private double? _balance;
        [NonSerialized] private List<Dictionary<string, object?>> _apps = new List<Dictionary<string, object?>>();
        [NonSerialized] private List<Dictionary<string, object?>> _workloads = new List<Dictionary<string, object?>>();
        [NonSerialized] private List<Dictionary<string, object?>> _stats = new List<Dictionary<string, object?>>();
        [NonSerialized] private string? _loadedFor;
        [NonSerialized] private string? _oneTimeServerKey;
        [NonSerialized] private string? _oneTimeKeyApp;

        [NonSerialized] private string _newAppName = string.Empty;
        [NonSerialized] private int _newAppKind;
        [NonSerialized] private string _statName = string.Empty;
        [NonSerialized] private int _statAgg = 1, _statReset, _statSort;
        [NonSerialized] private bool _statClientWritable;
        [NonSerialized] private Dictionary<string, object?>? _titleOriginal;
        [NonSerialized] private List<string> _titleInternal = new List<string>();
        [NonSerialized] private string _titleJson = string.Empty;
        [NonSerialized] private object? _liveRevision;
        [NonSerialized] private TextAsset? _scriptAsset;
        [NonSerialized] private string? _scriptFile;
        [NonSerialized] private string _scriptNote = string.Empty;
        [NonSerialized] private string _fnName = string.Empty;
        [NonSerialized] private string _fnArgs = "{}";
        [NonSerialized] private string _fnOutput = string.Empty;
        [NonSerialized] private string _wlName = "arena", _wlImage = string.Empty, _wlPort = "game", _wlProto = "udp";
        [NonSerialized] private int _wlContainerPort = 7777, _wlStandby = 1, _wlMax = 10, _wlMemory = 1024;
        [NonSerialized] private float _wlCpu = 1f;
        [NonSerialized] private List<Dictionary<string, object?>>? _currencies;
        [NonSerialized] private List<Dictionary<string, object?>> _catalog = new List<Dictionary<string, object?>>();
        [NonSerialized] private string _curCode = string.Empty, _curName = string.Empty;
        [NonSerialized] private long _curInitial, _curMax;
        [NonSerialized] private string _itemId = string.Empty, _itemName = string.Empty;
        [NonSerialized] private int _itemCurrency;
        [NonSerialized] private long _itemPrice = 100;
        [NonSerialized] private bool _itemStackable = true, _itemConsumable;
        [NonSerialized] private Dictionary<string, object?>? _itemEditing;
        [NonSerialized] private string _grantPlayer = string.Empty, _grantNote = "Granted from the Unity Editor";
        [NonSerialized] private int _grantItem, _grantItemCount = 1, _grantCurrency;
        [NonSerialized] private long _grantAmount = 100;

        /// <summary>Opens the window.</summary>
        [MenuItem("Tools/ENLU/Account", false, 1)]
        public static void Open()
        {
            var w = GetWindow<EnluAccountWindow>();
            w.titleContent = new GUIContent("ENLU Account");
            w.minSize = new Vector2(420, 360);
            w.Show();
        }

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(serverUrl))
            {
                var settings = EnluSettings.Load();
                serverUrl = settings != null && settings.BaseUrl.Trim() != "https://" && settings.BaseUrl.Trim().Length > 0
                    ? settings.BaseUrl
                    : EditorPrefs.GetString(UrlPrefsKey, "https://");
            }
            Bind();
        }

        private void OnDisable()
        {
            if (_auth != null) _auth.Changed -= Repaint;
            _cts?.Cancel();
        }

        private void Bind()
        {
            if (_auth != null) _auth.Changed -= Repaint;
            _auth = null;
            _loadedFor = null;
            try
            {
                _auth = EnluEditorSession.For(serverUrl);
            }
            catch (ArgumentException)
            {
                return;
            }
            _auth.Changed += Repaint;
            // A stored sign-in is restored quietly; OnGUI loads the account once signed in.
            if (_auth.Status == EnluAuthStatus.SignedOut || _auth.Status == EnluAuthStatus.SignedIn) RunInBackground(RestoreAsync);
        }

        /* ------------------------------ async plumbing ------------------------------ */

        private CancellationToken NewToken()
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            return _cts.Token;
        }

        private async void Run(string label, Func<CancellationToken, Task> op)
        {
            if (_busy != null) return;
            _busy = label;
            _error = null;
            _info = null;
            Repaint();
            try
            {
                await op(NewToken());
            }
            catch (OperationCanceledException)
            {
                _info = "Cancelled.";
            }
            catch (EnluException e)
            {
                _error = Describe(e);
            }
            catch (Exception e)
            {
                _error = e.Message;
            }
            finally
            {
                _busy = null;
                Repaint();
            }
        }

        private async void RunInBackground(Func<CancellationToken, Task> op)
        {
            try
            {
                await op(CancellationToken.None);
            }
            catch (EnluException e)
            {
                _error = Describe(e);
            }
            catch (Exception e)
            {
                _error = e.Message;
            }
            Repaint();
        }

        private string Describe(EnluException e)
        {
            if (e.HttpStatus == 403 && e.Code == "forbidden")
                return "Your team role" + (_auth?.User != null ? " (" + _auth.User.Role + ")" : "") + " does not allow this: " + e.Message;
            if (e.IsNetworkError) return "Cannot reach " + serverUrl + ": " + e.Message;
            return e.Message + " (" + e.Code + ")";
        }

        private async Task RestoreAsync(CancellationToken ct)
        {
            if (_auth != null) await _auth.TryRestoreAsync(ct);
        }

        private async Task LoadAccountAsync(CancellationToken ct)
        {
            if (_auth == null || !_auth.IsSignedIn) return;
            var api = new EnluDashboardApi(_auth);
            _account = await api.GetAccountAsync(ct);
            _balance = await api.GetBalanceAsync(ct);
            _apps = await api.ListAppsAsync(ct);
            _loadedFor = _auth.BaseUrl;
            if (_apps.Count > 0 && !_apps.Any(a => EnluJson.GetString(a, "name") == selectedApp))
                selectedApp = EnluJson.GetString(_apps[0], "name") ?? string.Empty;
            if (!string.IsNullOrEmpty(selectedApp)) await LoadAppAsync(ct);
        }

        private async Task LoadAppAsync(CancellationToken ct)
        {
            if (_auth == null || string.IsNullOrEmpty(selectedApp)) return;
            var api = new EnluDashboardApi(_auth);
            _workloads = await api.ListWorkloadsAsync(selectedApp, ct);
            _stats = await api.ListStatisticsAsync(selectedApp, ct);
            try
            {
                _liveRevision = EnluJson.Get(await api.GetScriptsAsync(selectedApp, ct), "liveRevision");
            }
            catch (EnluException e) when (e.HttpStatus == 403)
            {
                _liveRevision = null;
            }
            _titleOriginal = null;
            _titleJson = string.Empty;
            _currencies = null;
            _catalog.Clear();
        }

        /* ------------------------------------ GUI ------------------------------------ */

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawServer();
            if (_auth != null && _auth.IsSignedIn)
            {
                if (_loadedFor != _auth.BaseUrl && _busy == null) Run("Loading account…", LoadAccountAsync);
                DrawAccount();
                DrawApps();
                if (!string.IsNullOrEmpty(selectedApp)) DrawSelectedApp();
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawServer()
        {
            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(_auth != null && _auth.Status == EnluAuthStatus.SigningIn))
            {
                EditorGUI.BeginChangeCheck();
                var url = EditorGUILayout.DelayedTextField("Server URL", serverUrl);
                if (EditorGUI.EndChangeCheck())
                {
                    serverUrl = url.Trim();
                    EditorPrefs.SetString(UrlPrefsKey, serverUrl);
                    _account = null;
                    _apps.Clear();
                    Bind();
                }
            }
            if (_auth == null)
            {
                EditorGUILayout.HelpBox("Enter the URL of your ENLU server, e.g. https://api.example.com.", MessageType.Info);
                return;
            }
            if (!EnluEditorSession.IsSafeUrl(serverUrl))
                EditorGUILayout.HelpBox("This server uses plain http. Your sign-in tokens would travel unencrypted. Use https:// (http is fine only for localhost).", MessageType.Warning);

            var status = _auth.Status switch
            {
                EnluAuthStatus.SignedIn => "Signed in as " + (_auth.User?.Email ?? "?"),
                EnluAuthStatus.SigningIn => "Waiting for you to approve in the browser…",
                EnluAuthStatus.Restoring => "Restoring your sign-in…",
                _ => "Not signed in",
            };
            EditorGUILayout.LabelField("Status", status);
            if (!string.IsNullOrEmpty(_auth.LastMessage) && _auth.Status != EnluAuthStatus.SignedIn)
                EditorGUILayout.HelpBox(_auth.LastMessage, MessageType.None);
            if (!_auth.IsPersistent)
                EditorGUILayout.HelpBox("No secure credential store was found on this computer, so the sign-in is kept for this Unity session only. You will sign in again after restarting Unity.", MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                switch (_auth.Status)
                {
                    case EnluAuthStatus.SignedOut:
                        if (GUILayout.Button("Sign in with browser")) SignIn();
                        break;
                    case EnluAuthStatus.SigningIn:
                        if (GUILayout.Button("Cancel")) _auth.CancelSignIn();
                        break;
                    case EnluAuthStatus.SignedIn:
                        using (new EditorGUI.DisabledScope(_busy != null))
                        {
                            if (GUILayout.Button("Refresh")) Run("Loading…", LoadAccountAsync);
                            if (GUILayout.Button("Sign out")) Run("Signing out…", SignOutAsync);
                        }
                        break;
                }
                if (GUILayout.Button("Open dashboard")) Application.OpenURL(_auth.BaseUrl + "/dashboard/");
            }
            if (_busy != null)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(_busy, EditorStyles.miniLabel);
                    if (GUILayout.Button("Cancel", GUILayout.Width(70))) _cts?.Cancel();
                }
            }
            if (_error != null) EditorGUILayout.HelpBox(_error, MessageType.Error);
            if (_info != null) EditorGUILayout.HelpBox(_info, MessageType.Info);
            EditorGUILayout.Space();
        }

        private async void SignIn()
        {
            if (_auth == null) return;
            _error = null;
            _info = "Your browser opened the ENLU sign-in page. Sign in there and approve Unity Editor. Unity never sees your password.";
            try
            {
                await _auth.SignInAsync(Application.OpenURL);
                _info = null;
                Run("Loading account…", LoadAccountAsync);
            }
            catch (OperationCanceledException)
            {
                _info = "Sign-in cancelled.";
            }
            catch (EnluException e)
            {
                _info = null;
                _error = Describe(e);
            }
            Repaint();
        }

        private async Task SignOutAsync(CancellationToken ct)
        {
            if (_auth == null) return;
            await _auth.SignOutAsync(ct);
            _account = null;
            _balance = null;
            _apps.Clear();
            _workloads.Clear();
            _stats.Clear();
            _oneTimeServerKey = null;
            _loadedFor = null;
        }

        private void DrawAccount()
        {
            EditorGUILayout.LabelField("Account", EditorStyles.boldLabel);
            var user = _auth!.User;
            EditorGUILayout.LabelField("Email", user?.Email ?? "");
            EditorGUILayout.LabelField("Role", user?.Role ?? "");
            EditorGUILayout.LabelField("Team", _auth.Account?.Name ?? EnluJson.GetString(_account, "name") ?? "");
            var plan = EnluJson.GetObject(_account, "plan");
            if (plan != null) EditorGUILayout.LabelField("Plan", EnluJson.GetString(plan, "name") ?? EnluJson.GetString(plan, "id") ?? "");
            if (_balance.HasValue) EditorGUILayout.LabelField("Balance", _balance.Value.ToString("0.##", CultureInfo.InvariantCulture) + " credits");
            EditorGUILayout.LabelField("Sign-in stored in", _auth.StoreDescription, EditorStyles.miniLabel);
            EditorGUILayout.Space();
        }

        private void DrawApps()
        {
            EditorGUILayout.LabelField("Apps", EditorStyles.boldLabel);
            if (_oneTimeServerKey != null) DrawOneTimeKey();
            if (_apps.Count == 0) EditorGUILayout.HelpBox("No apps yet. Create one below.", MessageType.None);
            foreach (var app in _apps)
            {
                var name = EnluJson.GetString(app, "name") ?? "";
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    var selected = name == selectedApp;
                    var label = name + "  (" + (EnluJson.GetString(app, "kind") ?? "") + ")  players " + EnluJson.GetLong(app, "players") +
                                ", workloads " + EnluJson.GetLong(app, "workloads") + ", running " + EnluJson.GetLong(app, "runningInstances");
                    if (GUILayout.Toggle(selected, label, EditorStyles.radioButton) && !selected)
                    {
                        selectedApp = name;
                        Run("Loading app…", LoadAppAsync);
                    }
                    if (GUILayout.Button(new GUIContent("Use this app", "Write this server URL and app into your EnluSettings asset."), GUILayout.Width(100)))
                        UseApp(name);
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _newAppName = EditorGUILayout.TextField("New app", _newAppName);
                _newAppKind = EditorGUILayout.Popup(_newAppKind, AppKinds, GUILayout.Width(80));
                using (new EditorGUI.DisabledScope(_busy != null || _newAppName.Trim().Length == 0))
                {
                    if (GUILayout.Button("Create", GUILayout.Width(70))) Run("Creating app…", CreateAppAsync);
                }
            }
            EditorGUILayout.LabelField("Lower-case letters, digits and dashes; unique on the server.", EditorStyles.miniLabel);
            EditorGUILayout.Space();
        }

        private async Task CreateAppAsync(CancellationToken ct)
        {
            var api = new EnluDashboardApi(_auth!);
            var created = await api.CreateAppAsync(_newAppName.Trim(), AppKinds[_newAppKind], ct);
            _oneTimeServerKey = EnluJson.GetString(created, "serverKey");
            _oneTimeKeyApp = EnluJson.GetString(created, "name");
            selectedApp = _oneTimeKeyApp ?? selectedApp;
            _newAppName = string.Empty;
            _apps = await api.ListAppsAsync(ct);
            await LoadAppAsync(ct);
        }

        private void DrawOneTimeKey()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.HelpBox("Server API key for '" + _oneTimeKeyApp + "'. It is shown only this once and is not saved anywhere. " +
                                        "Copy it into your server's secret store (e.g. enlu secret set). Never put it in your game, in assets or in source control.",
                    MessageType.Warning);
                EditorGUILayout.SelectableLabel(_oneTimeServerKey, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Copy")) EditorGUIUtility.systemCopyBuffer = _oneTimeServerKey;
                    if (GUILayout.Button("I have stored it, hide it"))
                    {
                        _oneTimeServerKey = null;
                        _oneTimeKeyApp = null;
                    }
                }
            }
        }

        private void UseApp(string appName)
        {
            var settings = EnluSettings.Load();
            if (settings == null)
            {
                var guid = AssetDatabase.FindAssets("t:" + nameof(EnluSettings)).FirstOrDefault();
                if (guid != null) settings = AssetDatabase.LoadAssetAtPath<EnluSettings>(AssetDatabase.GUIDToAssetPath(guid));
            }
            if (settings == null)
            {
                EnluMenu.SelectOrCreateSettings();
                settings = EnluSettings.Load();
            }
            if (settings == null)
            {
                _error = "Could not find or create an EnluSettings asset.";
                return;
            }
            Undo.RecordObject(settings, "Use ENLU app");
            settings.BaseUrl = _auth!.BaseUrl;
            settings.App = appName;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            _info = "EnluSettings now uses app '" + appName + "' on " + _auth.BaseUrl + ".";
            EditorGUIUtility.PingObject(settings);
        }

        /* ------------------------------- selected app ------------------------------- */

        private void DrawSelectedApp()
        {
            EditorGUILayout.LabelField("App: " + selectedApp, EditorStyles.boldLabel);

            showWorkloads = EditorGUILayout.Foldout(showWorkloads, "Workloads (" + _workloads.Count + ")", true);
            if (showWorkloads)
            {
                if (_workloads.Count == 0) EditorGUILayout.LabelField("No workloads.", EditorStyles.miniLabel);
                foreach (var w in _workloads)
                {
                    var spec = EnluJson.GetObject(w, "spec");
                    var counts = EnluDashboardApi.CountInstances(w);
                    var live = string.Join(", ", counts.Select(kv => kv.Key + " " + kv.Value));
                    EditorGUILayout.LabelField(EnluJson.GetString(w, "name") ?? "", (EnluJson.GetString(spec, "kind") ?? "") + "  |  " + (live.Length > 0 ? live : "no instances"));
                }
            }

            showStats = EditorGUILayout.Foldout(showStats, "Statistics (" + _stats.Count + ")", true);
            if (showStats) DrawStatistics();

            showTitleData = EditorGUILayout.Foldout(showTitleData, "Title data", true);
            if (showTitleData) DrawTitleData();

            showScripts = EditorGUILayout.Foldout(showScripts, "Cloud scripts", true);
            if (showScripts) DrawScripts();

            showEconomy = EditorGUILayout.Foldout(showEconomy, "Economy", true);
            if (showEconomy) DrawEconomy();

            showServer = EditorGUILayout.Foldout(showServer, "Dedicated server fleet", true);
            if (showServer) DrawServerHelper();
        }

        private void DrawStatistics()
        {
            foreach (var s in _stats)
            {
                EditorGUILayout.LabelField(EnluJson.GetString(s, "name") ?? "",
                    (EnluJson.GetString(s, "aggregation") ?? "") + ", reset " + (EnluJson.GetString(s, "resetPeriod") ?? "") + ", " +
                    (EnluJson.GetString(s, "sort") ?? "") + (EnluJson.GetBool(s, "clientWritable") ? ", client-writable" : ", server only"));
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _statName = EditorGUILayout.TextField("Name", _statName);
                _statAgg = EditorGUILayout.Popup("Aggregation", _statAgg, Aggregations);
                _statReset = EditorGUILayout.Popup("Reset", _statReset, ResetPeriods);
                _statSort = EditorGUILayout.Popup("Sort", _statSort, Sorts);
                _statClientWritable = EditorGUILayout.Toggle(new GUIContent("Client writable", "Players can submit values. Leave off for anything competitive."), _statClientWritable);
                using (new EditorGUI.DisabledScope(_busy != null || !EnluIds.IsValidName(_statName)))
                {
                    if (GUILayout.Button("Define statistic"))
                    {
                        Run("Saving statistic…", async ct =>
                        {
                            var api = new EnluDashboardApi(_auth!);
                            await api.DefineStatisticAsync(selectedApp, _statName, Aggregations[_statAgg], ResetPeriods[_statReset], Sorts[_statSort], _statClientWritable, ct);
                            _stats = await api.ListStatisticsAsync(selectedApp, ct);
                            _info = "Statistic '" + _statName + "' saved.";
                        });
                    }
                }
            }
        }

        private void DrawTitleData()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_busy != null))
                {
                    if (GUILayout.Button("Load"))
                    {
                        Run("Loading title data…", async ct =>
                        {
                            var r = await new EnluDashboardApi(_auth!).GetTitleDataAsync(selectedApp, ct);
                            _titleOriginal = EnluJson.GetObject(r, "data") ?? new Dictionary<string, object?>();
                            _titleInternal = EnluJson.GetStringList(r, "internalKeys");
                            _titleJson = EnluJson.Serialize(_titleOriginal, true);
                        });
                    }
                    using (new EditorGUI.DisabledScope(_titleOriginal == null))
                    {
                        if (GUILayout.Button("Save")) SaveTitleData();
                    }
                }
            }
            if (_titleOriginal == null) return;
            if (_titleInternal.Count > 0) EditorGUILayout.LabelField("Internal (server-only) keys: " + string.Join(", ", _titleInternal), EditorStyles.miniLabel);
            _titleJson = EditorGUILayout.TextArea(_titleJson, GUILayout.MinHeight(120));
            EditorGUILayout.LabelField("A JSON object. Removing a key deletes it on Save.", EditorStyles.miniLabel);
        }

        private void SaveTitleData()
        {
            if (!EnluJson.TryParse(_titleJson, out var node) || !(node is Dictionary<string, object?> edited))
            {
                _error = "Title data must be a JSON object.";
                return;
            }
            var original = _titleOriginal!;
            Run("Saving title data…", async ct =>
            {
                var api = new EnluDashboardApi(_auth!);
                await api.SaveTitleDataAsync(selectedApp, original, _titleInternal, edited, ct);
                var r = await api.GetTitleDataAsync(selectedApp, ct);
                _titleOriginal = EnluJson.GetObject(r, "data") ?? new Dictionary<string, object?>();
                _titleInternal = EnluJson.GetStringList(r, "internalKeys");
                _titleJson = EnluJson.Serialize(_titleOriginal, true);
                _info = "Title data saved.";
            });
        }

        private void DrawScripts()
        {
            EditorGUILayout.LabelField("Live revision", _liveRevision == null ? "none" : Convert.ToString(_liveRevision, CultureInfo.InvariantCulture));
            using (new EditorGUILayout.HorizontalScope())
            {
                _scriptAsset = (TextAsset?)EditorGUILayout.ObjectField("Script (.js)", _scriptAsset, typeof(TextAsset), false);
                if (GUILayout.Button("Choose file…", GUILayout.Width(100)))
                {
                    var path = EditorUtility.OpenFilePanel("Cloud script", Application.dataPath, "js");
                    if (!string.IsNullOrEmpty(path))
                    {
                        _scriptFile = path;
                        _scriptAsset = null;
                    }
                }
            }
            if (_scriptAsset == null && _scriptFile != null) EditorGUILayout.LabelField("File", Path.GetFileName(_scriptFile), EditorStyles.miniLabel);
            _scriptNote = EditorGUILayout.TextField("Note", _scriptNote);
            using (new EditorGUI.DisabledScope(_busy != null || (_scriptAsset == null && _scriptFile == null)))
            {
                if (GUILayout.Button("Push and publish"))
                {
                    var source = _scriptAsset != null ? _scriptAsset.text : File.ReadAllText(_scriptFile!);
                    Run("Pushing script…", async ct =>
                    {
                        var api = new EnluDashboardApi(_auth!);
                        var r = await api.PushScriptAsync(selectedApp, source, _scriptNote, ct);
                        _liveRevision = EnluJson.Get(await api.GetScriptsAsync(selectedApp, ct), "liveRevision");
                        _info = "Revision " + EnluJson.GetLong(r, "revision") + " is live.";
                    });
                }
            }

            EditorGUILayout.Space();
            _fnName = EditorGUILayout.TextField("Function", _fnName);
            EditorGUILayout.LabelField("Arguments (JSON)");
            _fnArgs = EditorGUILayout.TextArea(_fnArgs, GUILayout.MinHeight(40));
            using (new EditorGUI.DisabledScope(_busy != null || _fnName.Trim().Length == 0))
            {
                if (GUILayout.Button("Run"))
                {
                    if (!EnluJson.TryParse(_fnArgs, out var args))
                    {
                        _error = "The arguments are not valid JSON.";
                    }
                    else
                    {
                        Run("Running " + _fnName + "…", async ct =>
                        {
                            var r = await new EnluDashboardApi(_auth!).RunFunctionAsync(selectedApp, _fnName.Trim(), args, ct);
                            var lines = new List<string>
                            {
                                (EnluJson.GetBool(r, "ok") ? "ok" : "failed: " + EnluJson.GetString(r, "error")) + " in " +
                                EnluJson.GetDouble(r, "ms").ToString("0.#", CultureInfo.InvariantCulture) + " ms",
                                "result: " + EnluJson.Serialize(EnluJson.Get(r, "result"), true),
                            };
                            var logs = EnluJson.GetArray(r, "logs");
                            if (logs != null)
                                foreach (var l in logs)
                                    lines.Add("[" + EnluJson.GetString(EnluJson.AsObject(l), "level") + "] " + EnluJson.GetString(EnluJson.AsObject(l), "message"));
                            _fnOutput = string.Join("\n", lines);
                        });
                    }
                }
            }
            if (_fnOutput.Length > 0) EditorGUILayout.SelectableLabel(_fnOutput, EditorStyles.helpBox, GUILayout.MinHeight(60));
        }

        /* --------------------------------- economy --------------------------------- */

        private async Task LoadEconomyAsync(CancellationToken ct)
        {
            var api = new EnluDashboardApi(_auth!);
            _currencies = await api.ListCurrenciesAsync(selectedApp, ct);
            _catalog = await api.ListCatalogAsync(selectedApp, ct);
        }

        private string[] CurrencyCodes() => (_currencies ?? new List<Dictionary<string, object?>>()).Select(c => EnluJson.GetString(c, "code") ?? "").ToArray();

        private static string Amounts(Dictionary<string, object?>? map) =>
            map == null || map.Count == 0 ? "-" : string.Join(", ", map.Select(kv => kv.Key + " " + EnluJson.ToLong(kv.Value)));

        private void DrawEconomy()
        {
            EditorGUILayout.LabelField("Currencies, a simple item editor and a grant tester. Use the dashboard for stores, bundles, IAP products, achievements and segments.",
                EditorStyles.wordWrappedMiniLabel);
            if (_currencies == null)
            {
                using (new EditorGUI.DisabledScope(_busy != null))
                {
                    if (GUILayout.Button("Load economy")) Run("Loading economy…", LoadEconomyAsync);
                }
                return;
            }
            var codes = CurrencyCodes();
            DrawCurrencies(codes);
            DrawCatalog(codes);
            DrawGrantTester(codes);
            using (new EditorGUI.DisabledScope(_busy != null))
            {
                if (GUILayout.Button("Reload economy")) Run("Loading economy…", LoadEconomyAsync);
            }
        }

        private void DrawCurrencies(string[] codes)
        {
            EditorGUILayout.LabelField("Currencies", EditorStyles.miniBoldLabel);
            foreach (var c in _currencies!)
            {
                var max = EnluJson.Get(c, "max");
                EditorGUILayout.LabelField(EnluJson.GetString(c, "code") ?? "", (EnluJson.GetString(c, "name") ?? "") + ", start " + EnluJson.GetLong(c, "initial") +
                    (EnluJson.IsNumber(max) ? ", max " + EnluJson.ToLong(max) : "") + (EnluJson.IsNumber(EnluJson.Get(c, "rechargeRate")) ? ", recharges" : ""));
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _curCode = EditorGUILayout.TextField("Code / name", _curCode).ToUpperInvariant();
                    _curName = EditorGUILayout.TextField(_curName);
                }
                _curInitial = EditorGUILayout.LongField("Starting balance", _curInitial);
                _curMax = EditorGUILayout.LongField("Max (0 = none)", _curMax);
                var exists = codes.Contains(_curCode);
                var valid = System.Text.RegularExpressions.Regex.IsMatch(_curCode, "^[A-Z][A-Z0-9]{1,7}$") && _curName.Trim().Length > 0;
                using (new EditorGUI.DisabledScope(_busy != null || !valid))
                {
                    if (GUILayout.Button(exists ? "Update currency" : "Create currency"))
                    {
                        var code = _curCode;
                        Run("Saving currency…", async ct =>
                        {
                            await new EnluDashboardApi(_auth!).SaveCurrencyAsync(selectedApp, code, _curName.Trim(), _curInitial, _curMax > 0 ? _curMax : (long?)null, exists, ct);
                            await LoadEconomyAsync(ct);
                            _info = "Currency " + code + " saved.";
                        });
                    }
                }
            }
        }

        private void DrawCatalog(string[] codes)
        {
            EditorGUILayout.LabelField("Catalog (" + _catalog.Count + ")", EditorStyles.miniBoldLabel);
            foreach (var item in _catalog)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var flags = (EnluJson.GetBool(item, "stackable") ? ", stackable" : "") + (EnluJson.GetBool(item, "consumable") ? ", consumable" : "") +
                                (EnluJson.GetBool(item, "enabled", true) ? "" : ", disabled") + (EnluJson.GetObject(item, "bundle") != null ? ", bundle" : "");
                    EditorGUILayout.LabelField(EnluJson.GetString(item, "itemId") ?? "", (EnluJson.GetString(item, "name") ?? "") + "  " + Amounts(EnluJson.GetObject(item, "prices")) + flags);
                    if (GUILayout.Button("Edit", GUILayout.Width(50))) EditItem(item, codes);
                }
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUI.DisabledScope(_itemEditing != null))
                {
                    _itemId = EditorGUILayout.TextField("Item id", _itemId);
                }
                _itemName = EditorGUILayout.TextField("Name", _itemName);
                if (codes.Length == 0)
                {
                    EditorGUILayout.HelpBox("Create a currency first to give items a price.", MessageType.None);
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        _itemPrice = EditorGUILayout.LongField("Price", _itemPrice);
                        _itemCurrency = EditorGUILayout.Popup(Mathf.Clamp(_itemCurrency, 0, codes.Length - 1), codes, GUILayout.Width(70));
                    }
                }
                _itemStackable = EditorGUILayout.Toggle("Stackable", _itemStackable);
                _itemConsumable = EditorGUILayout.Toggle(new GUIContent("Consumable", "Players may consume it from the client."), _itemConsumable);
                var validItem = System.Text.RegularExpressions.Regex.IsMatch(_itemId, "^[a-z][a-z0-9_.-]{0,63}$") && _itemName.Trim().Length > 0 && codes.Length > 0 && _itemPrice >= 0;
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(_busy != null || !validItem))
                    {
                        if (GUILayout.Button(_itemEditing != null ? "Update item" : "Create item"))
                        {
                            var currency = codes[Mathf.Clamp(_itemCurrency, 0, codes.Length - 1)];
                            var editing = _itemEditing;
                            var itemId = _itemId;
                            Run("Saving item…", async ct =>
                            {
                                await new EnluDashboardApi(_auth!).SaveSimpleItemAsync(selectedApp, itemId, _itemName.Trim(), currency, _itemPrice,
                                    _itemStackable, _itemConsumable, editing, ct);
                                await LoadEconomyAsync(ct);
                                _info = "Item " + itemId + " saved.";
                                _itemEditing = null;
                            });
                        }
                    }
                    if (_itemEditing != null && GUILayout.Button("New item", GUILayout.Width(80)))
                    {
                        _itemEditing = null;
                        _itemId = string.Empty;
                        _itemName = string.Empty;
                    }
                }
            }
        }

        private void DrawGrantTester(string[] codes)
        {
            EditorGUILayout.LabelField("Grant to player (testing)", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _grantPlayer = EditorGUILayout.TextField(new GUIContent("Player", "A player id (pl_…), or a custom id, email or name to search."), _grantPlayer);
                var itemIds = new[] { "(no item)" }.Concat(_catalog.Select(i => EnluJson.GetString(i, "itemId") ?? "")).ToArray();
                using (new EditorGUILayout.HorizontalScope())
                {
                    _grantItem = EditorGUILayout.Popup("Item", Mathf.Clamp(_grantItem, 0, itemIds.Length - 1), itemIds);
                    _grantItemCount = Mathf.Max(1, EditorGUILayout.IntField(_grantItemCount, GUILayout.Width(50)));
                }
                var currencyChoices = new[] { "(no currency)" }.Concat(codes).ToArray();
                using (new EditorGUILayout.HorizontalScope())
                {
                    _grantCurrency = EditorGUILayout.Popup("Currency", Mathf.Clamp(_grantCurrency, 0, currencyChoices.Length - 1), currencyChoices);
                    _grantAmount = EditorGUILayout.LongField(_grantAmount, GUILayout.Width(80));
                }
                _grantNote = EditorGUILayout.TextField("Note", _grantNote);
                var anything = _grantItem > 0 || (_grantCurrency > 0 && _grantAmount != 0);
                using (new EditorGUI.DisabledScope(_busy != null || _grantPlayer.Trim().Length == 0 || !anything))
                {
                    if (GUILayout.Button("Grant"))
                    {
                        var item = _grantItem > 0 ? itemIds[_grantItem] : null;
                        var currency = _grantCurrency > 0 ? currencyChoices[_grantCurrency] : null;
                        var who = _grantPlayer;
                        Run("Granting…", async ct =>
                        {
                            var api = new EnluDashboardApi(_auth!);
                            var player = await api.FindPlayerIdAsync(selectedApp, who, ct);
                            if (player == null)
                            {
                                _error = "No player matches '" + who + "'.";
                                return;
                            }
                            var r = await api.GrantAsync(selectedApp, player, item, _grantItemCount, currency, _grantAmount, _grantNote, ct);
                            var clipped = EnluJson.GetObject(r, "clipped");
                            _info = "Granted to " + player + ". Balances: " + Amounts(EnluJson.GetObject(r, "currencies")) +
                                    (clipped != null && clipped.Count > 0 ? ". Clipped by max: " + Amounts(clipped) : "");
                        });
                    }
                }
            }
        }

        private void EditItem(Dictionary<string, object?> item, string[] codes)
        {
            _itemEditing = item;
            _itemId = EnluJson.GetString(item, "itemId") ?? string.Empty;
            _itemName = EnluJson.GetString(item, "name") ?? string.Empty;
            _itemStackable = EnluJson.GetBool(item, "stackable");
            _itemConsumable = EnluJson.GetBool(item, "consumable");
            var prices = EnluJson.GetObject(item, "prices");
            if (prices != null && prices.Count > 0)
            {
                var first = prices.First();
                _itemCurrency = Math.Max(0, Array.IndexOf(codes, first.Key));
                _itemPrice = EnluJson.ToLong(first.Value);
                if (prices.Count > 1) _info = "This item has several prices; saving here keeps only the one shown. Edit multi-currency prices in the dashboard.";
            }
        }


        private void DrawServerHelper()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _wlName = EditorGUILayout.TextField("Workload name", _wlName);
                _wlImage = EditorGUILayout.TextField(new GUIContent("Image", "e.g. registry.example.com/my-game/arena:1.0.0"), _wlImage);
                using (new EditorGUILayout.HorizontalScope())
                {
                    _wlPort = EditorGUILayout.TextField("Port", _wlPort);
                    _wlContainerPort = EditorGUILayout.IntField(_wlContainerPort, GUILayout.Width(70));
                    _wlProto = Protocols[EditorGUILayout.Popup(Array.IndexOf(Protocols, _wlProto), Protocols, GUILayout.Width(60))];
                }
                _wlStandby = EditorGUILayout.IntField(new GUIContent("Standby servers", "Ready servers kept waiting for matches."), _wlStandby);
                _wlMax = EditorGUILayout.IntField(new GUIContent("Max servers", "Cap of allocated plus standby servers."), _wlMax);
                _wlCpu = EditorGUILayout.FloatField("CPU cores", _wlCpu);
                _wlMemory = EditorGUILayout.IntField("Memory (MB)", _wlMemory);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(_busy != null || _wlImage.Trim().Length == 0 || _wlName.Trim().Length == 0))
                    {
                        if (GUILayout.Button("Create or update fleet"))
                        {
                            var spec = EnluDashboardApi.GameServerSpec(_wlImage.Trim(), _wlPort.Trim(), _wlContainerPort, _wlProto, _wlStandby, _wlMax, _wlCpu, _wlMemory);
                            Run("Saving workload…", async ct =>
                            {
                                var api = new EnluDashboardApi(_auth!);
                                await api.CreateOrUpdateWorkloadAsync(selectedApp, _wlName.Trim(), spec, ct);
                                _workloads = await api.ListWorkloadsAsync(selectedApp, ct);
                                _info = "Workload '" + _wlName + "' saved. Servers appear once they call EnluGameServer.ReadyForPlayers().";
                            });
                        }
                    }
                    using (new EditorGUI.DisabledScope(_busy != null || _wlName.Trim().Length == 0))
                    {
                        if (GUILayout.Button("Scale only"))
                        {
                            Run("Scaling…", async ct =>
                            {
                                var api = new EnluDashboardApi(_auth!);
                                await api.ScaleFleetAsync(selectedApp, _wlName.Trim(), _wlStandby, _wlMax, ct);
                                _workloads = await api.ListWorkloadsAsync(selectedApp, ct);
                                _info = "Fleet scaled to " + _wlStandby + " standby (max " + _wlMax + ").";
                            });
                        }
                    }
                }
                EditorGUILayout.LabelField("Build the image with the Dedicated Server sample's Dockerfile and push it to a registry your nodes can pull from.", EditorStyles.wordWrappedMiniLabel);
            }
        }
    }
}
