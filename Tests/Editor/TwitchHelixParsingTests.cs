using System;
using LiveChat.Twitch;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace LiveChat.Tests
{
    /// <summary>Payloads follow the examples in Twitch's Helix reference.</summary>
    public class TwitchHelixParsingTests
    {
        [Test]
        public void PredictionCarriesOutcomesAndStatus()
        {
            TwitchPrediction prediction = TwitchHelix.PredictionFrom(JObject.Parse(@"{
                ""id"": ""bc637af0-7766-4525-9308-4112f4cbf178"",
                ""broadcaster_id"": ""141981764"",
                ""title"": ""Will there be any leaks today?"",
                ""winning_outcome_id"": null,
                ""outcomes"": [
                    { ""id"": ""73085848-a94d-4040-9d21-2cb7a89374b7"", ""title"": ""Yes"", ""users"": 2, ""channel_points"": 1500,
                      ""top_predictors"": null, ""color"": ""BLUE"" },
                    { ""id"": ""906b70ba-1f12-47ea-9e95-e5f93d20e9cc"", ""title"": ""No"", ""users"": 0, ""channel_points"": 0,
                      ""top_predictors"": null, ""color"": ""PINK"" }
                ],
                ""prediction_window"": 600,
                ""status"": ""ACTIVE"",
                ""created_at"": ""2021-04-28T16:03:06.320848689Z"",
                ""ended_at"": null,
                ""locked_at"": null
            }"));

            Assert.AreEqual("bc637af0-7766-4525-9308-4112f4cbf178", prediction.Id);
            Assert.AreEqual("Will there be any leaks today?", prediction.Title);
            Assert.AreEqual(TwitchPredictionStatus.Active, prediction.Status);
            Assert.IsNull(prediction.WinningOutcomeId);
            Assert.AreEqual(2, prediction.Outcomes.Count);
            Assert.AreEqual("73085848-a94d-4040-9d21-2cb7a89374b7", prediction.Outcomes[0].Id);
            Assert.AreEqual("Yes", prediction.Outcomes[0].Title);
            Assert.AreEqual(2, prediction.Outcomes[0].Users);
            Assert.AreEqual(1500, prediction.Outcomes[0].ChannelPoints);
        }

        [TestCase("RESOLVED", TwitchPredictionStatus.Resolved)]
        [TestCase("CANCELED", TwitchPredictionStatus.Canceled)]
        [TestCase("LOCKED", TwitchPredictionStatus.Locked)]
        public void PredictionStatusesParse(string status, TwitchPredictionStatus expected)
        {
            TwitchPrediction prediction = TwitchHelix.PredictionFrom(JObject.Parse(
                @"{ ""id"": ""1"", ""title"": ""t"", ""winning_outcome_id"": ""o1"", ""outcomes"": [], ""status"": """ + status + @""" }"));

            Assert.AreEqual(expected, prediction.Status);
            Assert.AreEqual("o1", prediction.WinningOutcomeId);
        }

        [Test]
        public void PredictionWithoutOutcomesIsEmptyNotNull()
        {
            TwitchPrediction prediction = TwitchHelix.PredictionFrom(JObject.Parse(@"{ ""id"": ""1"", ""outcomes"": null, ""status"": ""ACTIVE"" }"));

            Assert.IsNotNull(prediction.Outcomes);
            Assert.AreEqual(0, prediction.Outcomes.Count);
        }

        [Test]
        public void PollCarriesChoicesAndVotes()
        {
            TwitchPoll poll = TwitchHelix.PollFrom(JObject.Parse(@"{
                ""id"": ""ed961efd-8a3f-4cf5-a9d0-e616c590cd2a"",
                ""broadcaster_id"": ""55696719"",
                ""title"": ""Heads or Tails?"",
                ""choices"": [
                    { ""id"": ""4c123012-1351-4f33-84b7-43856e7a0f47"", ""title"": ""Heads"", ""votes"": 7, ""channel_points_votes"": 2, ""bits_votes"": 0 },
                    { ""id"": ""279087e3-54a7-467e-bcd0-c1393fcea4f0"", ""title"": ""Tails"", ""votes"": 3, ""channel_points_votes"": 0, ""bits_votes"": 0 }
                ],
                ""bits_voting_enabled"": false,
                ""channel_points_voting_enabled"": false,
                ""status"": ""COMPLETED"",
                ""duration"": 1800,
                ""started_at"": ""2021-03-19T06:08:33.871278372Z""
            }"));

            Assert.AreEqual("ed961efd-8a3f-4cf5-a9d0-e616c590cd2a", poll.Id);
            Assert.AreEqual("Heads or Tails?", poll.Title);
            Assert.AreEqual(TwitchPollStatus.Completed, poll.Status);
            Assert.AreEqual(2, poll.Choices.Count);
            Assert.AreEqual("Heads", poll.Choices[0].Title);
            Assert.AreEqual(7, poll.Choices[0].Votes);
            Assert.AreEqual(2, poll.Choices[0].ChannelPointsVotes);
            Assert.AreEqual(3, poll.Choices[1].Votes);
        }

        [Test]
        public void UnknownPollStatusIsInvalid()
        {
            TwitchPoll poll = TwitchHelix.PollFrom(JObject.Parse(@"{ ""id"": ""1"", ""choices"": [], ""status"": ""SOMETHING_NEW"" }"));

            Assert.AreEqual(TwitchPollStatus.Invalid, poll.Status);
        }

        [Test]
        public void StreamCarriesViewerCountAndStartTime()
        {
            TwitchStream stream = TwitchHelix.StreamFrom(JObject.Parse(@"{
                ""id"": ""123456789"",
                ""user_id"": ""98765"",
                ""user_login"": ""sandysanderman"",
                ""user_name"": ""SandySanderman"",
                ""game_id"": ""494131"",
                ""game_name"": ""Little Nightmares"",
                ""type"": ""live"",
                ""title"": ""hablamos y le damos a Little Nightmares 1"",
                ""tags"": [""Español""],
                ""viewer_count"": 78365,
                ""started_at"": ""2021-03-10T15:04:21Z"",
                ""language"": ""es"",
                ""is_mature"": false
            }"));

            Assert.AreEqual("123456789", stream.Id);
            Assert.AreEqual("98765", stream.UserId);
            Assert.AreEqual("Little Nightmares", stream.CategoryName);
            Assert.AreEqual(78365, stream.ViewerCount);
            Assert.AreEqual(new DateTime(2021, 3, 10, 15, 4, 21, DateTimeKind.Utc), stream.StartedAt.ToUniversalTime());
        }

        [Test]
        public void UserCarriesProfileImage()
        {
            TwitchUser user = TwitchHelix.UserFrom(JObject.Parse(@"{
                ""id"": ""141981764"", ""login"": ""twitchdev"", ""display_name"": ""TwitchDev"",
                ""profile_image_url"": ""https://static-cdn.jtvnw.net/jtv_user_pictures/8a6381c7-d0c0-4576-b179-38bd5ce1d6af-profile_image-300x300.png""
            }"));

            Assert.AreEqual("141981764", user.Id);
            Assert.AreEqual("twitchdev", user.Login);
            Assert.AreEqual("TwitchDev", user.DisplayName);
            StringAssert.EndsWith("profile_image-300x300.png", user.ProfileImageUrl);
        }

        [Test]
        public void MissingObjectsParseAsNull()
        {
            Assert.IsNull(TwitchHelix.PredictionFrom(null));
            Assert.IsNull(TwitchHelix.PollFrom(null));
            Assert.IsNull(TwitchHelix.StreamFrom(null));
            Assert.IsNull(TwitchHelix.UserFrom(null));
        }
    }
}
