using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LiveChat.Twitch
{
    /// <summary>
    /// Turns EventSub notifications into LiveChat events. Pure, so it can be tested with sample payloads.
    /// </summary>
    public static class EventSubTranslator
    {
        public const string ChatMessage = "channel.chat.message";
        public const string ChatNotification = "channel.chat.notification";
        public const string Follow = "channel.follow";
        public const string Redemption = "channel.channel_points_custom_reward_redemption.add";

        /// <summary>
        /// Returns a <see cref="LiveChatMessage"/>, <see cref="LiveChatSubscriptionEvent"/>,
        /// <see cref="LiveChatGiftSubscriptionEvent"/>, <see cref="LiveChatCommunityGiftEvent"/>,
        /// <see cref="LiveChatRaidEvent"/>, <see cref="LiveChatFollowEvent"/> or
        /// <see cref="LiveChatChannelPointsRedemption"/>, or null for anything the package ignores.
        /// </summary>
        /// <param name="channel">The channel login, copied onto chat messages.</param>
        /// <param name="selfUserId">The logged-in account, so its own messages get <see cref="LiveChatMessage.IsMe"/>.</param>
        public static object Translate(string subscriptionType, JObject ev, string channel, string selfUserId)
        {
            if (ev == null)
                return null;
            switch (subscriptionType)
            {
                case ChatMessage:
                    return Message(ev, channel, selfUserId);
                case ChatNotification:
                    return Notice(ev);
                case Follow:
                    return new LiveChatFollowEvent
                    {
                        UserId = (string)ev["user_id"],
                        Username = (string)ev["user_login"],
                        DisplayName = (string)ev["user_name"]
                    };
                case Redemption:
                    return new LiveChatChannelPointsRedemption
                    {
                        RedemptionId = (string)ev["id"],
                        RewardId = (string)ev["reward"].Field("id"),
                        RewardTitle = (string)ev["reward"].Field("title"),
                        Cost = (int?)ev["reward"].Field("cost") ?? 0,
                        UserId = (string)ev["user_id"],
                        Username = (string)ev["user_login"],
                        DisplayName = (string)ev["user_name"],
                        UserInput = (string)ev["user_input"]
                    };
                default:
                    return null;
            }
        }

        private static LiveChatMessage Message(JObject ev, string channel, string selfUserId)
        {
            HashSet<string> badges = Badges(ev);
            string userId = (string)ev["chatter_user_id"];
            Color color = Color.white;
            string colorHex = (string)ev["color"];
            if (!string.IsNullOrEmpty(colorHex))
                ColorUtility.TryParseHtmlString(colorHex, out color);

            List<LiveChatEmote> emotes = new List<LiveChatEmote>();
            int index = 0;
            foreach (JToken fragment in ev["message"].Field("fragments") ?? new JArray())
            {
                string text = (string)fragment["text"] ?? string.Empty;
                if ((string)fragment["type"] == "emote")
                {
                    string id = (string)fragment["emote"].Field("id");
                    emotes.Add(new LiveChatEmote
                    {
                        Id = id,
                        Name = text,
                        StartIndex = index,
                        EndIndex = index + text.Length - 1,
                        ImageUrl = $"https://static-cdn.jtvnw.net/emoticons/v2/{id}/default/dark/1.0"
                    });
                }
                index += text.Length;
            }

            return new LiveChatMessage
            {
                MessageId = (string)ev["message_id"],
                UserId = userId,
                Username = (string)ev["chatter_user_login"],
                DisplayName = (string)ev["chatter_user_name"],
                Channel = channel,
                RawMessage = (string)ev["message"].Field("text") ?? string.Empty,
                UsernameColor = color,
                IsSubscriber = badges.Contains("subscriber") || badges.Contains("founder"),
                IsModerator = badges.Contains("moderator") || badges.Contains("lead_moderator"),
                IsVip = badges.Contains("vip"),
                IsBroadcaster = badges.Contains("broadcaster") || userId == (string)ev["broadcaster_user_id"],
                IsMe = !string.IsNullOrEmpty(selfUserId) && userId == selfUserId,
                Bits = (int?)ev["cheer"].Field("bits") ?? 0,
                Emotes = emotes
            };
        }

        private static object Notice(JObject ev)
        {
            string chatterId = (string)ev["chatter_user_id"];
            string chatterLogin = (string)ev["chatter_user_login"];
            string chatterName = (string)ev["chatter_user_name"];
            bool anonymous = (bool?)ev["chatter_is_anonymous"] ?? false;

            // shared_chat_* notices are events in the other channels of a shared chat session
            switch ((string)ev["notice_type"])
            {
                case "sub":
                {
                    JToken sub = ev["sub"];
                    return new LiveChatSubscriptionEvent
                    {
                        UserId = chatterId,
                        Username = chatterLogin,
                        DisplayName = chatterName,
                        Plan = Plan((string)sub.Field("sub_tier"), (bool?)sub.Field("is_prime") ?? false),
                        CumulativeMonths = 1,
                        DurationMonths = (int?)sub.Field("duration_months") ?? 1
                    };
                }
                case "resub":
                {
                    JToken resub = ev["resub"];
                    return new LiveChatSubscriptionEvent
                    {
                        UserId = chatterId,
                        Username = chatterLogin,
                        DisplayName = chatterName,
                        Plan = Plan((string)resub.Field("sub_tier"), (bool?)resub.Field("is_prime") ?? false),
                        CumulativeMonths = (int?)resub.Field("cumulative_months") ?? 1,
                        DurationMonths = (int?)resub.Field("duration_months") ?? 1,
                        IsResub = true,
                        Message = (string)ev["message"].Field("text")
                    };
                }
                case "sub_gift":
                {
                    JToken gift = ev["sub_gift"];
                    return new LiveChatGiftSubscriptionEvent
                    {
                        GifterUserId = anonymous ? null : chatterId,
                        GifterUsername = anonymous ? null : chatterLogin,
                        GifterDisplayName = anonymous ? null : chatterName,
                        GifterIsAnonymous = anonymous,
                        RecipientUserId = (string)gift.Field("recipient_user_id"),
                        RecipientUsername = (string)gift.Field("recipient_user_login"),
                        RecipientDisplayName = (string)gift.Field("recipient_user_name"),
                        Plan = Plan((string)gift.Field("sub_tier"), false),
                        DurationMonths = (int?)gift.Field("duration_months") ?? 1,
                        CommunityGiftId = (string)gift.Field("community_gift_id")
                    };
                }
                case "community_sub_gift":
                {
                    JToken gift = ev["community_sub_gift"];
                    return new LiveChatCommunityGiftEvent
                    {
                        Id = (string)gift.Field("id"),
                        GifterUserId = anonymous ? null : chatterId,
                        GifterUsername = anonymous ? null : chatterLogin,
                        GifterDisplayName = anonymous ? null : chatterName,
                        GifterIsAnonymous = anonymous,
                        Count = (int?)gift.Field("total") ?? 0,
                        Plan = Plan((string)gift.Field("sub_tier"), false)
                    };
                }
                case "raid":
                {
                    JToken raid = ev["raid"];
                    return new LiveChatRaidEvent
                    {
                        FromUserId = (string)raid.Field("user_id"),
                        FromUsername = (string)raid.Field("user_login"),
                        FromDisplayName = (string)raid.Field("user_name"),
                        Viewers = (int?)raid.Field("viewer_count") ?? 0,
                        ProfileImageUrl = (string)raid.Field("profile_image_url")
                    };
                }
                default:
                    return null;
            }
        }

        private static HashSet<string> Badges(JObject ev)
        {
            return new HashSet<string>((ev["badges"] ?? new JArray())
                .Select(badge => (string)badge["set_id"])
                .Where(id => !string.IsNullOrEmpty(id)));
        }

        public static LiveChatSubscriptionPlan Plan(string tier, bool isPrime)
        {
            if (isPrime)
                return LiveChatSubscriptionPlan.Prime;
            switch (tier)
            {
                case "1000": return LiveChatSubscriptionPlan.Tier1;
                case "2000": return LiveChatSubscriptionPlan.Tier2;
                case "3000": return LiveChatSubscriptionPlan.Tier3;
                default: return LiveChatSubscriptionPlan.Unknown;
            }
        }
    }
}
