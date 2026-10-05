using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiveChat.Twitch
{
    /// <summary>One EventSub notification: the subscription type and its event payload.</summary>
    public class EventSubNotification
    {
        public string MessageId;
        public string SubscriptionType;
        public JObject Event;
    }

    /// <summary>
    /// An EventSub WebSocket session for one Twitch user. Reads on a background task and hands every
    /// message to the main thread through <see cref="Pump"/>, which also runs the keepalive watchdog,
    /// follows session_reconnect to the new URL without losing subscriptions, drops duplicate and
    /// stale messages, and reconnects with backoff after a drop.
    /// </summary>
    public sealed class EventSubSocket : IDisposable
    {
        public const string DefaultUrl = "wss://eventsub.wss.twitch.tv/ws?keepalive_timeout_seconds=30";
        private static readonly TimeSpan MaxMessageAge = TimeSpan.FromMinutes(10);
        private const int RememberedMessageIds = 1000;

        private sealed class Connection
        {
            public ClientWebSocket Socket;
            public CancellationTokenSource Cancel;
            public bool Welcomed;
            /// <summary>Opened from a session_reconnect URL: it inherits the old session's subscriptions.</summary>
            public bool IsMigration;
        }

        private sealed class Inbound
        {
            public Connection From;
            public string Text;
            public string ClosedReason;
        }

        private readonly ConcurrentQueue<Inbound> _inbound = new ConcurrentQueue<Inbound>();
        private readonly HashSet<string> _seenIds = new HashSet<string>();
        private readonly Queue<string> _seenOrder = new Queue<string>();
        private readonly string _url;
        private Connection _active;
        private Connection _migrating;
        private double _lastMessageAt;
        private double _keepaliveSeconds = 30;
        private double _reconnectAt = -1;
        private int _failures;
        private bool _running;

        public EventSubSocket(string url = DefaultUrl)
        {
            _url = url;
        }

        public string SessionId { get; private set; }

        /// <summary>A new session is ready: create subscriptions now (within 10 s).</summary>
        public event Action<string> Welcomed;
        public event Action<EventSubNotification> NotificationReceived;
        /// <summary>Twitch revoked a subscription (type, status), e.g. because the user removed the app's access.</summary>
        public event Action<string, string> Revoked;
        /// <summary>The session dropped and its subscriptions are gone. It reconnects by itself.</summary>
        public event Action<string> Dropped;

        public void Start(double now)
        {
            _running = true;
            _failures = 0;
            _lastMessageAt = now;
            _active = Open(_url);
        }

        public void Stop()
        {
            _running = false;
            Close(_active);
            Close(_migrating);
            _active = _migrating = null;
            SessionId = null;
        }

        public void Dispose() => Stop();

        /// <summary>Call every frame on the main thread with a realtime clock in seconds.</summary>
        public void Pump(double now)
        {
            if (!_running)
                return;

            while (_inbound.TryDequeue(out Inbound inbound))
            {
                if (inbound.ClosedReason != null)
                    OnClosed(inbound.From, inbound.ClosedReason, now);
                else
                    OnText(inbound.From, inbound.Text, now);
            }

            if (_active == null && _reconnectAt >= 0 && now >= _reconnectAt)
            {
                _reconnectAt = -1;
                _lastMessageAt = now;
                _active = Open(_url);
            }

            // No keepalive or notification within the timeout means the connection is dead
            if (_active != null && now - _lastMessageAt > _keepaliveSeconds + 5)
                OnClosed(_active, "keepalive timed out", now);
        }

        private void OnText(Connection from, string text, double now)
        {
            if (from != _active && from != _migrating)
                return;

            JObject message;
            try
            {
                // Keep timestamps as strings: see ParseTimestamp
                using JsonTextReader reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
                message = JObject.Load(reader);
            }
            catch (JsonException)
            {
                return;
            }

            JObject metadata = message["metadata"] as JObject;
            JObject payload = message["payload"] as JObject;
            string type = (string)metadata.Field("message_type");
            if (from == _active)
                _lastMessageAt = now;

            switch (type)
            {
                case "session_welcome":
                    JObject session = payload.Field("session") as JObject;
                    _keepaliveSeconds = (double?)session.Field("keepalive_timeout_seconds") ?? 30;
                    SessionId = (string)session.Field("id");
                    from.Welcomed = true;
                    _failures = 0;
                    if (from == _migrating || from.IsMigration)
                    {
                        // Subscriptions carried over to the new connection: just swap
                        if (from == _migrating)
                            Close(_active);
                        _active = from;
                        _migrating = null;
                        _lastMessageAt = now;
                    }
                    else
                    {
                        Welcomed?.Invoke(SessionId);
                    }
                    break;

                case "session_keepalive":
                    break;

                case "session_reconnect":
                    string reconnectUrl = (string)payload.Field("session").Field("reconnect_url");
                    if (from == _active && !string.IsNullOrEmpty(reconnectUrl))
                    {
                        Close(_migrating);
                        _migrating = Open(reconnectUrl);
                        _migrating.IsMigration = true;
                    }
                    break;

                case "notification":
                    if (!IsFresh(metadata))
                        break;
                    NotificationReceived?.Invoke(new EventSubNotification
                    {
                        MessageId = (string)metadata["message_id"],
                        SubscriptionType = (string)metadata["subscription_type"],
                        Event = payload.Field("event") as JObject
                    });
                    break;

                case "revocation":
                    if (!IsFresh(metadata))
                        break;
                    Revoked?.Invoke((string)metadata["subscription_type"], (string)payload.Field("subscription").Field("status"));
                    break;
            }
        }

        /// <summary>Drops replays: messages seen before, or older than ten minutes.</summary>
        private bool IsFresh(JObject metadata)
        {
            string id = (string)metadata.Field("message_id");
            if (string.IsNullOrEmpty(id) || !_seenIds.Add(id))
                return false;
            _seenOrder.Enqueue(id);
            if (_seenOrder.Count > RememberedMessageIds)
                _seenIds.Remove(_seenOrder.Dequeue());

            DateTime? sent = ParseTimestamp(metadata["message_timestamp"]);
            return sent == null || DateTime.UtcNow - sent.Value < MaxMessageAge;
        }

        /// <summary>Twitch timestamps have nanoseconds (9 digits), more than DateTime parses, so trim them.</summary>
        public static DateTime? ParseTimestamp(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type == JTokenType.Date)
                return ((DateTime)token).ToUniversalTime();

            string text = ((string)token).TrimEnd('Z');
            int dot = text.IndexOf('.');
            if (dot >= 0 && text.Length - dot - 1 > 7)
                text = text.Substring(0, dot + 8);
            return DateTime.TryParse(text + "Z", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTime parsed) ? parsed : (DateTime?)null;
        }

        private void OnClosed(Connection from, string reason, double now)
        {
            if (from == _migrating)
            {
                // The new connection failed: keep the old one until Twitch closes it
                Close(_migrating);
                _migrating = null;
                return;
            }
            if (from != _active)
                return;

            Close(_active);
            _active = null;
            SessionId = null;
            if (_migrating != null)
            {
                // The old socket closed before the new one's welcome: promote the new one
                _active = _migrating;
                _migrating = null;
                _lastMessageAt = now;
                if (_active.Welcomed)
                    return;
            }
            if (_active != null)
                return;

            Dropped?.Invoke(reason);
            if (!_running)
                return;
            _failures++;
            double delay = Math.Min(30, Math.Pow(2, Math.Min(_failures, 5) - 1));
            _reconnectAt = now + delay;
        }

        private Connection Open(string url)
        {
            Connection connection = new Connection { Socket = new ClientWebSocket(), Cancel = new CancellationTokenSource() };
            _ = RunAsync(connection, url);
            return connection;
        }

        private static void Close(Connection connection)
        {
            if (connection == null)
                return;
            try
            {
                connection.Cancel.Cancel();
                connection.Socket.Abort();
                connection.Socket.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task RunAsync(Connection connection, string url)
        {
            string reason = "closed";
            CancellationToken ct = connection.Cancel.Token;
            try
            {
                await connection.Socket.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
                byte[] buffer = new byte[16 * 1024];
                using MemoryStream message = new MemoryStream();
                while (!ct.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        reason = $"closed by Twitch ({(int?)connection.Socket.CloseStatus} {connection.Socket.CloseStatusDescription})";
                        break;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                        continue;
                    _inbound.Enqueue(new Inbound { From = connection, Text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length) });
                    message.SetLength(0);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is WebSocketException || e is ObjectDisposedException || e is IOException)
            {
                reason = e.Message;
            }
            if (!ct.IsCancellationRequested)
                _inbound.Enqueue(new Inbound { From = connection, ClosedReason = reason });
        }
    }
}
