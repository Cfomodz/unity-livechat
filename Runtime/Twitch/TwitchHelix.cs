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
        /// <summary>False hides the reward from viewers.</summary>
        public bool IsEnabled;
        /// <summary>True shows the reward but stops viewers redeeming it.</summary>
        public bool IsPaused;
    }

    /// <summary>What <see cref="TwitchHelix.ModifyChannelInformationAsync"/> changes. Null fields are left as they are.</summary>
    [Serializable]
    public class TwitchStreamInfo
    {
        public string Title;
        /// <summary>A category (game) name as Twitch shows it, e.g. "Minecraft" or "Games + Demos".</summary>
        public string Category;
        /// <summary>Up to 10 tags of up to 25 letters or digits each. They replace the channel's tags.</summary>
        public string[] Tags;

        public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Category) && (Tags == null || Tags.Length == 0);
    }

    /// <summary>A live stream, from <see cref="TwitchHelix.GetStreamAsync"/>.</summary>
    public class TwitchStream
    {
        public string Id;
        public string UserId;
        public string Title;
        public string CategoryName;
        public int ViewerCount;
        public DateTime StartedAt;
    }

    public enum TwitchPredictionStatus
    {
        /// <summary>Taking bets.</summary>
        Active,
        /// <summary>Bets are closed; waiting for a result.</summary>
        Locked,
        Resolved,
        /// <summary>Canceled, and the points refunded.</summary>
        Canceled
    }

    public class TwitchPredictionOutcome
    {
        public string Id;
        public string Title;
        /// <summary>How many viewers bet on this outcome.</summary>
        public int Users;
        public long ChannelPoints;
    }

    public class TwitchPrediction
    {
        public string Id;
        public string Title;
        public TwitchPredictionStatus Status;
        /// <summary>Set once the prediction is resolved, otherwise null.</summary>
        public string WinningOutcomeId;
        public List<TwitchPredictionOutcome> Outcomes = new List<TwitchPredictionOutcome>();
    }

    public enum TwitchPollStatus
    {
        Active,
        Completed,
        /// <summary>Ended early.</summary>
        Terminated,
        /// <summary>Ended and hidden from viewers.</summary>
        Archived,
        Moderated,
        Invalid
    }

    public class TwitchPollChoice
    {
        public string Id;
        public string Title;
        /// <summary>All votes, including ones bought with channel points.</summary>
        public int Votes;
        public int ChannelPointsVotes;
    }

    public class TwitchPoll
    {
        public string Id;
        public string Title;
        public TwitchPollStatus Status;
        public List<TwitchPollChoice> Choices = new List<TwitchPollChoice>();
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

        /// <summary>
        /// The user IDs of everyone in the channel's chat right now (Twitch's list lags a few minutes).
        /// The account must be the broadcaster or a moderator, with moderator:read:chatters.
        /// </summary>
        public async Task<HashSet<string>> GetChatterIdsAsync(TwitchAccount moderator, string broadcasterId, CancellationToken ct)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            string cursor = null;
            do
            {
                string path = $"chat/chatters?broadcaster_id={broadcasterId}&moderator_id={moderator.UserId}&first=1000"
                    + (cursor == null ? "" : "&after=" + Uri.EscapeDataString(cursor));
                JObject json = await SendAsync(moderator, HttpMethod.Get, path, null, ct);
                foreach (JToken chatter in json["data"] ?? new JArray())
                {
                    string id = (string)chatter["user_id"];
                    if (!string.IsNullOrEmpty(id))
                        ids.Add(id);
                }
                cursor = (string)json["pagination"].Field("cursor");
            }
            while (!string.IsNullOrEmpty(cursor));
            return ids;
        }

        public async Task<TwitchUser> GetUserByLoginAsync(TwitchAccount account, string login, CancellationToken ct)
        {
            return (await GetUsersAsync(account, null, new[] { login }, ct)).FirstOrDefault();
        }

        public async Task<TwitchUser> GetUserByIdAsync(TwitchAccount account, string userId, CancellationToken ct)
        {
            return (await GetUsersAsync(account, new[] { userId }, null, ct)).FirstOrDefault();
        }

        /// <summary>Looks up users by ID and by login, up to 100 in total. Ones that don't exist are left out.</summary>
        public async Task<List<TwitchUser>> GetUsersAsync(TwitchAccount account, IEnumerable<string> userIds, IEnumerable<string> logins, CancellationToken ct)
        {
            List<string> query = new List<string>();
            query.AddRange((userIds ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)).Select(id => "id=" + Uri.EscapeDataString(id)));
            query.AddRange((logins ?? Enumerable.Empty<string>()).Where(login => !string.IsNullOrEmpty(login)).Select(login => "login=" + Uri.EscapeDataString(login)));
            if (query.Count == 0)
                return new List<TwitchUser>();
            if (query.Count > 100)
                throw new ArgumentException("Twitch looks up at most 100 users at once.");

            JObject json = await SendAsync(account, HttpMethod.Get, "users?" + string.Join("&", query), null, ct);
            return (json["data"] ?? new JArray()).Select(UserFrom).ToList();
        }

        /// <summary>The broadcaster's stream, or null when they aren't live.</summary>
        public async Task<TwitchStream> GetStreamAsync(TwitchAccount account, string broadcasterId, CancellationToken ct)
        {
            JObject json = await SendAsync(account, HttpMethod.Get, "streams?user_id=" + Uri.EscapeDataString(broadcasterId), null, ct);
            return StreamFrom(json["data"]?.FirstOrDefault());
        }

        /// <summary>
        /// Starts a prediction. Twitch allows a title of up to 45 characters, 2 to 10 outcomes of up to
        /// 25 characters each, and 30 to 1800 seconds for bets. Needs channel:manage:predictions.
        /// </summary>
        public async Task<TwitchPrediction> CreatePredictionAsync(TwitchAccount broadcaster, string title, IEnumerable<string> outcomes,
            int predictionWindowSeconds, CancellationToken ct)
        {
            List<string> titles = outcomes.ToList();
            if (titles.Count < 2 || titles.Count > 10)
                throw new ArgumentException("A prediction needs 2 to 10 outcomes.");

            JObject json = await SendAsync(broadcaster, HttpMethod.Post, "predictions", new
            {
                broadcaster_id = broadcaster.UserId,
                title,
                outcomes = titles.Select(outcome => new { title = outcome }),
                prediction_window = predictionWindowSeconds
            }, ct);
            return PredictionFrom(json["data"]?.FirstOrDefault());
        }

        /// <summary>The broadcaster's most recent predictions, newest first.</summary>
        public async Task<List<TwitchPrediction>> GetPredictionsAsync(TwitchAccount broadcaster, int first, CancellationToken ct)
        {
            JObject json = await SendAsync(broadcaster, HttpMethod.Get,
                $"predictions?broadcaster_id={broadcaster.UserId}&first={Math.Max(1, Math.Min(25, first))}", null, ct);
            return (json["data"] ?? new JArray()).Select(PredictionFrom).ToList();
        }

        /// <summary>
        /// Resolves a prediction (<paramref name="winningOutcomeId"/> is required), cancels it (refunding
        /// the points) or locks it (closing bets). Needs channel:manage:predictions.
        /// </summary>
        public Task EndPredictionAsync(TwitchAccount broadcaster, string predictionId, TwitchPredictionStatus status,
            string winningOutcomeId, CancellationToken ct)
        {
            if (status == TwitchPredictionStatus.Active)
                throw new ArgumentException("A prediction can only be resolved, canceled or locked.");
            if (status == TwitchPredictionStatus.Resolved && string.IsNullOrEmpty(winningOutcomeId))
                throw new ArgumentException("Resolving a prediction needs the winning outcome.");

            Dictionary<string, string> body = new Dictionary<string, string>
            {
                ["broadcaster_id"] = broadcaster.UserId,
                ["id"] = predictionId,
                ["status"] = status.ToString().ToUpperInvariant()
            };
            if (status == TwitchPredictionStatus.Resolved)
                body["winning_outcome_id"] = winningOutcomeId;
            return SendAsync(broadcaster, new HttpMethod("PATCH"), "predictions", body, ct);
        }

        /// <summary>
        /// Starts a poll. Twitch allows a title of up to 60 characters, 2 to 5 choices of up to 25
        /// characters each, and 15 to 1800 seconds. Needs channel:manage:polls.
        /// </summary>
        public async Task<TwitchPoll> CreatePollAsync(TwitchAccount broadcaster, string title, IEnumerable<string> choices,
            int durationSeconds, CancellationToken ct)
        {
            List<string> titles = choices.ToList();
            if (titles.Count < 2 || titles.Count > 5)
                throw new ArgumentException("A poll needs 2 to 5 choices.");

            JObject json = await SendAsync(broadcaster, HttpMethod.Post, "polls", new
            {
                broadcaster_id = broadcaster.UserId,
                title,
                choices = titles.Select(choice => new { title = choice }),
                duration = durationSeconds
            }, ct);
            return PollFrom(json["data"]?.FirstOrDefault());
        }

        /// <summary>The broadcaster's most recent polls, newest first.</summary>
        public async Task<List<TwitchPoll>> GetPollsAsync(TwitchAccount broadcaster, int first, CancellationToken ct)
        {
            JObject json = await SendAsync(broadcaster, HttpMethod.Get,
                $"polls?broadcaster_id={broadcaster.UserId}&first={Math.Max(1, Math.Min(20, first))}", null, ct);
            return (json["data"] ?? new JArray()).Select(PollFrom).ToList();
        }

        /// <summary>Ends a poll early: Terminated leaves the results showing, Archived hides them. Needs channel:manage:polls.</summary>
        public Task EndPollAsync(TwitchAccount broadcaster, string pollId, TwitchPollStatus status, CancellationToken ct)
        {
            if (status != TwitchPollStatus.Terminated && status != TwitchPollStatus.Archived)
                throw new ArgumentException("A poll can only be ended as Terminated or Archived.");
            return SendAsync(broadcaster, new HttpMethod("PATCH"), "polls", new Dictionary<string, string>
            {
                ["broadcaster_id"] = broadcaster.UserId,
                ["id"] = pollId,
                ["status"] = status.ToString().ToUpperInvariant()
            }, ct);
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
            JObject json = await SendAsync(broadcaster, HttpMethod.Post,
                $"channel_points/custom_rewards?broadcaster_id={broadcaster.UserId}", RewardBody(spec), ct);
            return RewardFrom(json["data"]?.First());
        }

        private static Dictionary<string, object> RewardBody(TwitchRewardSpec spec)
        {
            Dictionary<string, object> body = new Dictionary<string, object>
            {
                ["title"] = spec.Title,
                ["cost"] = spec.Cost,
                ["is_user_input_required"] = spec.IsUserInputRequired,
                // Empty clears a prompt a reward had before
                ["prompt"] = spec.Prompt ?? string.Empty
            };
            if (!string.IsNullOrEmpty(spec.BackgroundColor))
                body["background_color"] = spec.BackgroundColor;
            return body;
        }

        /// <summary>Marks a redemption FULFILLED or CANCELED (which refunds the points). Only works for this app's rewards.</summary>
        public Task UpdateRedemptionStatusAsync(TwitchAccount broadcaster, string rewardId, string redemptionId, bool fulfilled, CancellationToken ct)
        {
            return SendAsync(broadcaster, new HttpMethod("PATCH"),
                $"channel_points/custom_rewards/redemptions?broadcaster_id={broadcaster.UserId}&reward_id={rewardId}&id={redemptionId}",
                new { status = fulfilled ? "FULFILLED" : "CANCELED" }, ct);
        }

        /// <summary>Shows or hides one of this app's rewards. Only the app that created a reward can change it.</summary>
        public Task UpdateCustomRewardAsync(TwitchAccount broadcaster, string rewardId, bool isEnabled, CancellationToken ct)
        {
            return SendAsync(broadcaster, new HttpMethod("PATCH"),
                $"channel_points/custom_rewards?broadcaster_id={broadcaster.UserId}&id={rewardId}",
                new { is_enabled = isEnabled }, ct);
        }

        /// <summary>Changes one of this app's rewards to match <paramref name="spec"/>, and shows or hides it.</summary>
        public Task UpdateCustomRewardAsync(TwitchAccount broadcaster, string rewardId, TwitchRewardSpec spec, bool isEnabled, CancellationToken ct)
        {
            Dictionary<string, object> body = RewardBody(spec);
            body["is_enabled"] = isEnabled;
            return SendAsync(broadcaster, new HttpMethod("PATCH"),
                $"channel_points/custom_rewards?broadcaster_id={broadcaster.UserId}&id={rewardId}", body, ct);
        }

        /// <summary>Deletes one of this app's rewards.</summary>
        public Task DeleteCustomRewardAsync(TwitchAccount broadcaster, string rewardId, CancellationToken ct)
        {
            return SendAsync(broadcaster, HttpMethod.Delete,
                $"channel_points/custom_rewards?broadcaster_id={broadcaster.UserId}&id={rewardId}", null, ct);
        }

        /// <summary>The IDs of up to 50 of a reward's redemptions still waiting to be fulfilled or refunded.</summary>
        public async Task<List<string>> GetUnfulfilledRedemptionIdsAsync(TwitchAccount broadcaster, string rewardId, CancellationToken ct)
        {
            JObject json = await SendAsync(broadcaster, HttpMethod.Get,
                $"channel_points/custom_rewards/redemptions?broadcaster_id={broadcaster.UserId}&reward_id={rewardId}&status=UNFULFILLED&first=50", null, ct);
            return (json["data"] ?? new JArray()).Select(redemption => (string)redemption["id"]).Where(id => id != null).ToList();
        }

        /// <summary>Finds a category by its exact name, or by search if the name isn't exact. Null if there's none.</summary>
        public async Task<string> FindCategoryIdAsync(TwitchAccount account, string name, CancellationToken ct)
        {
            JObject exact = await SendAsync(account, HttpMethod.Get, "games?name=" + Uri.EscapeDataString(name), null, ct);
            string id = (string)exact["data"]?.FirstOrDefault()?["id"];
            if (id != null)
                return id;

            JObject search = await SendAsync(account, HttpMethod.Get, "search/categories?first=10&query=" + Uri.EscapeDataString(name), null, ct);
            JToken match = (search["data"] ?? new JArray()).FirstOrDefault(category =>
                string.Equals((string)category["name"], name, StringComparison.OrdinalIgnoreCase));
            return (string)match?["id"];
        }

        /// <summary>Sets the broadcaster's stream title, category and tags. Needs channel:manage:broadcast.</summary>
        public Task ModifyChannelInformationAsync(TwitchAccount broadcaster, string title, string categoryId, string[] tags, CancellationToken ct)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(title))
                body["title"] = title.Trim();
            if (!string.IsNullOrEmpty(categoryId))
                body["game_id"] = categoryId;
            if (tags != null)
                body["tags"] = tags;
            return SendAsync(broadcaster, new HttpMethod("PATCH"), $"channels?broadcaster_id={broadcaster.UserId}", body, ct);
        }

        public static TwitchUser UserFrom(JToken user) => user == null ? null : new TwitchUser
        {
            Id = (string)user["id"],
            Login = (string)user["login"],
            DisplayName = (string)user["display_name"],
            ProfileImageUrl = (string)user["profile_image_url"]
        };

        public static TwitchStream StreamFrom(JToken stream) => stream == null ? null : new TwitchStream
        {
            Id = (string)stream["id"],
            UserId = (string)stream["user_id"],
            Title = (string)stream["title"],
            CategoryName = (string)stream["game_name"],
            ViewerCount = (int?)stream["viewer_count"] ?? 0,
            StartedAt = (DateTime?)stream["started_at"] ?? default
        };

        public static TwitchPrediction PredictionFrom(JToken prediction) => prediction == null ? null : new TwitchPrediction
        {
            Id = (string)prediction["id"],
            Title = (string)prediction["title"],
            Status = ParseStatus((string)prediction["status"], TwitchPredictionStatus.Active),
            WinningOutcomeId = (string)prediction["winning_outcome_id"],
            Outcomes = (prediction["outcomes"] as JArray ?? new JArray()).Select(outcome => new TwitchPredictionOutcome
            {
                Id = (string)outcome["id"],
                Title = (string)outcome["title"],
                Users = (int?)outcome["users"] ?? 0,
                ChannelPoints = (long?)outcome["channel_points"] ?? 0
            }).ToList()
        };

        public static TwitchPoll PollFrom(JToken poll) => poll == null ? null : new TwitchPoll
        {
            Id = (string)poll["id"],
            Title = (string)poll["title"],
            Status = ParseStatus((string)poll["status"], TwitchPollStatus.Invalid),
            Choices = (poll["choices"] as JArray ?? new JArray()).Select(choice => new TwitchPollChoice
            {
                Id = (string)choice["id"],
                Title = (string)choice["title"],
                Votes = (int?)choice["votes"] ?? 0,
                ChannelPointsVotes = (int?)choice["channel_points_votes"] ?? 0
            }).ToList()
        };

        private static T ParseStatus<T>(string status, T fallback) where T : struct =>
            Enum.TryParse(status, true, out T parsed) ? parsed : fallback;

        private static TwitchCustomReward RewardFrom(JToken reward) => reward == null ? null : new TwitchCustomReward
        {
            Id = (string)reward["id"],
            Title = (string)reward["title"],
            Cost = (int?)reward["cost"] ?? 0,
            IsEnabled = (bool?)reward["is_enabled"] ?? true,
            IsPaused = (bool?)reward["is_paused"] ?? false
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
