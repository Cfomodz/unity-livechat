using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LiveChat.Obs
{
    /// <summary>
    /// Drives OBS through obs-websocket v5: keeps a connection (retrying quietly while OBS isn't
    /// running), tracks whether OBS is streaming and its program scene, and switches scenes or starts
    /// and stops the stream when asked. It never starts or stops the stream on its own: only
    /// <see cref="GoLive"/> and <see cref="EndStream"/> do, and only chat commands call them.
    /// Every method must be called on the main thread; the Tasks resume there too.
    /// </summary>
    public class ObsController : MonoBehaviour
    {
        private const ObsEventSubscription Subscriptions =
            ObsEventSubscription.General | ObsEventSubscription.Scenes | ObsEventSubscription.Outputs;

        private ObsSettings _settings;
        private ObsWebSocket _socket;
        private bool _everConnected;
        private bool _sessionUp;
        private string _lastLoggedProblem;
        private bool _busy;

        /// <summary>Whether OBS is streaming. Null until OBS has told us.</summary>
        public bool? StreamActive { get; private set; }
        /// <summary>OBS lost its connection to Twitch and is reconnecting.</summary>
        public bool StreamReconnecting { get; private set; }
        public string CurrentScene { get; private set; }
        public bool IsConnected => _socket != null && _socket.IsReady;
        public string StartingScene => _settings?.startingScene?.Trim();
        public string GameScene => _settings?.gameScene?.Trim();

        /// <summary>Adds a controller to <paramref name="host"/> and starts connecting, or returns null if OBS control is off.</summary>
        public static ObsController Create(GameObject host, ObsSettings settings)
        {
            if (settings == null || !settings.enabled)
                return null;
            ObsController controller = host.AddComponent<ObsController>();
            controller.Begin(settings);
            return controller;
        }

        private void Begin(ObsSettings settings)
        {
            _settings = settings;
            _socket = new ObsWebSocket(settings.Url, settings.password, Subscriptions);
            _socket.Identified += OnIdentified;
            _socket.Disconnected += OnDisconnected;
            _socket.EventReceived += OnEvent;
            Debug.Log($"[OBS] Connecting to OBS at {settings.Url}");
            _socket.Start(Time.realtimeSinceStartupAsDouble);
        }

        private void Update()
        {
            _socket?.Pump(Time.realtimeSinceStartupAsDouble);
        }

        private void OnDestroy()
        {
            // Keep the socket object: a command still awaiting OBS gets a failed response, not a null
            _socket?.Stop();
        }

        #region Connection

        private async void OnIdentified()
        {
            bool reconnect = _everConnected;
            _everConnected = true;
            _sessionUp = true;
            _lastLoggedProblem = null;
            if (reconnect)
                Debug.Log("[OBS] Reconnected to OBS");

            try
            {
                if (!reconnect)
                {
                    ObsResponse version = await _socket.Request("GetVersion");
                    Debug.Log(version.Ok
                        ? $"[OBS] Connected to OBS {(string)version.Data?["obsVersion"]} (obs-websocket {(string)version.Data?["obsWebSocketVersion"]})"
                        : $"[OBS] Connected to OBS (obs-websocket {_socket.ObsWebSocketVersion})");
                }
                await RefreshStatus();
                if (!reconnect)
                    await CheckConfiguredScenes();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[OBS] Couldn't read OBS's state: {e.Message}");
            }
        }

        private void OnDisconnected(string reason)
        {
            bool wasConnected = _sessionUp;
            _sessionUp = false;
            StreamActive = null;
            StreamReconnecting = false;
            CurrentScene = null;

            // Log once per kind of problem: OBS simply not running yet shouldn't fill the Console
            string problem = _socket.AuthenticationFailed ? "auth" : reason == ObsProtocol.CloseReason(ObsProtocol.CloseUnsupportedRpcVersion) ? "rpc" : "down";
            if (wasConnected)
            {
                Debug.LogWarning($"[OBS] Lost the connection to OBS ({reason}). Retrying quietly.");
                _lastLoggedProblem = problem;
                return;
            }
            if (problem == _lastLoggedProblem)
                return;
            _lastLoggedProblem = problem;
            switch (problem)
            {
                case "auth":
                    Debug.LogError("[OBS] OBS rejected the password. Check the game's OBS password setting against "
                        + "OBS's Tools → WebSocket Server Settings. Retrying every minute.");
                    break;
                case "rpc":
                    Debug.LogError("[OBS] This OBS's obs-websocket version isn't supported (needs v5, OBS 28 or later).");
                    break;
                default:
                    Debug.Log($"[OBS] OBS isn't reachable ({reason}). Retrying quietly until it starts. "
                        + "Is OBS running with Tools → WebSocket Server Settings → Enable WebSocket server on?");
                    break;
            }
        }

        private void OnEvent(string type, JObject data)
        {
            switch (type)
            {
                case "StreamStateChanged":
                    bool active = (bool?)data["outputActive"] ?? false;
                    string state = (string)data["outputState"];
                    StreamReconnecting = state == "OBS_WEBSOCKET_OUTPUT_RECONNECTING";
                    if (StreamActive != active)
                        Debug.Log(active ? "[OBS] The stream started" : "[OBS] The stream stopped");
                    StreamActive = active;
                    break;
                case "CurrentProgramSceneChanged":
                    CurrentScene = (string)data["sceneName"] ?? CurrentScene;
                    break;
                case "SceneNameChanged":
                    if (CurrentScene != null && CurrentScene == (string)data["oldSceneName"])
                        CurrentScene = (string)data["sceneName"];
                    break;
            }
        }

        /// <summary>Reads the stream state and program scene from OBS.</summary>
        private async Task<ObsResponse> RefreshStatus()
        {
            ObsResponse stream = await _socket.Request("GetStreamStatus");
            if (stream.Ok && stream.Data != null)
            {
                StreamActive = (bool?)stream.Data["outputActive"] ?? false;
                StreamReconnecting = (bool?)stream.Data["outputReconnecting"] ?? false;
            }
            ObsResponse scene = await _socket.Request("GetCurrentProgramScene");
            if (scene.Ok && scene.Data != null)
                CurrentScene = (string)scene.Data["currentProgramSceneName"] ?? (string)scene.Data["sceneName"] ?? CurrentScene;
            return stream;
        }

        private async Task CheckConfiguredScenes()
        {
            List<string> scenes = await GetSceneNames();
            if (scenes == null)
                return;
            foreach (string configured in new[] { StartingScene, GameScene })
                if (!string.IsNullOrEmpty(configured) && !scenes.Contains(configured))
                    Debug.LogWarning($"[OBS] The OBS settings name the scene \"{configured}\", but OBS has no scene by that name. "
                        + $"OBS's scenes: {string.Join(", ", scenes)}");
        }

        private async Task<List<string>> GetSceneNames()
        {
            ObsResponse list = await _socket.Request("GetSceneList");
            if (!list.Ok || !(list.Data?["scenes"] is JArray scenes))
                return null;
            if (list.Data["currentProgramSceneName"] is JValue current && current.Type == JTokenType.String)
                CurrentScene = (string)current;
            // OBS lists scenes bottom-up; reverse to match the Scenes dock
            return scenes.OfType<JObject>().Select(s => (string)s["sceneName"]).Where(n => n != null).Reverse().ToList();
        }

        #endregion

        #region Actions (chat commands only)

        /// <summary>Switches the program scene. Returns the reply for chat.</summary>
        public async Task<string> SwitchScene(string argument)
        {
            if (!IsConnected)
                return NotConnectedReply();
            if (_busy)
                return "Still working on the last OBS command.";
            _busy = true;
            try
            {
                List<string> scenes = await GetSceneNames();
                if (scenes == null)
                    return "Couldn't get OBS's scene list.";
                string scene = ObsSceneResolver.Resolve(argument, StartingScene, GameScene, scenes, out string error);
                if (scene == null)
                    return error;
                if (scene == CurrentScene)
                    return $"Already on scene {scene}.";
                ObsResponse set = await _socket.Request("SetCurrentProgramScene", new JObject { ["sceneName"] = scene });
                if (!set.Ok)
                    return $"OBS didn't switch to {scene}: {set.Reason}";
                CurrentScene = scene;
                return $"Scene: {scene}";
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>
        /// Switches to the game scene (if one is configured) and starts streaming, unless OBS is
        /// already streaming. Returns the reply for chat.
        /// </summary>
        public async Task<string> GoLive()
        {
            if (!IsConnected)
                return NotConnectedReply();
            if (_busy)
                return "Still working on the last OBS command.";
            _busy = true;
            try
            {
                ObsResponse status = await RefreshStatus();
                if (!status.Ok)
                    return $"Couldn't read OBS's stream status: {status.Reason}. Not going live.";
                if (StreamActive == true)
                    return $"Already live (scene {CurrentScene}).";

                string game = GameScene;
                if (!string.IsNullOrEmpty(game) && game != CurrentScene)
                {
                    ObsResponse set = await _socket.Request("SetCurrentProgramScene", new JObject { ["sceneName"] = game });
                    if (!set.Ok)
                        return $"OBS didn't switch to {game}: {set.Reason}. Not going live.";
                    CurrentScene = game;
                }

                ObsResponse start = await _socket.Request("StartStream");
                if (!start.Ok)
                    return start.Code == ObsProtocol.StatusOutputRunning ? "OBS says it's already streaming." : $"OBS didn't start the stream: {start.Reason}";
                Debug.Log("[OBS] Started the stream");
                return $"Going live on scene {CurrentScene}.";
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>Stops the stream, if OBS is streaming. The caller handles confirmation. Returns the reply for chat.</summary>
        public async Task<string> EndStream()
        {
            if (!IsConnected)
                return NotConnectedReply();
            if (_busy)
                return "Still working on the last OBS command.";
            _busy = true;
            try
            {
                ObsResponse status = await RefreshStatus();
                if (status.Ok && StreamActive == false)
                    return "OBS isn't streaming.";
                ObsResponse stop = await _socket.Request("StopStream");
                if (!stop.Ok)
                    return stop.Code == ObsProtocol.StatusOutputNotRunning ? "OBS isn't streaming." : $"OBS didn't stop the stream: {stop.Reason}";
                Debug.Log("[OBS] Stopped the stream");
                return "Ending the stream.";
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>OBS's status for chat, read fresh from OBS when connected.</summary>
        public async Task<string> DescribeStatus()
        {
            if (!IsConnected)
                return NotConnectedReply();
            await RefreshStatus();
            return "OBS: " + StatusText();
        }

        #endregion

        #region Status

        /// <summary>The HUD line. Short (it's on stream) and never the URL or password.</summary>
        public string StatusLine => "OBS: " + StatusText();

        private string StatusText()
        {
            ObsConnectionState state = _socket?.State ?? ObsConnectionState.Stopped;
            return FormatStatus(state, _socket?.AuthenticationFailed ?? false, _everConnected, StreamActive, StreamReconnecting, CurrentScene);
        }

        private string NotConnectedReply()
        {
            return "OBS isn't connected: " + StatusText();
        }

        /// <summary>The status text after "OBS: ", e.g. "connected, live, scene Game" or "not running (retrying)".</summary>
        public static string FormatStatus(ObsConnectionState state, bool authenticationFailed, bool everConnected,
            bool? streamActive, bool streamReconnecting, string scene)
        {
            switch (state)
            {
                case ObsConnectionState.Ready:
                    string live = streamActive == null ? "checking" : streamActive.Value ? (streamReconnecting ? "live (reconnecting)" : "live") : "not live";
                    return string.IsNullOrEmpty(scene) ? $"connected, {live}" : $"connected, {live}, scene {Shorten(scene, 28)}";
                case ObsConnectionState.Waiting:
                    if (authenticationFailed)
                        return "wrong password (check the OBS settings)";
                    return everConnected ? "disconnected (retrying)" : "not running (retrying)";
                case ObsConnectionState.Stopped:
                    return "off";
                default:
                    return "connecting…";
            }
        }

        private static string Shorten(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }

        #endregion
    }
}
