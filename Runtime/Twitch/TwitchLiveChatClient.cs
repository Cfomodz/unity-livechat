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
        public static readonly string[] ChannelPointsScopes = { "channel:manage:redemptions" };
        public static readonly string[] StreamInfoScopes = { "channel:manage:broadcast" };
        private const double ValidateEverySeconds = 3600;
        private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(3);

        [Tooltip("Client ID of a Twitch application registered as a Public client.")]
        [SerializeField] private string _clientId;
        [Tooltip("Login of the bot account. Optional; if set, only that account is accepted at the bot login.")]
        [SerializeField] private string _botLogin;
        [Tooltip("Have the broadcaster log in too, to receive channel point redemptions.")]
        [SerializeField] private bool _channelPoints;
        [Tooltip("Title, category and tags to set when the broadcaster logs in. Leave empty to leave the stream info alone.")]
        [SerializeField] private TwitchStreamInfo _streamInfo = new TwitchStreamInfo();
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
        /// <summary>Rewards <see cref="EnsureRewardsAsync"/> showed, to hide again on quit.</summary>
        private readonly List<string> _shownRewards = new List<string>();

        public string ClientId { get => _clientId; set => _clientId = value; }
        public string BotLogin { get => _botLogin; set => _botLogin = value; }
        public bool ChannelPoints { get => _channelPoints; set => _channelPoints = value; }
        /// <summary>Applied once the broadcaster logs in. Setting any field makes the broadcaster log in.</summary>
        public TwitchStreamInfo StreamInfo { get => _streamInfo; set => _streamInfo = value ?? new TwitchStreamInfo(); }
        public string TokenFilePath { get => _tokenFilePath; set => _tokenFilePath = value; }
        /// <summary>Hide the rewards <see cref="EnsureRewardsAsync"/> showed when the app quits, so viewers can't redeem them while it isn't running.</summary>
        public bool HideRewardsOnQuit { get; set; } = true;

        /// <summary>The broadcaster logs in for channel points, stream info, or both.</summary>
        public bool NeedsBroadcaster => _channelPoints || !_streamInfo.IsEmpty;
        /// <summary>What happened to <see cref="StreamInfo"/>, for an on-screen status; null if there's none to set.</summary>
        public string StreamInfoStatus { get; private set; }

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
        /// Makes the channel's rewards from this app match <paramref name="rewards"/> and returns
        /// each one's ID by title. Rewards this app already created (matched by title) are hidden,
        /// their redemptions from while the app wasn't running are refunded, and they're updated to
        /// the spec (cost, prompt, input) and shown again; missing ones are created. With
        /// <paramref name="removeUnlisted"/>, this app's rewards that aren't listed are refunded and
        /// deleted. With <see cref="HideRewardsOnQuit"/>, they're hidden again when the app quits.
        /// Needs <see cref="ChannelPoints"/> and an affiliate or partner channel. Only rewards
        /// created this way can be fulfilled or refunded.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, string>> EnsureRewardsAsync(IEnumerable<TwitchRewardSpec> rewards, bool removeUnlisted = false)
        {
            Dictionary<string, string> ids = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_broadcasterAccount == null || !_broadcasterAccount.IsAuthorized)
                return ids;
            CancellationToken ct = _cts?.Token ?? CancellationToken.None;
            List<TwitchRewardSpec> specs = rewards.ToList();

            try
            {
                List<TwitchCustomReward> mine = await _helix.GetCustomRewardsAsync(_broadcasterAccount, true, ct);
                Dictionary<string, TwitchCustomReward> existing = mine
                    .GroupBy(reward => reward.Title).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

                if (removeUnlisted)
                {
                    HashSet<string> wanted = new HashSet<string>(specs.Select(spec => spec.Title), StringComparer.Ordinal);
                    foreach (TwitchCustomReward retired in mine.Where(reward => !wanted.Contains(reward.Title)))
                    {
                        await _helix.UpdateCustomRewardAsync(_broadcasterAccount, retired.Id, false, ct);
                        await RefundStaleAsync(retired, ct);
                        await _helix.DeleteCustomRewardAsync(_broadcasterAccount, retired.Id, ct);
                        Debug.Log($"[LiveChat] Deleted channel point reward '{retired.Title}', which is no longer used");
                    }
                }

                foreach (TwitchRewardSpec spec in specs)
                {
                    if (existing.TryGetValue(spec.Title, out TwitchCustomReward reward))
                    {
                        await ReopenRewardAsync(reward, spec, ct);
                        ids[reward.Title] = reward.Id;
                        continue;
                    }
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

            foreach (string id in ids.Values)
                if (!_shownRewards.Contains(id))
                    _shownRewards.Add(id);
            return ids;
        }

        /// <summary>
        /// The channel's rewards that this app didn't create, so it can't change or delete them
        /// (made in the Creator Dashboard or by another app). Empty without a broadcaster login.
        /// </summary>
        public async Task<IReadOnlyList<TwitchCustomReward>> GetOtherRewardsAsync()
        {
            if (_broadcasterAccount == null || !_broadcasterAccount.IsAuthorized)
                return Array.Empty<TwitchCustomReward>();
            CancellationToken ct = _cts?.Token ?? CancellationToken.None;
            HashSet<string> mine = new HashSet<string>((await _helix.GetCustomRewardsAsync(_broadcasterAccount, true, ct)).Select(reward => reward.Id));
            return (await _helix.GetCustomRewardsAsync(_broadcasterAccount, false, ct)).Where(reward => !mine.Contains(reward.Id)).ToList();
        }

        /// <summary>
        /// Hides a reward, refunds what viewers redeemed while nobody was handling it, then updates
        /// it to <paramref name="spec"/> and shows it. Hiding first means no new redemption can be
        /// refunded by mistake.
        /// </summary>
        private async Task ReopenRewardAsync(TwitchCustomReward reward, TwitchRewardSpec spec, CancellationToken ct)
        {
            if (reward.IsEnabled)
                await _helix.UpdateCustomRewardAsync(_broadcasterAccount, reward.Id, false, ct);
            await RefundStaleAsync(reward, ct);
            await _helix.UpdateCustomRewardAsync(_broadcasterAccount, reward.Id, spec, true, ct);
            if (reward.Cost != spec.Cost)
                Debug.Log($"[LiveChat] Changed '{reward.Title}' from {reward.Cost} to {spec.Cost} points");
        }

        private async Task RefundStaleAsync(TwitchCustomReward reward, CancellationToken ct)
        {
            List<string> stale = await _helix.GetUnfulfilledRedemptionIdsAsync(_broadcasterAccount, reward.Id, ct);
            foreach (string redemptionId in stale)
                await _helix.UpdateRedemptionStatusAsync(_broadcasterAccount, reward.Id, redemptionId, false, ct);
            if (stale.Count > 0)
                Debug.Log($"[LiveChat] Refunded {stale.Count} '{reward.Title}' redemption(s) made while the app wasn't running");
        }

        /// <summary>
        /// Hides the rewards <see cref="EnsureRewardsAsync"/> showed, waiting at most a few seconds:
        /// the app is about to exit, so this runs off the main thread and blocks for it.
        /// </summary>
        private void HideRewardsBeforeQuit()
        {
            if (!HideRewardsOnQuit || _shownRewards.Count == 0 || _broadcasterAccount == null || !_broadcasterAccount.IsAuthorized)
                return;
            string[] rewardIds = _shownRewards.ToArray();
            _shownRewards.Clear();
            TwitchAccount broadcaster = _broadcasterAccount;
            TwitchHelix helix = _helix;
            Task hide = Task.Run(async () =>
            {
                foreach (string id in rewardIds)
                    await helix.UpdateCustomRewardAsync(broadcaster, id, false, CancellationToken.None);
            });
            try
            {
                if (!hide.Wait(QuitTimeout))
                    Debug.LogWarning("[LiveChat] Twitch didn't answer in time; the channel point rewards may still be showing.");
            }
            catch (AggregateException e)
            {
                Debug.LogWarning($"[LiveChat] Couldn't hide the channel point rewards: {e.InnerException?.Message}");
            }
        }

        private void OnApplicationQuit() => HideRewardsBeforeQuit();

        private void Queue(string message, string replyTo)
        {
            _sendQueue.Enqueue(message, replyTo, Time.realtimeSinceStartupAsDouble);
        }

        /// <summary>Starts up, retrying with backoff while Twitch can't be reached (no network yet, an outage).</summary>
        private async Task StartAsync(string channel, CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await StartOnceAsync(channel, ct);
                    return;
                }
                catch (Exception e) when (IsTransient(e, ct))
                {
                    int delay = Math.Min(60, 5 * attempt);
                    Debug.LogWarning($"[LiveChat] Can't reach Twitch ({e.Message}), retrying in {delay} s");
                    SetState(TwitchConnectionState.Reconnecting, $"Can't reach Twitch, retrying in {delay} s");
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                }
            }
        }

        private static bool IsTransient(Exception e, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return false;
            if (e is System.Net.Http.HttpRequestException || e is TaskCanceledException)
                return true; // TaskCanceledException without our cancellation is an HTTP timeout
            return e is TwitchApiException api && (int)api.Status >= 500;
        }

        private async Task StartOnceAsync(string channel, CancellationToken ct)
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

            if (NeedsBroadcaster)
            {
                List<string> scopes = new List<string>();
                if (_channelPoints)
                    scopes.AddRange(ChannelPointsScopes);
                if (!_streamInfo.IsEmpty)
                    scopes.AddRange(StreamInfoScopes);
                _broadcasterAccount = new TwitchAccount("broadcaster", _clientId, scopes, store) { ExpectedLogin = Broadcaster.Login };
                await LogInAsync(_broadcasterAccount, "broadcaster", ct);
                if (!_streamInfo.IsEmpty)
                    Run(ApplyStreamInfoAsync(_streamInfo, ct));
            }

            SetState(TwitchConnectionState.Connecting, $"Connecting to #{channel}…");
            double now = Time.realtimeSinceStartupAsDouble;
            _botSocket = CreateSocket(sessionId => SubscribeBotAsync(sessionId, ct));
            _botSocket.Start(now);
            if (_channelPoints)
            {
                _broadcasterSocket = CreateSocket(sessionId => SubscribeBroadcasterAsync(sessionId, ct));
                _broadcasterSocket.Start(now);
            }
            _validateAt = now + ValidateEverySeconds;
        }

        /// <summary>Sets the stream title, category and tags. Failures are reported in <see cref="StreamInfoStatus"/>, not fatal.</summary>
        public Task SetStreamInfoAsync(TwitchStreamInfo info) => ApplyStreamInfoAsync(info, _cts?.Token ?? CancellationToken.None);

        private async Task ApplyStreamInfoAsync(TwitchStreamInfo info, CancellationToken ct)
        {
            if (info == null || info.IsEmpty)
                return;
            if (_broadcasterAccount == null || !_broadcasterAccount.IsAuthorized || !_broadcasterAccount.Scopes.Contains(StreamInfoScopes[0]))
            {
                StreamInfoStatus = "Stream info: needs the broadcaster's login with stream info set up";
                StatusChanged?.Invoke();
                return;
            }

            string categoryId = null;
            if (!string.IsNullOrWhiteSpace(info.Category))
            {
                categoryId = await _helix.FindCategoryIdAsync(_broadcasterAccount, info.Category.Trim(), ct);
                if (categoryId == null)
                    Debug.LogWarning($"[LiveChat] Twitch has no category called '{info.Category}'; leaving the category as it is.");
            }
            string[] tags = info.Tags?.Select(tag => tag?.Trim()).Where(tag => !string.IsNullOrEmpty(tag)).Take(10).ToArray();
            if (tags != null && tags.Length == 0)
                tags = null;

            try
            {
                await _helix.ModifyChannelInformationAsync(_broadcasterAccount, info.Title, categoryId, tags, ct);
                StreamInfoStatus = "Stream info set";
                Debug.Log($"[LiveChat] Stream info set: '{info.Title}'"
                    + (categoryId != null ? $" in {info.Category}" : "") + (tags != null ? $", tags {string.Join(", ", tags)}" : ""));
            }
            catch (TwitchApiException e) when (e.Status == HttpStatusCode.BadRequest)
            {
                // Usually a tag with spaces or symbols, or one over 25 characters
                StreamInfoStatus = "Stream info rejected: " + e.Message;
                Debug.LogWarning($"[LiveChat] Twitch rejected the stream info: {e.Message}");
            }
            StatusChanged?.Invoke();
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
