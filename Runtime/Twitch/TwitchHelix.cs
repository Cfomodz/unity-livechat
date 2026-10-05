using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiveChat.Twitch
{
    public class TwitchApiException : Exception
    {
        public TwitchApiException(HttpStatusCode status, string message) : base($"Twitch API {(int)status}: {message}")
        {
            Status = status;
        }

        public HttpStatusCode Status { get; }
    }

    public class TwitchUser
    {
        public string Id;
        public string Login;
        public string DisplayName;
        public string ProfileImageUrl;
    }

    /// <summary>A channel point reward for <see cref="TwitchHelix.CreateCustomRewardAsync"/>.</summary>
    [Serializable]
    public class TwitchRewardSpec
    {
        public string Title;
        public int Cost;
        public string Prompt;
        public bool IsUserInputRequired;
        public string BackgroundColor;
    }

    public class TwitchCustomReward
    {
        public string Id;
        public string Title;
        public int Cost;
    }

    /// <summary>The Helix calls this package uses, authorized with a <see cref="TwitchAccount"/>.</summary>
    public class TwitchHelix
    {
        public const string BaseUrl = "https://api.twitch.tv/helix/";
        private readonly string _clientId;

        public TwitchHelix(string clientId)
        {
            _clientId = clientId;
        }

        public async Task<TwitchUser> GetUserByLoginAsync(TwitchAccount account, string login, CancellationToken ct)
        {
            JObject json = await SendAsync(account, HttpMethod.Get, "users?login=" + Uri.EscapeDataString(login), null, ct);
            JToken user = json["data"]?.FirstOrDefault();
            return user == null ? null : new TwitchUser
            {
                Id = (string)user["id"],
                Login = (string)user["login"],
                DisplayName = (string)user["display_name"],
                ProfileImageUrl = (string)user["profile_image_url"]
            };
        }

        public Task CreateEventSubSubscriptionAsync(TwitchAccount account, string type, string version,
            Dictionary<string, string> condition, string sessionId, CancellationToken ct)
        {
            return SendAsync(account, HttpMethod.Post, "eventsub/subscriptions", new
            {
                type,
                version,
                condition,
                transport = new { method = "websocket", session_id = sessionId }
            }, ct);
        }

        /// <summary>Sends a chat message as the account. Returns null if sent, or Twitch's reason for dropping it.</summary>
        public async Task<string> SendChatMessageAsync(TwitchAccount account, string broadcasterId, string message,
            string replyParentMessageId, CancellationToken ct)
        {
            Dictionary<string, string> body = new Dictionary<string, string>
            {
                ["broadcaster_id"] = broadcasterId,
                ["sender_id"] = account.UserId,
                ["message"] = message
            };
            if (!string.IsNullOrEmpty(replyParentMessageId))
                body["reply_parent_message_id"] = replyParentMessageId;

            JObject json = await SendAsync(account, HttpMethod.Post, "chat/messages", body, ct);
            JToken result = json["data"]?.FirstOrDefault();
            if (result == null || (bool?)result["is_sent"] == true)
                return null;
            return (string)result["drop_reason"].Field("message") ?? (string)result["drop_reason"].Field("code") ?? "dropped";
        }

        /// <summary>The channel's rewards; with <paramref name="onlyManageable"/>, only the ones this app created.</summary>
        public async Task<List<TwitchCustomReward>> GetCustomRewardsAsync(TwitchAccount broadcaster, bool onlyManageable, CancellationToken ct)
        {
            JObject json = await SendAsync(broadcaster, HttpMethod.Get,
                $"channel_points/custom_rewards?broadcaster_id={broadcaster.UserId}&only_manageable_rewards={(onlyManageable ? "true" : "false")}", null, ct);
            return (json["data"] ?? new JArray()).Select(RewardFrom).ToList();
        }

        public async Task<TwitchCustomReward> CreateCustomRewardAsync(TwitchAccount broadcaster, TwitchRewardSpec spec, CancellationToken ct)
        {
            Dictionary<string, object> body = new Dictionary<string, object>
            {
                ["title"] = spec.Title,
                ["cost"] = spec.Cost,
                ["is_user_input_required"] = spec.IsUserInputRequired
            };
            if (!string.IsNullOrEmpty(spec.Prompt))
                body["prompt"] = spec.Prompt;
            if (!string.IsNullOrEmpty(spec.BackgroundColor))
                body["background_color"] = spec.BackgroundColor;

            JObject json = await SendAsync(broadcaster, HttpMethod.Post,
                $"channel_points/custom_rewards?broadcaster_id={broadcaster.UserId}", body, ct);
            return RewardFrom(json["data"]?.First());
        }

        /// <summary>Marks a redemption FULFILLED or CANCELED (which refunds the points). Only works for this app's rewards.</summary>
        public Task UpdateRedemptionStatusAsync(TwitchAccount broadcaster, string rewardId, string redemptionId, bool fulfilled, CancellationToken ct)
        {
            return SendAsync(broadcaster, new HttpMethod("PATCH"),
                $"channel_points/custom_rewards/redemptions?broadcaster_id={broadcaster.UserId}&reward_id={rewardId}&id={redemptionId}",
                new { status = fulfilled ? "FULFILLED" : "CANCELED" }, ct);
        }

        private static TwitchCustomReward RewardFrom(JToken reward) => reward == null ? null : new TwitchCustomReward
        {
            Id = (string)reward["id"],
            Title = (string)reward["title"],
            Cost = (int?)reward["cost"] ?? 0
        };

        /// <summary>
        /// Sends a request with the account's token. On 401 it refreshes the token and tries once more;
        /// on 429 it waits for the rate limit to reset and tries once more.
        /// </summary>
        private async Task<JObject> SendAsync(TwitchAccount account, HttpMethod method, string path, object body, CancellationToken ct)
        {
            string payload = body == null ? null : JsonConvert.SerializeObject(body);
            for (int attempt = 0; ; attempt++)
            {
                string token = await account.GetAccessTokenAsync(ct);
                using HttpRequestMessage request = new HttpRequestMessage(method, BaseUrl + path);
                request.Headers.TryAddWithoutValidation("Client-Id", _clientId);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                if (payload != null)
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await TwitchOAuth.Http.SendAsync(request, ct);
                string text = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                    return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);

                if (attempt == 0 && response.StatusCode == HttpStatusCode.Unauthorized && await account.RefreshAsync(token, ct))
                    continue;
                if (attempt == 0 && (int)response.StatusCode == 429)
                {
                    await Task.Delay(RateLimitDelay(response), ct);
                    continue;
                }

                string message = text;
                try
                {
                    message = (string)JObject.Parse(text)["message"] ?? text;
                }
                catch (JsonException)
                {
                }
                throw new TwitchApiException(response.StatusCode, message);
            }
        }

        private static TimeSpan RateLimitDelay(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Ratelimit-Reset", out IEnumerable<string> values)
                && long.TryParse(values.FirstOrDefault(), out long reset))
            {
                TimeSpan wait = DateTimeOffset.FromUnixTimeSeconds(reset) - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero && wait < TimeSpan.FromMinutes(1))
                    return wait + TimeSpan.FromMilliseconds(250);
            }
            return TimeSpan.FromSeconds(2);
        }
    }
}
