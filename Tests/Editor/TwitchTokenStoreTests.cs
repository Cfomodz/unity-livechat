using System;
using System.IO;
using LiveChat.Twitch;
using NUnit.Framework;

namespace LiveChat.Tests
{
    public class TwitchTokenStoreTests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "LiveChatTokens_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }

        [Test]
        public void KeepsEachRoleSeparately()
        {
            TwitchTokenStore store = new TwitchTokenStore(Path.Combine(_directory, "nested", "tokens.json"));
            Assert.IsNull(store.Load("bot"));

            store.Save("bot", new TwitchToken { AccessToken = "a1", RefreshToken = "r1", UserId = "1", Login = "bot", Scopes = new[] { "user:read:chat" } });
            store.Save("broadcaster", new TwitchToken { AccessToken = "a2", RefreshToken = "r2", UserId = "2" });
            store.Save("bot", new TwitchToken { AccessToken = "a3", RefreshToken = "r3", UserId = "1", Scopes = new[] { "user:read:chat" } });

            TwitchTokenStore reopened = new TwitchTokenStore(store.Path);
            Assert.AreEqual("a3", reopened.Load("bot").AccessToken);
            Assert.AreEqual("r3", reopened.Load("bot").RefreshToken);
            Assert.AreEqual("a2", reopened.Load("broadcaster").AccessToken);
            Assert.IsFalse(File.Exists(store.Path + ".tmp"));

            reopened.Delete("bot");
            Assert.IsNull(new TwitchTokenStore(store.Path).Load("bot"));
            Assert.IsNotNull(new TwitchTokenStore(store.Path).Load("broadcaster"));
        }

        [Test]
        public void ACorruptFileMeansNoTokens()
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, "tokens.json");
            File.WriteAllText(path, "{ not json");
            Assert.IsNull(new TwitchTokenStore(path).Load("bot"));
        }

        [Test]
        public void StreamInfoIsEmptyUntilSomethingIsSet()
        {
            Assert.IsTrue(new TwitchStreamInfo().IsEmpty);
            Assert.IsTrue(new TwitchStreamInfo { Title = "  ", Tags = new string[0] }.IsEmpty);
            Assert.IsFalse(new TwitchStreamInfo { Title = "Deep Dig" }.IsEmpty);
            Assert.IsFalse(new TwitchStreamInfo { Category = "Games + Demos" }.IsEmpty);
            Assert.IsFalse(new TwitchStreamInfo { Tags = new[] { "English" } }.IsEmpty);
        }

        [Test]
        public void ChecksScopes()
        {
            TwitchToken token = new TwitchToken { Scopes = new[] { "user:read:chat", "user:write:chat" } };
            Assert.IsTrue(token.HasScopes(new[] { "user:write:chat" }));
            Assert.IsFalse(token.HasScopes(new[] { "user:write:chat", "moderator:read:followers" }));
        }
    }
}
