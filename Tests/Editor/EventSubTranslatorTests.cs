using LiveChat.Twitch;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace LiveChat.Tests
{
    /// <summary>Payloads follow the examples in Twitch's EventSub reference.</summary>
    public class EventSubTranslatorTests
    {
        private const string Bot = "999";

        private static object Translate(string type, string json) =>
            EventSubTranslator.Translate(type, JObject.Parse(json), "streamer", Bot);

        [Test]
        public void ChatMessageCarriesSenderBadgesColorAndCheer()
        {
            LiveChatMessage message = (LiveChatMessage)Translate(EventSubTranslator.ChatMessage, @"{
                ""broadcaster_user_id"": ""1"", ""broadcaster_user_login"": ""streamer"",
                ""chatter_user_id"": ""42"", ""chatter_user_login"": ""viewer"", ""chatter_user_name"": ""Viewer"",
                ""message_id"": ""abc"",
                ""message"": { ""text"": ""!buy diamond Cheer300"", ""fragments"": [
                    { ""type"": ""text"", ""text"": ""!buy diamond "" },
                    { ""type"": ""cheermote"", ""text"": ""Cheer300"", ""cheermote"": { ""prefix"": ""cheer"", ""bits"": 300, ""tier"": 100 } } ] },
                ""color"": ""#00FF7F"",
                ""badges"": [ { ""set_id"": ""moderator"", ""id"": ""1"" }, { ""set_id"": ""founder"", ""id"": ""0"" } ],
                ""message_type"": ""text"",
                ""cheer"": { ""bits"": 300 }
            }");

            Assert.AreEqual("abc", message.MessageId);
            Assert.AreEqual("42", message.UserId);
            Assert.AreEqual("viewer", message.Username);
            Assert.AreEqual("Viewer", message.DisplayName);
            Assert.AreEqual("streamer", message.Channel);
            Assert.AreEqual("!buy diamond Cheer300", message.RawMessage);
            Assert.AreEqual(300, message.Bits);
            Assert.IsTrue(message.IsModerator);
            Assert.IsTrue(message.IsSubscriber, "Founders are subscribers");
            Assert.IsFalse(message.IsVip);
            Assert.IsFalse(message.IsBroadcaster);
            Assert.IsFalse(message.IsMe);
            Assert.AreEqual(new Color(0f, 1f, 127f / 255f), message.UsernameColor);
        }

        [Test]
        public void ChatMessageFindsEmotesAndTheBroadcaster()
        {
            LiveChatMessage message = (LiveChatMessage)Translate(EventSubTranslator.ChatMessage, @"{
                ""broadcaster_user_id"": ""1"", ""chatter_user_id"": ""1"", ""chatter_user_login"": ""streamer"", ""chatter_user_name"": ""Streamer"",
                ""message_id"": ""m"", ""message"": { ""text"": ""hi Kappa"", ""fragments"": [
                    { ""type"": ""text"", ""text"": ""hi "" },
                    { ""type"": ""emote"", ""text"": ""Kappa"", ""emote"": { ""id"": ""25"" } } ] },
                ""color"": """", ""badges"": []
            }");

            Assert.IsTrue(message.IsBroadcaster);
            Assert.AreEqual(Color.white, message.UsernameColor);
            Assert.AreEqual(0, message.Bits);
            Assert.AreEqual(1, message.Emotes.Count);
            Assert.AreEqual("Kappa", message.Emotes[0].Name);
            Assert.AreEqual(3, message.Emotes[0].StartIndex);
            Assert.AreEqual(7, message.Emotes[0].EndIndex);
            StringAssert.Contains("/25/", message.Emotes[0].ImageUrl);
        }

        [Test]
        public void TheBotsOwnMessagesAreMarked()
        {
            LiveChatMessage message = (LiveChatMessage)Translate(EventSubTranslator.ChatMessage, @"{
                ""broadcaster_user_id"": ""1"", ""chatter_user_id"": ""999"", ""chatter_user_login"": ""bot"", ""chatter_user_name"": ""Bot"",
                ""message_id"": ""m"", ""message"": { ""text"": ""hello"", ""fragments"": [] }, ""badges"": [ { ""set_id"": ""vip"" } ]
            }");
            Assert.IsTrue(message.IsMe);
            Assert.IsTrue(message.IsVip);
        }

        [Test]
        public void ResubKeepsMonthsTierAndMessage()
        {
            LiveChatSubscriptionEvent sub = (LiveChatSubscriptionEvent)Translate(EventSubTranslator.ChatNotification, @"{
                ""chatter_user_id"": ""42"", ""chatter_user_login"": ""viewer"", ""chatter_user_name"": ""Viewer"", ""chatter_is_anonymous"": false,
                ""notice_type"": ""resub"", ""message"": { ""text"": ""still here"", ""fragments"": [] },
                ""resub"": { ""cumulative_months"": 10, ""duration_months"": 1, ""streak_months"": 3, ""sub_tier"": ""2000"", ""is_prime"": false, ""is_gift"": false }
            }");
            Assert.IsTrue(sub.IsResub);
            Assert.AreEqual(10, sub.CumulativeMonths);
            Assert.AreEqual(LiveChatSubscriptionPlan.Tier2, sub.Plan);
            Assert.AreEqual("still here", sub.Message);
            Assert.AreEqual("42", sub.UserId);
        }

        [Test]
        public void PrimeSubIsPrime()
        {
            LiveChatSubscriptionEvent sub = (LiveChatSubscriptionEvent)Translate(EventSubTranslator.ChatNotification, @"{
                ""chatter_user_id"": ""42"", ""chatter_user_login"": ""viewer"", ""chatter_user_name"": ""Viewer"",
                ""notice_type"": ""sub"", ""sub"": { ""sub_tier"": ""1000"", ""is_prime"": true, ""duration_months"": 1 }
            }");
            Assert.IsFalse(sub.IsResub);
            Assert.AreEqual(1, sub.CumulativeMonths);
            Assert.AreEqual(LiveChatSubscriptionPlan.Prime, sub.Plan);
        }

        [Test]
        public void CommunityGiftAndItsGiftsAreLinked()
        {
            LiveChatCommunityGiftEvent community = (LiveChatCommunityGiftEvent)Translate(EventSubTranslator.ChatNotification, @"{
                ""chatter_user_id"": ""42"", ""chatter_user_login"": ""generous"", ""chatter_user_name"": ""Generous"", ""chatter_is_anonymous"": false,
                ""notice_type"": ""community_sub_gift"", ""community_sub_gift"": { ""id"": ""cg1"", ""total"": 5, ""sub_tier"": ""1000"", ""cumulative_total"": 50 }
            }");
            Assert.AreEqual("cg1", community.Id);
            Assert.AreEqual(5, community.Count);
            Assert.AreEqual("Generous", community.GifterDisplayName);
            Assert.IsFalse(community.GifterIsAnonymous);

            LiveChatGiftSubscriptionEvent gift = (LiveChatGiftSubscriptionEvent)Translate(EventSubTranslator.ChatNotification, @"{
                ""chatter_user_id"": ""42"", ""chatter_user_login"": ""generous"", ""chatter_user_name"": ""Generous"", ""chatter_is_anonymous"": false,
                ""notice_type"": ""sub_gift"", ""sub_gift"": { ""duration_months"": 1, ""recipient_user_id"": ""7"", ""recipient_user_login"": ""lucky"",
                    ""recipient_user_name"": ""Lucky"", ""sub_tier"": ""3000"", ""community_gift_id"": ""cg1"" }
            }");
            Assert.AreEqual("cg1", gift.CommunityGiftId);
            Assert.AreEqual("7", gift.RecipientUserId);
            Assert.AreEqual("Lucky", gift.RecipientDisplayName);
            Assert.AreEqual("42", gift.GifterUserId);
            Assert.AreEqual(LiveChatSubscriptionPlan.Tier3, gift.Plan);
        }

        [Test]
        public void AnonymousGiftHasNoGifter()
        {
            LiveChatGiftSubscriptionEvent gift = (LiveChatGiftSubscriptionEvent)Translate(EventSubTranslator.ChatNotification, @"{
                ""chatter_user_id"": ""274598607"", ""chatter_user_login"": ""ananonymousgifter"", ""chatter_user_name"": ""AnAnonymousGifter"",
                ""chatter_is_anonymous"": true, ""notice_type"": ""sub_gift"",
                ""sub_gift"": { ""recipient_user_id"": ""7"", ""recipient_user_login"": ""lucky"", ""recipient_user_name"": ""Lucky"", ""sub_tier"": ""1000"", ""community_gift_id"": null }
            }");
            Assert.IsTrue(gift.GifterIsAnonymous);
            Assert.IsNull(gift.GifterUserId);
            Assert.IsNull(gift.CommunityGiftId);
        }

        [Test]
        public void RaidHasTheRaiderAndViewerCount()
        {
            LiveChatRaidEvent raid = (LiveChatRaidEvent)Translate(EventSubTranslator.ChatNotification, @"{
                ""chatter_user_id"": ""5"", ""chatter_user_login"": ""raider"", ""chatter_user_name"": ""Raider"",
                ""notice_type"": ""raid"", ""raid"": { ""user_id"": ""5"", ""user_login"": ""raider"", ""user_name"": ""Raider"",
                    ""viewer_count"": 120, ""profile_image_url"": ""https://example.com/a.png"" }
            }");
            Assert.AreEqual("5", raid.FromUserId);
            Assert.AreEqual("Raider", raid.FromDisplayName);
            Assert.AreEqual(120, raid.Viewers);
            Assert.AreEqual("https://example.com/a.png", raid.ProfileImageUrl);
        }

        [TestCase("shared_chat_sub")]
        [TestCase("announcement")]
        [TestCase("unknown")]
        public void OtherNoticesAreIgnored(string noticeType)
        {
            Assert.IsNull(Translate(EventSubTranslator.ChatNotification,
                $@"{{ ""chatter_user_id"": ""5"", ""notice_type"": ""{noticeType}"" }}"));
        }

        [Test]
        public void FollowAndRedemption()
        {
            LiveChatFollowEvent follow = (LiveChatFollowEvent)Translate(EventSubTranslator.Follow,
                @"{ ""user_id"": ""8"", ""user_login"": ""fan"", ""user_name"": ""Fan"", ""followed_at"": ""2026-10-05T00:00:00Z"" }");
            Assert.AreEqual("8", follow.UserId);
            Assert.AreEqual("Fan", follow.DisplayName);

            LiveChatChannelPointsRedemption redemption = (LiveChatChannelPointsRedemption)Translate(EventSubTranslator.Redemption, @"{
                ""id"": ""r1"", ""user_id"": ""8"", ""user_login"": ""fan"", ""user_name"": ""Fan"", ""user_input"": ""5"", ""status"": ""unfulfilled"",
                ""reward"": { ""id"": ""rw"", ""title"": ""Deep Dig: drop TNT"", ""cost"": 1500, ""prompt"": ""Lane"" }
            }");
            Assert.AreEqual("r1", redemption.RedemptionId);
            Assert.AreEqual("rw", redemption.RewardId);
            Assert.AreEqual("Deep Dig: drop TNT", redemption.RewardTitle);
            Assert.AreEqual(1500, redemption.Cost);
            Assert.AreEqual("5", redemption.UserInput);
        }

        [Test]
        public void UnknownSubscriptionTypesAreIgnored()
        {
            Assert.IsNull(Translate("channel.update", @"{ ""title"": ""x"" }"));
        }

        [Test]
        public void TimestampsWithNanosecondsParse()
        {
            System.DateTime? parsed = EventSubSocket.ParseTimestamp(new JValue("2023-07-19T14:56:51.634234626Z"));
            Assert.IsTrue(parsed.HasValue);
            Assert.AreEqual(new System.DateTime(2023, 7, 19, 14, 56, 51, System.DateTimeKind.Utc).AddTicks(6342346), parsed.Value);
            Assert.AreEqual(System.DateTimeKind.Utc, parsed.Value.Kind);
        }
    }
}
