#nullable enable
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Enlu.Editor
{
    /// <summary>Inspector for <see cref="EnluSettings"/> with validation and a "Test connection" button.</summary>
    [CustomEditor(typeof(EnluSettings))]
    public sealed class EnluSettingsEditor : UnityEditor.Editor
    {
        private ConnectionTest? _test;

        /// <inheritdoc />
        public override void OnInspectorGUI()
        {
            var settings = (EnluSettings)target;
            EditorGUILayout.HelpBox(
                "Player-facing settings. They ship inside your game build, so never put an API key here. " +
                "Name the asset 'EnluSettings' and keep it in a Resources folder to use EnluClient.Default.",
                MessageType.Info);

            DrawDefaultInspector();

            var problem = settings.Validate();
            if (problem != null) EditorGUILayout.HelpBox(problem, MessageType.Warning);

            var path = AssetDatabase.GetAssetPath(settings);
            if (!string.IsNullOrEmpty(path) &&
                (Path.GetFileNameWithoutExtension(path) != EnluSettings.ResourceName || path.Replace('\\', '/').IndexOf("/Resources/", StringComparison.Ordinal) < 0))
            {
                EditorGUILayout.HelpBox("EnluClient.Default and EnluSettings.Load() look for Resources/" + EnluSettings.ResourceName +
                                        ". This asset is elsewhere, so pass it to new EnluClient(settings) yourself.", MessageType.None);
            }

            EditorGUILayout.Space();
            if (GUILayout.Button(new GUIContent("Sign in to ENLU / choose app…", "Opens Tools > ENLU > Account: sign in through your browser, pick or create an app, and fill these settings.")))
                EnluAccountWindow.Open();
            using (new EditorGUI.DisabledScope(_test != null && _test.Running))
            {
                if (GUILayout.Button(_test != null && _test.Running ? "Testing…" : "Test connection"))
                {
                    _test = new ConnectionTest(settings, Repaint);
                    _test.Start();
                }
            }
            if (_test != null && !string.IsNullOrEmpty(_test.Message))
            {
                EditorGUILayout.HelpBox(_test.Message, _test.Ok ? MessageType.Info : (_test.Running ? MessageType.None : MessageType.Error));
            }
        }

        private void OnDisable()
        {
            _test?.Cancel();
        }

        /// <summary>
        /// Two requests: <c>GET /v1/health</c> (is this an ENLU control node?) then
        /// <c>GET /client/v1/:app/me</c> without a ticket, which answers 401 when the app exists and 404 when it does not.
        /// </summary>
        private sealed class ConnectionTest
        {
            private readonly EnluSettings _settings;
            private readonly Action _repaint;
            private UnityWebRequest? _www;
            private int _step;

            public bool Running { get; private set; }
            public bool Ok { get; private set; }
            public string Message { get; private set; } = string.Empty;

            public ConnectionTest(EnluSettings settings, Action repaint)
            {
                _settings = settings;
                _repaint = repaint;
            }

            public void Start()
            {
                string baseUrl;
                try
                {
                    baseUrl = EnluUrl.NormalizeBaseUrl(_settings.BaseUrl);
                }
                catch (ArgumentException e)
                {
                    Finish(false, e.Message);
                    return;
                }
                if (string.IsNullOrWhiteSpace(_settings.App))
                {
                    Finish(false, "Set the app id or name first.");
                    return;
                }
                Running = true;
                Message = "Contacting " + baseUrl + " …";
                _step = 0;
                Send(baseUrl + "/v1/health");
                EditorApplication.update += Poll;
            }

            public void Cancel()
            {
                EditorApplication.update -= Poll;
                _www?.Abort();
                _www?.Dispose();
                _www = null;
                Running = false;
            }

            private void Send(string url)
            {
                _www?.Dispose();
                _www = UnityWebRequest.Get(url);
                _www.timeout = Mathf.Max(2, _settings.RequestTimeoutSeconds);
                _www.SetRequestHeader("Accept", "application/json");
                _www.SendWebRequest();
            }

            private void Poll()
            {
                var www = _www;
                if (www == null || !www.isDone) return;
                var code = (int)www.responseCode;
                var body = www.downloadHandler?.text ?? string.Empty;
                var network = www.result == UnityWebRequest.Result.ConnectionError;
                var error = www.error;

                if (_step == 0)
                {
                    if (network || code == 0)
                    {
                        Finish(false, "Cannot reach the server: " + error + ". Check the base URL and that the control node is running.");
                        return;
                    }
                    if (code != 200 || !body.Contains("enlu-control"))
                    {
                        Finish(false, "The server answered HTTP " + code + " but does not look like an ENLU control node. Check the base URL (no path after the host).");
                        return;
                    }
                    _step = 1;
                    Message = "Server found. Checking the app…";
                    _repaint();
                    var routes = new EnluClientRoutes(_settings.BaseUrl, _settings.App);
                    Send(routes.Me());
                    return;
                }

                // Step 1: without a ticket, an existing app answers 401; a missing one 404.
                var parsed = EnluException.FromResponse(code, body, null);
                if (code == 401)
                    Finish(true, "Connected. App '" + _settings.App.Trim() + "' exists and the player API is reachable.");
                else if (code == 404)
                    Finish(false, "The server is reachable but app '" + _settings.App.Trim() + "' was not found. Use the app id (app_…) or exact app name.");
                else if (code == 403)
                    Finish(false, "The app is unavailable: " + parsed.Message);
                else if (network || code == 0)
                    Finish(false, "The second request failed: " + error);
                else
                    Finish(false, "Unexpected answer HTTP " + code + ": " + parsed.Message);
            }

            private void Finish(bool ok, string message)
            {
                Cancel();
                Ok = ok;
                Message = message;
                _repaint();
            }
        }
    }
}
