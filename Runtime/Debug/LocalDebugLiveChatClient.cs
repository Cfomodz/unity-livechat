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
        [Tooltip("Shows or hides the chat box (while it isn't focused). None disables the toggle.")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.BackQuote;
        [SerializeField] private string _hint = "Local chat (debug): type a command like !help. Add cheer100 to simulate bits.";
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

        public KeyCode ToggleKey
        {
            get => _toggleKey;
            set => _toggleKey = value;
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
                IsSubscriber = isSubscriber,
                IsVip = isVip,
                IsModerator = isModerator,
                IsBroadcaster = isBroadcaster,
                IsMe = false,
                Bits = ParseCheerBits(trimmed),
                Emotes = new List<LiveChatEmote>()
            };

            RaiseMessageReceived(message);
        }

        public void SimulateSubscription(string username, int cumulativeMonths = 1,
            LiveChatSubscriptionPlan plan = LiveChatSubscriptionPlan.Tier1)
        {
            AddLogLine($"* {username} subscribed{(cumulativeMonths > 1 ? $" for {cumulativeMonths} months" : "")}");
            RaiseSubscribed(new LiveChatSubscriptionEvent
            {
                UserId = username.ToLowerInvariant(),
                Username = username.ToLowerInvariant(),
                DisplayName = username,
                Plan = plan,
                CumulativeMonths = Mathf.Max(1, cumulativeMonths),
                DurationMonths = 1,
                IsResub = cumulativeMonths > 1
            });
        }

        /// <summary>A gifted sub. Pass a null <paramref name="gifter"/> for an anonymous gift.</summary>
        public void SimulateGiftSubscription(string gifter, string recipient,
            LiveChatSubscriptionPlan plan = LiveChatSubscriptionPlan.Tier1, string communityGiftId = null)
        {
            if (communityGiftId == null)
                AddLogLine($"* {gifter ?? "An anonymous gifter"} gifted a sub to {recipient}");
            RaiseSubscriptionGifted(new LiveChatGiftSubscriptionEvent
            {
                GifterUserId = gifter?.ToLowerInvariant(),
                GifterUsername = gifter?.ToLowerInvariant(),
                GifterDisplayName = gifter,
                GifterIsAnonymous = gifter == null,
                RecipientUserId = recipient.ToLowerInvariant(),
                RecipientUsername = recipient.ToLowerInvariant(),
                RecipientDisplayName = recipient,
                Plan = plan,
                DurationMonths = 1,
                CommunityGiftId = communityGiftId
            });
        }

        /// <summary>A community gift, followed by one gifted sub per recipient, as Twitch sends them.</summary>
        public void SimulateCommunityGift(string gifter, IReadOnlyList<string> recipients,
            LiveChatSubscriptionPlan plan = LiveChatSubscriptionPlan.Tier1)
        {
            string id = Guid.NewGuid().ToString("N");
            AddLogLine($"* {gifter ?? "An anonymous gifter"} is gifting {recipients.Count} subs");
            RaiseCommunityGiftStarted(new LiveChatCommunityGiftEvent
            {
                Id = id,
                GifterUserId = gifter?.ToLowerInvariant(),
                GifterUsername = gifter?.ToLowerInvariant(),
                GifterDisplayName = gifter,
                GifterIsAnonymous = gifter == null,
                Count = recipients.Count,
                Plan = plan
            });
            foreach (string recipient in recipients)
                SimulateGiftSubscription(gifter, recipient, plan, id);
        }

        public void SimulateRaid(string fromChannel, int viewers)
        {
            AddLogLine($"* {fromChannel} is raiding with {viewers} viewers");
            RaiseRaided(new LiveChatRaidEvent
            {
                FromUserId = fromChannel.ToLowerInvariant(),
                FromUsername = fromChannel.ToLowerInvariant(),
                FromDisplayName = fromChannel,
                Viewers = viewers
            });
        }

        public void SimulateFollow(string username)
        {
            AddLogLine($"* {username} followed");
            RaiseFollowed(new LiveChatFollowEvent
            {
                UserId = username.ToLowerInvariant(),
                Username = username.ToLowerInvariant(),
                DisplayName = username
            });
        }

        public void SimulateRedemption(string rewardTitle, string username, string userInput = null, int cost = 0)
        {
            AddLogLine($"* {username} redeemed {rewardTitle}{(string.IsNullOrEmpty(userInput) ? "" : $": {userInput}")}");
            RaiseChannelPointsRedeemed(new LiveChatChannelPointsRedemption
            {
                RedemptionId = Guid.NewGuid().ToString("N"),
                RewardId = rewardTitle,
                RewardTitle = rewardTitle,
                Cost = cost,
                UserId = username.ToLowerInvariant(),
                Username = username.ToLowerInvariant(),
                DisplayName = username,
                UserInput = userInput
            });
        }

        public override void CompleteRedemption(LiveChatChannelPointsRedemption redemption, bool fulfilled)
        {
            if (redemption != null && !fulfilled)
                AddLogLine($"* Refunded {redemption.DisplayName}'s {redemption.RewardTitle}");
        }

        /// <summary>
        /// Sums "cheerN" tokens (e.g. "!buy steel cheer100"), the way Twitch reports bits on a
        /// message, so games can test bits handling without real cheers.
        /// </summary>
        public static int ParseCheerBits(string message)
        {
            if (string.IsNullOrEmpty(message))
                return 0;

            int total = 0;
            foreach (string token in message.Split(' '))
            {
                if (token.Length > 5 && token.StartsWith("cheer", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(token.Substring(5), out int bits) && bits > 0)
                    total += bits;
            }
            return total;
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
            Event current = Event.current;
            if (_toggleKey != KeyCode.None && current != null && current.type == EventType.KeyDown
                && current.keyCode == _toggleKey && GUI.GetNameOfFocusedControl() != InputControlName)
            {
                _showGui = !_showGui;
                if (!_showGui && GUIUtility.keyboardControl != 0)
                    GUIUtility.keyboardControl = 0;
                current.Use();
            }

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

            // Check keys before drawing the text field: GUI.TextField uses up the Enter key event,
            // so checking afterwards never sees it
            Event e = Event.current;
            if (e != null && e.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == InputControlName)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    SimulateIncoming(_input);
                    _input = string.Empty;
                    _focusInput = true;
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    // Release the box so games' keyboard shortcuts work again; click it to type
                    GUIUtility.keyboardControl = 0;
                    e.Use();
                }
            }

            GUI.SetNextControlName(InputControlName);
            _input = GUI.TextField(new Rect(x, y + logHeight + 4f, width, inputHeight), _input, 200, _inputStyle);

            if (_focusInput)
            {
                GUI.FocusControl(InputControlName);
                _focusInput = false;
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
