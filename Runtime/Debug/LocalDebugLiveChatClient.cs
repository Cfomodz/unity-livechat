using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiveChat
{
    /// <summary>
    /// Fake chat client for testing chat-controlled games without going live.
    /// Shows an on-screen chat box: type a message and press Enter to raise it
    /// as if a viewer had sent it. Bot replies are shown in the log.
    /// </summary>
    public class LocalDebugLiveChatClient : LiveChatClientBase
    {
        [Header("Connection")]
        [SerializeField] private string _defaultChannel = "local";
        [SerializeField] private bool _autoConnect = true;

        [Header("Simulated viewer")]
        [SerializeField] private string _username = "LocalUser";
        [SerializeField] private string _displayName = "LocalUser";
        [SerializeField] private bool _isSubscriber;
        [SerializeField] private bool _isVip;
        [SerializeField] private bool _isModerator;
        [SerializeField] private bool _isBroadcaster = true;

        [Header("On-screen chat")]
        [SerializeField] private bool _showGui = true;
        [SerializeField] private string _hint = "Local chat (debug): type a command like !help";
        [SerializeField] private int _maxLogEntries = 8;
        [SerializeField] private bool _logToConsole = true;

        private readonly List<string> _log = new List<string>();
        private string _input = string.Empty;
        private bool _focusInput = true;

        private GUIStyle _logStyle;
        private GUIStyle _inputStyle;

        private const string InputControlName = "LocalDebugChatInput";

        public string Hint
        {
            get => _hint;
            set => _hint = value ?? string.Empty;
        }

        public bool ShowGui
        {
            get => _showGui;
            set => _showGui = value;
        }

        public string Username
        {
            get => _username;
            set
            {
                _username = value ?? string.Empty;
                _displayName = _username;
            }
        }

        public bool IsSubscriber { get => _isSubscriber; set => _isSubscriber = value; }
        public bool IsVip { get => _isVip; set => _isVip = value; }
        public bool IsModerator { get => _isModerator; set => _isModerator = value; }
        public bool IsBroadcaster { get => _isBroadcaster; set => _isBroadcaster = value; }

        public IReadOnlyList<string> Log => _log;

        private void Awake()
        {
            DefaultChannel = _defaultChannel;
            if (_autoConnect)
                Connect(new LiveChatConnectConfig { ChannelName = _defaultChannel });
        }

        public override void Connect(LiveChatConnectConfig config)
        {
            string channel = string.IsNullOrEmpty(config?.ChannelName) ? _defaultChannel : config.ChannelName;
            if (IsConnected && channel == DefaultChannel)
                return;

            DefaultChannel = channel;
            IsConnected = true;
            RaiseConnected();
            RaiseJoinedChannel(DefaultChannel);
        }

        public override void Disconnect()
        {
            if (!IsConnected)
                return;

            IsConnected = false;
            RaiseDisconnected();
        }

        public override void SendMessage(string channel, string message)
        {
            AddLogLine($"Bot: {message}");
            if (_logToConsole)
                Debug.Log($"[LocalChat:{channel}] {message}");
        }

        public override void SendReply(string channel, string replyToMessageId, string message)
        {
            AddLogLine($"Bot: {message}");
            if (_logToConsole)
                Debug.Log($"[LocalChat:{channel}] reply to {replyToMessageId}: {message}");
        }

        /// <summary>Raise a message from the configured simulated viewer.</summary>
        public void SimulateIncoming(string rawMessage)
        {
            SimulateIncoming(rawMessage, _username, _displayName, _isSubscriber, _isVip, _isModerator, _isBroadcaster);
        }

        /// <summary>Raise a message from any simulated viewer, e.g. to test vote weighting.</summary>
        public void SimulateIncoming(string rawMessage, string username, string displayName = null,
            bool isSubscriber = false, bool isVip = false, bool isModerator = false, bool isBroadcaster = false)
        {
            if (string.IsNullOrWhiteSpace(rawMessage))
                return;

            if (!IsConnected)
                Connect(new LiveChatConnectConfig { ChannelName = _defaultChannel });

            string trimmed = rawMessage.Trim();
            string name = string.IsNullOrEmpty(displayName) ? username : displayName;
            AddLogLine($"{name}: {trimmed}");

            LiveChatMessage message = new LiveChatMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                UserId = username,
                Username = username,
                DisplayName = name,
                Channel = DefaultChannel,
                RawMessage = trimmed,
                RawIrcMessage = trimmed,
                IsSubscriber = isSubscriber,
                IsVip = isVip,
                IsModerator = isModerator,
                IsBroadcaster = isBroadcaster,
                IsMe = false,
                Emotes = new List<LiveChatEmote>()
            };

            RaiseMessageReceived(message);
        }

        private void AddLogLine(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;

            _log.Add(line);
            int max = Mathf.Max(1, _maxLogEntries) * 2;
            if (_log.Count > max)
                _log.RemoveRange(0, _log.Count - max);
        }

        private void OnGUI()
        {
            if (!_showGui)
                return;

            EnsureStyles();

            const float lineHeight = 18f;
            const float inputHeight = 24f;
            const float padding = 8f;
            int lines = Mathf.Max(1, _maxLogEntries);
            float width = Mathf.Min(520f, Screen.width - 16f);
            float logHeight = lineHeight * (lines + 1) + padding;
            float x = 8f;
            float y = Screen.height - logHeight - inputHeight - 16f;

            GUI.Box(new Rect(x, y, width, logHeight), string.Empty);
            GUI.Label(new Rect(x + padding, y + 4f, width - padding * 2f, lineHeight), _hint, _logStyle);

            int linesToShow = Mathf.Min(_log.Count, lines);
            int startIndex = _log.Count - linesToShow;
            float startY = y + logHeight - 4f - linesToShow * lineHeight;
            for (int i = 0; i < linesToShow; i++)
                GUI.Label(new Rect(x + padding, startY + i * lineHeight, width - padding * 2f, lineHeight), _log[startIndex + i], _logStyle);

            GUI.SetNextControlName(InputControlName);
            _input = GUI.TextField(new Rect(x, y + logHeight + 4f, width, inputHeight), _input, 200, _inputStyle);

            if (_focusInput)
            {
                GUI.FocusControl(InputControlName);
                _focusInput = false;
            }

            Event e = Event.current;
            if (e != null && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                && GUI.GetNameOfFocusedControl() == InputControlName)
            {
                SimulateIncoming(_input);
                _input = string.Empty;
                _focusInput = true;
                e.Use();
            }
        }

        private void EnsureStyles()
        {
            if (_logStyle == null)
                _logStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };

            if (_inputStyle == null)
                _inputStyle = new GUIStyle(GUI.skin.textField) { fontSize = 13 };
        }
    }
}
