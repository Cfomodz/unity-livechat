using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace LiveChat.Twitch
{
    /// <summary>What to show the user while a device code login is waiting for them.</summary>
    public class TwitchDeviceAuthorization
    {
        /// <summary>Which account this login is for ("bot" or "broadcaster").</summary>
        public string Role;
        /// <summary>The account that should log in, if known (the channel for the broadcaster).</summary>
        public string ExpectedLogin;
        public string UserCode;
        /// <summary>Opens Twitch's activation page with the code filled in.</summary>
        public string VerificationUri;
        public DateTime ExpiresAtUtc;

        public string Instructions =>
            $"Log in to Twitch as {(string.IsNullOrEmpty(ExpectedLogin) ? $"the {Role} account" : ExpectedLogin)}, "
            + $"go to twitch.tv/activate and enter {UserCode}";
    }

    public class TwitchAuthException : Exception
    {
        public TwitchAuthException(string message) : base(message) { }
    }

    /// <summary>
    /// Twitch's OAuth endpoints for a public client (no client secret): the device code grant flow,
    /// refreshing and validating tokens.
    /// </summary>
    public static class TwitchOAuth
    {
        public const string DeviceUrl = "https://id.twitch.tv/oauth2/device";
        public const string TokenUrl = "https://id.twitch.tv/oauth2/token";
        public const string ValidateUrl = "https://id.twitch.tv/oauth2/validate";
        private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

        internal static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public class DeviceCode
        {
            public string Code;
            public string UserCode;
            public string VerificationUri;
            public int IntervalSeconds;
            public DateTime ExpiresAtUtc;
        }

        public class Validation
        {
            public string ClientId;
            public string Login;
            public string UserId;
            public string[] Scopes;
            public int ExpiresInSeconds;
        }

        public static async Task<DeviceCode> RequestDeviceCodeAsync(string clientId, IEnumerable<string> scopes, CancellationToken ct)
        {
            JObject json = await PostFormAsync(DeviceUrl, new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scopes"] = string.Join(" ", scopes)
            }, ct);
            return new DeviceCode
            {
                Code = (string)json["device_code"],
                UserCode = (string)json["user_code"],
                VerificationUri = (string)json["verification_uri"],
                IntervalSeconds = Math.Max(1, (int?)json["interval"] ?? 5),
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds((int?)json["expires_in"] ?? 1800)
            };
        }

        /// <summary>
        /// Polls until the user approves the code. Returns null if the code expires first, so the
        /// caller can issue a new one. Throws <see cref="TwitchAuthException"/> if the user declines.
        /// </summary>
        public static async Task<TwitchToken> PollDeviceCodeAsync(string clientId, IEnumerable<string> scopes, DeviceCode code, CancellationToken ct)
        {
            int interval = code.IntervalSeconds;
            string scopeText = string.Join(" ", scopes);
            while (DateTime.UtcNow < code.ExpiresAtUtc)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), ct);
                (HttpStatusCode status, JObject json) = await PostFormRawAsync(TokenUrl, new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["scopes"] = scopeText,
                    ["device_code"] = code.Code,
                    ["grant_type"] = DeviceGrant
                }, ct);

                if (status == HttpStatusCode.OK)
                    return TokenFrom(json);
                if ((int)status >= 500)
                    continue; // Twitch is having trouble: keep polling

                string message = ((string)json.Field("message") ?? string.Empty).ToLowerInvariant();
                if (message.Contains("authorization_pending"))
                    continue;
                if (message.Contains("slow_down"))
                {
                    interval += 5;
                    continue;
                }
                if (message.Contains("invalid device code") || message.Contains("expired"))
                    return null;
                throw new TwitchAuthException($"Twitch login failed: {(string)json.Field("message") ?? status.ToString()}");
            }
            return null;
        }

        /// <summary>Exchanges a refresh token for a new token pair, or returns null if Twitch rejects it.</summary>
        public static async Task<TwitchToken> RefreshAsync(string clientId, string refreshToken, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(refreshToken))
                return null;
            (HttpStatusCode status, JObject json) = await PostFormRawAsync(TokenUrl, new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            }, ct);
            if (status == HttpStatusCode.OK)
                return TokenFrom(json);
            if (status == HttpStatusCode.BadRequest || status == HttpStatusCode.Unauthorized)
                return null;
            string failure = $"Refreshing the Twitch token failed: {(int)status} {(string)json.Field("message")}";
            if ((int)status >= 500)
                throw new HttpRequestException(failure); // Twitch is having trouble: worth retrying
            throw new TwitchAuthException(failure);
        }

        /// <summary>Validates a token, as Twitch requires at startup and hourly. Null means it's no longer valid.</summary>
        public static async Task<Validation> ValidateAsync(string accessToken, CancellationToken ct)
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, ValidateUrl);
            request.Headers.TryAddWithoutValidation("Authorization", "OAuth " + accessToken);
            using HttpResponseMessage response = await Http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return null;
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                string failure = $"Validating the Twitch token failed: {(int)response.StatusCode} {body}";
                if ((int)response.StatusCode >= 500)
                    throw new HttpRequestException(failure);
                throw new TwitchAuthException(failure);
            }
            JObject json = JObject.Parse(body);
            return new Validation
            {
                ClientId = (string)json["client_id"],
                Login = (string)json["login"],
                UserId = (string)json["user_id"],
                Scopes = json["scopes"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
                ExpiresInSeconds = (int?)json["expires_in"] ?? 0
            };
        }

        private static TwitchToken TokenFrom(JObject json) => new TwitchToken
        {
            AccessToken = (string)json["access_token"],
            RefreshToken = (string)json["refresh_token"],
            Scopes = json["scope"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds((int?)json["expires_in"] ?? 3600)
        };

        private static async Task<JObject> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
        {
            (HttpStatusCode status, JObject json) = await PostFormRawAsync(url, form, ct);
            if (status == HttpStatusCode.OK)
                return json;
            string message = (string)json.Field("message");
            if ((int)status >= 500)
                throw new HttpRequestException($"Twitch returned {(int)status}: {message}");
            if (message != null && message.IndexOf("invalid client", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new TwitchAuthException("Twitch doesn't recognise the client ID. Copy it from your app at "
                    + "dev.twitch.tv/console, and make sure the app's Client Type is Public.");
            throw new TwitchAuthException($"Twitch returned {(int)status}: {message}");
        }

        private static async Task<(HttpStatusCode, JObject)> PostFormRawAsync(string url, Dictionary<string, string> form, CancellationToken ct)
        {
            using FormUrlEncodedContent content = new FormUrlEncodedContent(form);
            using HttpResponseMessage response = await Http.PostAsync(url, content, ct);
            string body = await response.Content.ReadAsStringAsync();
            JObject json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(body) ? null : JObject.Parse(body);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // Non-JSON error body: keep the status code
            }
            return (response.StatusCode, json);
        }
    }
}
