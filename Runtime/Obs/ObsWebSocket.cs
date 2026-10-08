using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LiveChat.Obs
{
    public enum ObsConnectionState
    {
        /// <summary>Not started, or stopped.</summary>
        Stopped,
        /// <summary>Opening the socket.</summary>
        Connecting,
        /// <summary>Socket open, waiting for Hello / Identified.</summary>
        Identifying,
        /// <summary>Identified: requests can be sent.</summary>
        Ready,
        /// <summary>Not connected; retrying after a delay.</summary>
        Waiting
    }

    /// <summary>
    /// An obs-websocket v5 client. Reads on a background task and hands every message to the main
    /// thread through <see cref="Pump"/>, which also matches request responses, times out stuck
    /// requests and handshakes, and reconnects with backoff (OBS often starts after the game).
    /// Every event and every request's Task completes on the thread that calls Pump.
    /// </summary>
    public sealed class ObsWebSocket : IDisposable
    {
        private const double HandshakeTimeoutSeconds = 10;
        private const double RequestTimeoutSeconds = 10;
        private const double MaxRetrySeconds = 10;
        private const double AuthFailedRetrySeconds = 60;

        private sealed class Connection
        {
            public ClientWebSocket Socket;
            public CancellationTokenSource Cancel;
            /// <summary>Sends are chained so only one is in flight at a time, as ClientWebSocket requires.</summary>
            public Task SendChain = Task.CompletedTask;
        }

        private sealed class Inbound
        {
            public Connection From;
            public bool Opened;
            public string Text;
            public string ClosedReason;
            public int CloseCode;
        }

        private sealed class Pending
        {
            public TaskCompletionSource<ObsResponse> Completion;
            public string RequestType;
            public double Deadline;
        }

        private readonly ConcurrentQueue<Inbound> _inbound = new ConcurrentQueue<Inbound>();
        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();
        private readonly string _url;
        private readonly string _password;
        private readonly ObsEventSubscription _subscriptions;
        private Connection _active;
        private double _stateSince;
        private double _now;
        private double _reconnectAt = -1;
        private int _failures;
        private int _nextRequestId;

        /// <param name="password">Only ever used to compute the Identify hash. Never logged or exposed.</param>
        public ObsWebSocket(string url, string password, ObsEventSubscription subscriptions)
        {
            _url = string.IsNullOrWhiteSpace(url) ? ObsProtocol.DefaultUrl : url.Trim();
            _password = password ?? string.Empty;
            _subscriptions = subscriptions;
        }

        public ObsConnectionState State { get; private set; }
        public bool IsReady => State == ObsConnectionState.Ready;
        /// <summary>Why the last connection attempt or session ended, e.g. "wrong password". Null once identified.</summary>
        public string LastError { get; private set; }
        /// <summary>The last close was OBS rejecting the password.</summary>
        public bool AuthenticationFailed { get; private set; }
        public string ObsWebSocketVersion { get; private set; }

        /// <summary>Identified: the session is ready for requests.</summary>
        public event Action Identified;
        /// <summary>The session ended or a connection attempt failed (reason). It retries by itself.</summary>
        public event Action<string> Disconnected;
        /// <summary>An OBS event (eventType, eventData).</summary>
        public event Action<string, JObject> EventReceived;

        public void Start(double now)
        {
            if (State != ObsConnectionState.Stopped)
                return;
            _now = now;
            _failures = 0;
            Open(now);
        }

        public void Stop()
        {
            Close(_active);
            _active = null;
            _reconnectAt = -1;
            State = ObsConnectionState.Stopped;
            FailPending("stopped");
        }

        public void Dispose() => Stop();

        /// <summary>
        /// Sends a request. The Task completes (on the Pump thread) with OBS's response, or with a failed
        /// response if the session isn't ready, drops, or OBS doesn't answer in time. It never throws.
        /// </summary>
        public Task<ObsResponse> Request(string requestType, JObject requestData = null)
        {
            if (State != ObsConnectionState.Ready || _active == null)
                return Task.FromResult(ObsResponse.Failed(requestType, "OBS isn't connected"));

            string id = "dd-" + (++_nextRequestId);
            TaskCompletionSource<ObsResponse> completion = new TaskCompletionSource<ObsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = new Pending { Completion = completion, RequestType = requestType, Deadline = _now + RequestTimeoutSeconds };
            Send(_active, ObsProtocol.BuildRequest(requestType, id, requestData));
            return completion.Task;
        }

        /// <summary>Call every frame on the main thread with a realtime clock in seconds.</summary>
        public void Pump(double now)
        {
            _now = now;
            if (State == ObsConnectionState.Stopped)
                return;

            while (_inbound.TryDequeue(out Inbound inbound))
            {
                if (inbound.From != _active)
                    continue;
                if (inbound.ClosedReason != null)
                    OnClosed(inbound.ClosedReason, inbound.CloseCode, now);
                else if (inbound.Opened)
                    SetState(ObsConnectionState.Identifying, now);
                else
                    OnText(inbound.Text, now);
            }

            if (State == ObsConnectionState.Waiting && _reconnectAt >= 0 && now >= _reconnectAt)
            {
                _reconnectAt = -1;
                Open(now);
            }

            if ((State == ObsConnectionState.Connecting || State == ObsConnectionState.Identifying)
                && now - _stateSince > HandshakeTimeoutSeconds)
                OnClosed("no answer from OBS", 0, now);

            if (_pending.Count > 0)
            {
                List<string> expired = null;
                foreach (KeyValuePair<string, Pending> p in _pending)
                    if (now > p.Value.Deadline)
                        (expired ??= new List<string>()).Add(p.Key);
                if (expired != null)
                    foreach (string id in expired)
                        Complete(id, ObsResponse.Failed(_pending[id].RequestType, "OBS didn't answer in time"));
            }
        }

        private void OnText(string text, double now)
        {
            ObsMessage message = ObsProtocol.Parse(text);
            if (message == null)
                return;

            switch (message.Op)
            {
                case ObsOpCode.Hello:
                    ObsHello hello = ObsProtocol.ParseHello(message.Data);
                    ObsWebSocketVersion = hello.ObsWebSocketVersion;
                    Send(_active, ObsProtocol.BuildIdentify(hello, _password, _subscriptions));
                    break;

                case ObsOpCode.Identified:
                    SetState(ObsConnectionState.Ready, now);
                    _failures = 0;
                    LastError = null;
                    AuthenticationFailed = false;
                    Identified?.Invoke();
                    break;

                case ObsOpCode.Event:
                    (string type, JObject data) = ObsProtocol.ParseEvent(message.Data);
                    if (!string.IsNullOrEmpty(type))
                        EventReceived?.Invoke(type, data);
                    break;

                case ObsOpCode.RequestResponse:
                    ObsResponse response = ObsProtocol.ParseResponse(message.Data);
                    if (response.RequestId != null)
                        Complete(response.RequestId, response);
                    break;
            }
        }

        private void OnClosed(string reason, int closeCode, double now)
        {
            Close(_active);
            _active = null;
            FailPending("OBS disconnected");

            string known = ObsProtocol.CloseReason(closeCode);
            AuthenticationFailed = closeCode == ObsProtocol.CloseAuthenticationFailed;
            LastError = known ?? reason;

            _failures++;
            double delay = AuthenticationFailed || closeCode == ObsProtocol.CloseUnsupportedRpcVersion
                ? AuthFailedRetrySeconds
                : Math.Min(MaxRetrySeconds, Math.Pow(2, Math.Min(_failures, 5) - 1));
            _reconnectAt = now + delay;
            SetState(ObsConnectionState.Waiting, now);
            Disconnected?.Invoke(LastError);
        }

        private void SetState(ObsConnectionState state, double now)
        {
            State = state;
            _stateSince = now;
        }

        private void Complete(string id, ObsResponse response)
        {
            if (!_pending.TryGetValue(id, out Pending pending))
                return;
            _pending.Remove(id);
            pending.Completion.TrySetResult(response);
        }

        private void FailPending(string reason)
        {
            if (_pending.Count == 0)
                return;
            List<KeyValuePair<string, Pending>> all = new List<KeyValuePair<string, Pending>>(_pending);
            _pending.Clear();
            foreach (KeyValuePair<string, Pending> p in all)
                p.Value.Completion.TrySetResult(ObsResponse.Failed(p.Value.RequestType, reason));
        }

        private void Open(double now)
        {
            SetState(ObsConnectionState.Connecting, now);
            Connection connection = new Connection { Socket = new ClientWebSocket(), Cancel = new CancellationTokenSource() };
            _active = connection;
            _ = RunAsync(connection);
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

        private static void Send(Connection connection, string text)
        {
            if (connection == null)
                return;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            CancellationToken ct = connection.Cancel.Token;
            connection.SendChain = connection.SendChain.ContinueWith(async _ =>
            {
                try
                {
                    await connection.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A failed send means the socket is going down; the receive loop reports the close
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }

        private async Task RunAsync(Connection connection)
        {
            string reason = "closed";
            int closeCode = 0;
            CancellationToken ct = connection.Cancel.Token;
            try
            {
                await connection.Socket.ConnectAsync(new Uri(_url), ct).ConfigureAwait(false);
                _inbound.Enqueue(new Inbound { From = connection, Opened = true });
                byte[] buffer = new byte[16 * 1024];
                using MemoryStream message = new MemoryStream();
                while (!ct.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        closeCode = (int?)connection.Socket.CloseStatus ?? 0;
                        string description = connection.Socket.CloseStatusDescription;
                        reason = string.IsNullOrEmpty(description) ? $"closed by OBS ({closeCode})" : $"closed by OBS ({closeCode} {description})";
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
            catch (Exception e) when (e is WebSocketException || e is ObjectDisposedException || e is IOException
                                      || e is UriFormatException || e is ArgumentException || e is InvalidOperationException)
            {
                reason = e.InnerException?.Message ?? e.Message;
            }
            if (!ct.IsCancellationRequested)
                _inbound.Enqueue(new Inbound { From = connection, ClosedReason = reason, CloseCode = closeCode });
        }
    }
}
