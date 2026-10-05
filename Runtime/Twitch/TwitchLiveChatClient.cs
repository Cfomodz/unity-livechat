using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace LiveChat.Twitch
{
    public enum TwitchConnectionState
    {
        Idle,
        /// <summary>Waiting for a login; see <see cref="TwitchLiveChatClient.PendingAuthorization"/>.</summary>
        LoggingIn,
        Connecting,
        Connected,
        Reconnecting,
        Failed
    }

    /// <summary>
    /// Twitch chat through EventSub (WebSocket) and the Helix API, with a separate bot account.
    ///
    /// The bot account reads and sends chat, and sees bits, subs, gifts, raids and (if it's a
    /// moderator) follows. With <see cref="ChannelPoints"/> on, the broadcaster logs in too, for
    /// channel point redemptions. Both log in with Twitch's device code flow, so the app needs only
    /// a public client ID, no secret. Tokens are saved in <see cref="TokenFilePath"/>.
    /// </summary>
    public class TwitchLiveChatClient : LiveChatClientBase
    {
        public static readonly string[] BotScopes = { "user:read:chat", "user:write:chat", "moderator:read:followers" };
        public static readonly string[] BroadcasterScopes = { "channel:manage:redemptions" };
        private const double ValidateEverySeconds = 3600;

        [Tooltip("Client ID of a Twitch application registered as a Public client.")]
        [SerializeField] private string _clientId;
        [Tooltip("Login of the bot account. Optional; if set, only that account is accepted at the bot login.")]
        [SerializeField] private string _botLogin;
        [Tooltip("Have the broadcaster log in too, to receive channel point redemptions.")]
        [SerializeField] private bool _channelPoints;
        [Tooltip("Where tokens are saved. Keep it out of version control.")]
        [SerializeField] private string _tokenFilePath;

        private readonly ChatSendQueue _sendQueue = new ChatSendQueue();
        private CancellationTokenSource _cts;
        private TwitchHelix _helix;
        private TwitchAccount _bot;
        private TwitchAccount _broadcasterAccount;
        private EventSubSocket _botSocket;
        private EventSubSocket _broadcasterSocket;
        private bool _sending;
        private bool _announcedConnected;
        private bool _warnedFollows;
        private double _validateAt;
        private readonly HashSet<string> _unmanagedRewards = new HashSet<string>();

        public string ClientId { get => _clientId; set => _clientId = value; }
        public string BotLogin { get => _botLogin; set => _botLogin = value; }
        public bool ChannelPoints { get => _channelPoints; set => _channelPoints = value; }
        public string TokenFilePath { get => _tokenFilePath; set => _tokenFilePath = value; }

        public TwitchConnectionState State { get; private set; }
        /// <summary>One line for an on-screen status display.</summary>
        public string StatusText { get; private set; } = "Not connected";
        /// <summary>The login the user needs to complete, or null.</summary>
        public TwitchDeviceAuthorization PendingAuthorization { get; private set; }
        public TwitchUser Broadcaster { get; private set; }
        public string BotUserId => _bot?.UserId;
        /// <summary>True once the bot has been seen with a moderator, VIP or broadcaster badge (higher chat limits).</summary>
        public bool BotIsPrivileged => _sendQueue.Privileged;
        public bool ChannelPointsReady { get; private set; }

        public event Action StatusChanged;
        public event Action<TwitchDeviceAuthorization> AuthorizationRequired;
        /// <summary>Raised when channel point redemptions start arriving (the broadcaster logged in and subscribed).</summary>
        public event Action ChannelPointsConnected;

        public override void Connect(LiveChatConnectConfig config)
        {
            string channel = config?.ChannelName?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(channel))
            {
                Fail(new ArgumentException("A channel name is required."));
                return;
            }
            if (string.IsNullOrWhiteSpace(_clientId))
            {
                Fail(new InvalidOperationException("TwitchLiveChatClient needs a ClientId (a Twitch app registered as a Public client)."));
                return;
            }
            if (string.IsNullOrWhiteSpace(_tokenFilePath))
                _tokenFilePath = System.IO.Path.Combine(Application.persistentDataPath, "livechat-tokens.json");

            Disconnect();
            DefaultChannel = channel;
            _cts = new CancellationTokenSource();
            Run(StartAsync(channel, _cts.Token), fatal: true);
        }

        public override void Disconnect()
        {
            bool wasConnected = IsConnected;
            _cts?.Cancel();
            _cts = null;
            _botSocket?.Stop();
            _broadcasterSocket?.Stop();
            _botSocket = _broadcasterSocket = null;
            _sendQueue.Clear();
            IsConnected = false;
            ChannelPointsReady = false;
            _announcedConnected = false;
            PendingAuthorization = null;
            SetState(TwitchConnectionState.Idle, "Not connected");
            if (wasConnected)
                RaiseDisconnected();
        }

        public override void SendMessage(string channel, string message) => Queue(message, null);

        public override void SendReply(string channel, string replyToMessageId, string message) => Queue(message, replyToMessageId);

        public override void CompleteRedemption(LiveChatChannelPointsRedemption redemption, bool fulfilled)
        {
            if (redemption == null || _broadcasterAccount == null || !_broadcasterAccount.IsAuthorized
                || _unmanagedRewards.Contains(redemption.RewardId))
                return;
            Run(CompleteRedemptionAsync(redemption, fulfilled, _cts?.Token ?? CancellationToken.None));
        }

        /// <summary>
        /// Creates any of these rewards that this app hasn't created yet (matched by title) and
        /// returns every one's ID by title. Needs <see cref="ChannelPoints"/> and an affiliate or
        /// partner channel. Only rewards created this way can be fulfilled or refunded.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, string>> EnsureRewardsAsync(IEnumerable<TwitchRewardSpec> rewards)
        {
            Dictionary<string, string> ids = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_broadcasterAccount == null || !_broadcasterAccount.IsAuthorized)
                return ids;
            CancellationToken ct = _cts?.Token ?? CancellationToken.None;

            try
            {
                foreach (TwitchCustomReward existing in await _helix.GetCustomRewardsAsync(_broadcasterAccount, true, ct))
                    ids[existing.Title] = existing.Id;
                foreach (TwitchRewardSpec spec in rewards)
                {
                    if (ids.ContainsKey(spec.Title))
                        continue;
                    try
                    {
                        TwitchCustomReward created = await _helix.CreateCustomRewardAsync(_broadcasterAccount, spec, ct);
                        ids[created.Title] = created.Id;
                        Debug.Log($"[LiveChat] Created channel point reward '{spec.Title}' ({spec.Cost} points)");
                    }
                    catch (TwitchApiException e) when (e.Status == HttpStatusCode.BadRequest)
                    {
                        Debug.LogWarning($"[LiveChat] Couldn't create the reward '{spec.Title}': {e.Message}. "
                            + "If a reward with that title already exists, delete it in the Creator Dashboard so this app can create its own.");
                    }
                }
            }
            catch (TwitchApiException e) when (e.Status == HttpStatusCode.Forbidden)
            {
                Debug.LogWarning($"[LiveChat] Channel point rewards need an affiliate or partner channel: {e.Message}");
            }
            return ids;
        }

        private void Queue(string message, string replyTo)
        {
            _sendQueue.Enqueue(message, replyTo, Time.realtimeSinceStartupAsDouble);
        }

        private async Task StartAsync(string channel, CancellationToken ct)
        {
            TwitchTokenStore store = new TwitchTokenStore(_tokenFilePath);
            _helix = new TwitchHelix(_clientId);

            _bot = new TwitchAccount("bot", _clientId, BotScopes, store) { ExpectedLogin = string.IsNullOrWhiteSpace(_botLogin) ? null : _botLogin.Trim() };
            await LogInAsync(_bot, "bot", ct);
            if (string.Equals(_bot.Login, channel, StringComparison.OrdinalIgnoreCase))
                Debug.LogWarning($"[LiveChat] The bot is logged in as the channel itself ({channel}). Use a separate bot account so replies don't come from the streamer.");

            SetState(TwitchConnectionState.Connecting, $"Looking up #{channel}…");
            Broadcaster = await _helix.GetUserByLoginAsync(_bot, channel, ct)
                ?? throw new InvalidOperationException($"There's no Twitch channel named '{channel}'.");

            if (_channelPoints)
            {
                _broadcasterAccount = new TwitchAccount("broadcaster", _clientId, BroadcasterScopes, store) { ExpectedLogin = Broadcaster.Login };
                await LogInAsync(_broadcasterAccount, "broadcaster", ct);
            }

            SetState(TwitchConnectionState.Connecting, $"Connecting to #{channel}…");
            double now = Time.realtimeSinceStartupAsDouble;
            _botSocket = CreateSocket(sessionId => SubscribeBotAsync(sessionId, ct));
            _botSocket.Start(now);
            if (_broadcasterAccount != null)
            {
                _broadcasterSocket = CreateSocket(sessionId => SubscribeBroadcasterAsync(sessionId, ct));
                _broadcasterSocket.Start(now);
            }
            _validateAt = now + ValidateEverySeconds;
        }

        private async Task LogInAsync(TwitchAccount account, string label, CancellationToken ct)
        {
            account.AuthorizationRequired += OnAuthorizationRequired;
            account.AuthorizationCompleted += OnAuthorizationCompleted;
            try
            {
                SetState(TwitchConnectionState.LoggingIn, $"Logging in the {label} account…");
                await account.AuthorizeAsync(ct);
                Debug.Log($"[LiveChat] {label} logged in as {account.Login}");
            }
            finally
            {
                account.AuthorizationRequired -= OnAuthorizationRequired;
                account.AuthorizationCompleted -= OnAuthorizationCompleted;
                PendingAuthorization = null;
            }
        }

        private void OnAuthorizationRequired(TwitchDeviceAuthorization authorization)
        {
            PendingAuthorization = authorization;
            Debug.Log($"[LiveChat] {authorization.Instructions} ({authorization.VerificationUri})");
            SetState(TwitchConnectionState.LoggingIn, authorization.Instructions);
            AuthorizationRequired?.Invoke(authorization);
        }

        private void OnAuthorizationCompleted()
        {
            PendingAuthorization = null;
            StatusChanged?.Invoke();
        }

        private EventSubSocket CreateSocket(Func<string, Task> subscribe)
        {
            EventSubSocket socket = new EventSubSocket();
            socket.Welcomed += sessionId => Run(subscribe(sessionId));
            socket.NotificationReceived += OnNotification;
            socket.Revoked += OnRevoked;
            socket.Dropped += reason =>
            {
                Debug.LogWarning($"[LiveChat] EventSub connection lost ({reason}), reconnecting");
                if (socket == _botSocket)
                    SetState(TwitchConnectionState.Reconnecting, "Reconnecting to Twitch…");
                else
                    ChannelPointsReady = false;
            };
            return socket;
        }

        private async Task SubscribeBotAsync(string sessionId, CancellationToken ct)
        {
            Dictionary<string, string> chat = new Dictionary<string, string>
            {
                ["broadcaster_user_id"] = Broadcaster.Id,
                ["user_id"] = _bot.UserId
            };
            await _helix.CreateEventSubSubscriptionAsync(_bot, EventSubTranslator.ChatMessage, "1", chat, sessionId, ct);
            await _helix.CreateEventSubSubscriptionAsync(_bot, EventSubTranslator.ChatNotification, "1", chat, sessionId, ct);
            try
            {
                await _helix.CreateEventSubSubscriptionAsync(_bot, EventSubTranslator.Follow, "2", new Dictionary<string, string>
                {
                    ["broadcaster_user_id"] = Broadcaster.Id,
                    ["moderator_user_id"] = _bot.UserId
                }, sessionId, ct);
            }
            catch (TwitchApiException e) when (e.Status == HttpStatusCode.Forbidden)
            {
                if (!_warnedFollows)
                    Debug.LogWarning($"[LiveChat] Follows need the bot to be a moderator: type /mod {_bot.Login} in #{Broadcaster.Login}'s chat, then restart.");
                _warnedFollows = true;
            }

            if (_botSocket?.SessionId != sessionId)
                return; // The session dropped while subscribing; the next welcome subscribes again
            IsConnected = true;
            SetState(TwitchConnectionState.Connected, $"Connected to #{Broadcaster.Login} as {_bot.Login}");
            if (_announcedConnected)
                return;
            _announcedConnected = true;
            RaiseConnected();
            RaiseJoinedChannel(DefaultChannel);
        }

        private async Task SubscribeBroadcasterAsync(string sessionId, CancellationToken ct)
        {
            await _helix.CreateEventSubSubscriptionAsync(_broadcasterAccount, EventSubTranslator.Redemption, "1",
                new Dictionary<string, string> { ["broadcaster_user_id"] = Broadcaster.Id }, sessionId, ct);
            if (_broadcasterSocket?.SessionId != sessionId)
                return;
            bool first = !ChannelPointsReady;
            ChannelPointsReady = true;
            if (first)
                ChannelPointsConnected?.Invoke();
        }

        private void OnNotification(EventSubNotification notification)
        {
            object e = EventSubTranslator.Translate(notification.SubscriptionType, notification.Event, DefaultChannel, _bot?.UserId);
            switch (e)
            {
                case LiveChatMessage message when message.IsMe:
                    // The bot's own messages tell us whether it has a moderator/VIP badge
                    _sendQueue.Privileged = message.IsModerator || message.IsVip || message.IsBroadcaster;
                    break;
                case LiveChatMessage message:
                    RaiseMessageReceived(message);
                    break;
                case LiveChatSubscriptionEvent sub:
                    RaiseSubscribed(sub);
                    break;
                case LiveChatGiftSubscriptionEvent gift:
                    RaiseSubscriptionGifted(gift);
                    break;
                case LiveChatCommunityGiftEvent community:
                    RaiseCommunityGiftStarted(community);
                    break;
                case LiveChatRaidEvent raid:
                    RaiseRaided(raid);
                    break;
                case LiveChatFollowEvent follow:
                    RaiseFollowed(follow);
                    break;
                case LiveChatChannelPointsRedemption redemption:
                    RaiseChannelPointsRedeemed(redemption);
                    break;
            }
        }

        private void OnRevoked(string type, string status)
        {
            Debug.LogWarning($"[LiveChat] Twitch revoked the {type} subscription ({status})");
            if (status == "authorization_revoked" || status == "user_removed")
            {
                // Someone removed the app's access: log in again from scratch
                Connect(new LiveChatConnectConfig { ChannelName = DefaultChannel });
            }
        }

        private void Update()
        {
            if (_cts == null)
                return;
            double now = Time.realtimeSinceStartupAsDouble;
            _botSocket?.Pump(now);
            _broadcasterSocket?.Pump(now);

            if (IsConnected && !_sending && _sendQueue.TryDequeue(now, out ChatSendQueue.Outgoing outgoing))
                Run(SendAsync(outgoing, _cts.Token));

            if (_botSocket != null && now >= _validateAt)
            {
                _validateAt = now + ValidateEverySeconds;
                Run(ValidateAsync(_cts.Token));
            }
        }

        private async Task SendAsync(ChatSendQueue.Outgoing outgoing, CancellationToken ct)
        {
            _sending = true;
            try
            {
                string dropped;
                try
                {
                    dropped = await _helix.SendChatMessageAsync(_bot, Broadcaster.Id, outgoing.Text, outgoing.ReplyToMessageId, ct);
                }
                catch (TwitchApiException e) when (e.Status == HttpStatusCode.BadRequest && !string.IsNullOrEmpty(outgoing.ReplyToMessageId))
                {
                    // The message being replied to may be gone: send it as a plain message
                    dropped = await _helix.SendChatMessageAsync(_bot, Broadcaster.Id, outgoing.Text, null, ct);
                }
                if (dropped != null)
                    Debug.LogWarning($"[LiveChat] Twitch dropped a bot message ({dropped}): {outgoing.Text}");
            }
            finally
            {
                _sending = false;
            }
        }

        /// <summary>Twitch requires validating tokens hourly. A token that can't be refreshed means logging in again.</summary>
        private async Task ValidateAsync(CancellationToken ct)
        {
            foreach ((TwitchAccount account, string label) in new[] { (_bot, "bot"), (_broadcasterAccount, "broadcaster") })
            {
                if (account == null || await account.ValidateAsync(ct))
                    continue;
                Debug.LogWarning($"[LiveChat] The {label} login expired; log in again.");
                await LogInAsync(account, label, ct);
                if (IsConnected)
                    SetState(TwitchConnectionState.Connected, $"Connected to #{Broadcaster.Login} as {_bot.Login}");
            }
        }

        private async Task CompleteRedemptionAsync(LiveChatChannelPointsRedemption redemption, bool fulfilled, CancellationToken ct)
        {
            try
            {
                await _helix.UpdateRedemptionStatusAsync(_broadcasterAccount, redemption.RewardId, redemption.RedemptionId, fulfilled, ct);
            }
            catch (TwitchApiException e) when (e.Status == HttpStatusCode.Forbidden || e.Status == HttpStatusCode.NotFound)
            {
                // Only the app that created a reward can update its redemptions
                if (_unmanagedRewards.Add(redemption.RewardId))
                    Debug.Log($"[LiveChat] Can't fulfil or refund '{redemption.RewardTitle}' redemptions: another app created that reward.");
            }
        }

        private void SetState(TwitchConnectionState state, string text)
        {
            State = state;
            StatusText = text;
            StatusChanged?.Invoke();
        }

        private void Fail(Exception e)
        {
            SetState(TwitchConnectionState.Failed, e.Message);
            RaiseError(e);
        }

        /// <summary>
        /// Awaits a task from the main thread, reporting failures instead of losing them. Only a
        /// <paramref name="fatal"/> failure (startup) puts the client in the Failed state.
        /// </summary>
        private async void Run(Task task, bool fatal = false)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                if (fatal)
                    Fail(e);
                else
                    RaiseError(e);
            }
        }

        private void OnDestroy() => Disconnect();
    }
}
