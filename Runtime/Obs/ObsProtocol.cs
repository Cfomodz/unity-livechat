using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiveChat.Obs
{
    /// <summary>obs-websocket v5 op codes.</summary>
    public enum ObsOpCode
    {
        Hello = 0,
        Identify = 1,
        Identified = 2,
        Reidentify = 3,
        Event = 5,
        Request = 6,
        RequestResponse = 7,
        RequestBatch = 8,
        RequestBatchResponse = 9
    }

    /// <summary>obs-websocket v5 event subscription bits, for Identify's eventSubscriptions.</summary>
    [Flags]
    public enum ObsEventSubscription
    {
        None = 0,
        General = 1 << 0,
        Config = 1 << 1,
        Scenes = 1 << 2,
        Inputs = 1 << 3,
        Transitions = 1 << 4,
        Filters = 1 << 5,
        Outputs = 1 << 6,
        SceneItems = 1 << 7,
        MediaInputs = 1 << 8,
        Vendors = 1 << 9,
        Ui = 1 << 10
    }

    /// <summary>One message off the socket: its op code and its "d" payload.</summary>
    public sealed class ObsMessage
    {
        public ObsOpCode Op;
        public JObject Data;
    }

    /// <summary>The Hello's fields the client needs.</summary>
    public sealed class ObsHello
    {
        public int RpcVersion;
        public string ObsWebSocketVersion;
        /// <summary>Null when the server has authentication off.</summary>
        public string Challenge;
        public string Salt;
        public bool AuthenticationRequired => Challenge != null;
    }

    /// <summary>A request's result: OBS's status (code and comment) and response data.</summary>
    public sealed class ObsResponse
    {
        public string RequestType;
        public string RequestId;
        public bool Ok;
        public int Code;
        /// <summary>OBS's reason when a request fails (or the client's, e.g. "not connected").</summary>
        public string Comment;
        public JObject Data;

        public static ObsResponse Failed(string requestType, string reason)
        {
            return new ObsResponse { RequestType = requestType, Ok = false, Code = 0, Comment = reason };
        }

        /// <summary>A short reason for chat, e.g. "OutputRunning (500)" when OBS sent no comment.</summary>
        public string Reason
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Comment))
                    return Comment;
                string name = ObsProtocol.StatusName(Code);
                return name != null ? $"{name} ({Code})" : $"error {Code}";
            }
        }
    }

    /// <summary>
    /// The engine-free parts of obs-websocket v5: the authentication string and building and parsing
    /// messages. See https://github.com/obsproject/obs-websocket/blob/master/docs/generated/protocol.md
    /// </summary>
    public static class ObsProtocol
    {
        public const int RpcVersion = 1;
        public const string DefaultUrl = "ws://127.0.0.1:4455";

        /// <summary>Request status codes the client reports by name.</summary>
        public const int StatusSuccess = 100;
        public const int StatusOutputRunning = 500;
        public const int StatusOutputNotRunning = 501;
        public const int StatusResourceNotFound = 600;

        /// <summary>Close codes the client handles specially.</summary>
        public const int CloseAuthenticationFailed = 4009;
        public const int CloseUnsupportedRpcVersion = 4010;

        /// <summary>
        /// The Identify authentication string: secret = base64(sha256(password + salt)),
        /// auth = base64(sha256(secret + challenge)). UTF-8 throughout.
        /// </summary>
        public static string ComputeAuthentication(string password, string salt, string challenge)
        {
            string secret = Sha256Base64((password ?? string.Empty) + (salt ?? string.Empty));
            return Sha256Base64(secret + (challenge ?? string.Empty));
        }

        private static string Sha256Base64(string text)
        {
            using SHA256 sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        /// <summary>Parses one message, or returns null if it isn't a JSON object with an op and a "d" object.</summary>
        public static ObsMessage Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            JObject message;
            try
            {
                using JsonTextReader reader = new JsonTextReader(new System.IO.StringReader(text)) { DateParseHandling = DateParseHandling.None };
                message = JObject.Load(reader);
            }
            catch (JsonException)
            {
                return null;
            }
            JToken op = message["op"];
            if (op == null || op.Type != JTokenType.Integer)
                return null;
            return new ObsMessage { Op = (ObsOpCode)(int)op, Data = message["d"] as JObject ?? new JObject() };
        }

        public static ObsHello ParseHello(JObject d)
        {
            JObject auth = d?["authentication"] as JObject;
            return new ObsHello
            {
                RpcVersion = (int?)d?["rpcVersion"] ?? 0,
                ObsWebSocketVersion = (string)d?["obsWebSocketVersion"],
                Challenge = (string)auth?["challenge"],
                Salt = (string)auth?["salt"]
            };
        }

        /// <summary>The Identify reply to a Hello. Pass the password only if the Hello asked for authentication.</summary>
        public static string BuildIdentify(ObsHello hello, string password, ObsEventSubscription subscriptions)
        {
            JObject d = new JObject
            {
                ["rpcVersion"] = RpcVersion,
                ["eventSubscriptions"] = (int)subscriptions
            };
            if (hello != null && hello.AuthenticationRequired)
                d["authentication"] = ComputeAuthentication(password, hello.Salt, hello.Challenge);
            return Wrap(ObsOpCode.Identify, d);
        }

        public static string BuildRequest(string requestType, string requestId, JObject requestData = null)
        {
            JObject d = new JObject
            {
                ["requestType"] = requestType,
                ["requestId"] = requestId
            };
            if (requestData != null)
                d["requestData"] = requestData;
            return Wrap(ObsOpCode.Request, d);
        }

        public static ObsResponse ParseResponse(JObject d)
        {
            JObject status = d?["requestStatus"] as JObject;
            return new ObsResponse
            {
                RequestType = (string)d?["requestType"],
                RequestId = (string)d?["requestId"],
                Ok = (bool?)status?["result"] ?? false,
                Code = (int?)status?["code"] ?? 0,
                Comment = (string)status?["comment"],
                Data = d?["responseData"] as JObject
            };
        }

        /// <summary>An event's type and data, from an Event (op 5) payload.</summary>
        public static (string type, JObject data) ParseEvent(JObject d)
        {
            return ((string)d?["eventType"], d?["eventData"] as JObject ?? new JObject());
        }

        private static string Wrap(ObsOpCode op, JObject d)
        {
            return new JObject { ["op"] = (int)op, ["d"] = d }.ToString(Formatting.None);
        }

        public static string StatusName(int code)
        {
            switch (code)
            {
                case StatusSuccess: return "Success";
                case 204: return "UnknownRequestType";
                case 205: return "GenericError";
                case 207: return "NotReady";
                case 300: return "MissingRequestField";
                case 400: return "InvalidRequestField";
                case StatusOutputRunning: return "OutputRunning";
                case StatusOutputNotRunning: return "OutputNotRunning";
                case 502: return "OutputPaused";
                case 503: return "OutputNotPaused";
                case 504: return "OutputDisabled";
                case 505: return "StudioModeActive";
                case 506: return "StudioModeNotActive";
                case StatusResourceNotFound: return "ResourceNotFound";
                case 604: return "InvalidResourceState";
                case 701: return "ResourceActionFailed";
                case 702: return "RequestProcessingFailed";
                case 703: return "CannotAct";
                default: return null;
            }
        }

        /// <summary>A plain reason for a close code, or null for codes that just mean "OBS went away".</summary>
        public static string CloseReason(int code)
        {
            switch (code)
            {
                case CloseAuthenticationFailed: return "wrong password";
                case CloseUnsupportedRpcVersion: return "unsupported obs-websocket version";
                case 4011: return "session closed by OBS";
                case 4002:
                case 4003:
                case 4004:
                case 4005:
                case 4006:
                case 4007:
                case 4008:
                case 4012: return $"protocol error ({code})";
                default: return null;
            }
        }
    }
}
