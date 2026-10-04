using LiveChat.Commands;
using NUnit.Framework;

namespace LiveChat.Tests
{
    public class ChatCommandParserTests
    {
        private static readonly string[] Bang = { "!" };

        [Test]
        public void ParsesCommandWithoutArgs()
        {
            Assert.IsTrue(ChatCommandParser.TryParse("!ping", Bang, true, out ChatCommandParseResult result));
            Assert.AreEqual("!", result.Prefix);
            Assert.AreEqual("ping", result.Command);
            Assert.AreEqual(string.Empty, result.ArgsRaw);
            Assert.IsEmpty(result.Args);
        }

        [Test]
        public void ParsesCommandWithArgs()
        {
            Assert.IsTrue(ChatCommandParser.TryParse("  !dig   left  2", Bang, true, out ChatCommandParseResult result));
            Assert.AreEqual("dig", result.Command);
            Assert.AreEqual("left  2", result.ArgsRaw);
            CollectionAssert.AreEqual(new[] { "left", "2" }, result.Args);
            Assert.AreEqual("!dig   left  2", result.RawText);
        }

        [Test]
        public void RejectsMessageWithoutPrefix()
        {
            Assert.IsFalse(ChatCommandParser.TryParse("hello chat", Bang, true, out _));
        }

        [Test]
        public void RejectsPrefixOnly()
        {
            Assert.IsFalse(ChatCommandParser.TryParse("!", Bang, true, out _));
            Assert.IsFalse(ChatCommandParser.TryParse("!   ", Bang, true, out _));
        }

        [Test]
        public void RejectsEmptyInputAndPrefixes()
        {
            Assert.IsFalse(ChatCommandParser.TryParse(null, Bang, true, out _));
            Assert.IsFalse(ChatCommandParser.TryParse("   ", Bang, true, out _));
            Assert.IsFalse(ChatCommandParser.TryParse("!ping", null, true, out _));
            Assert.IsFalse(ChatCommandParser.TryParse("!ping", new string[0], true, out _));
        }

        [Test]
        public void SupportsMultiplePrefixes()
        {
            string[] prefixes = { "!", "?" };
            Assert.IsTrue(ChatCommandParser.TryParse("?help", prefixes, true, out ChatCommandParseResult result));
            Assert.AreEqual("?", result.Prefix);
            Assert.AreEqual("help", result.Command);
        }

        [Test]
        public void PrefixCaseSensitivityFollowsIgnoreCase()
        {
            string[] prefixes = { "bot:" };
            Assert.IsTrue(ChatCommandParser.TryParse("BOT: ping", prefixes, true, out ChatCommandParseResult result));
            Assert.AreEqual("ping", result.Command);
            Assert.IsFalse(ChatCommandParser.TryParse("BOT: ping", prefixes, false, out _));
        }

        [Test]
        public void QuotedArgumentsKeepSpaces()
        {
            CollectionAssert.AreEqual(
                new[] { "hello world", "single quoted", "plain" },
                ChatCommandParser.ParseArguments("\"hello world\" 'single quoted' plain"));
        }

        [Test]
        public void EscapedQuotesInsideQuotes()
        {
            CollectionAssert.AreEqual(
                new[] { "say \"hi\"" },
                ChatCommandParser.ParseArguments("\"say \\\"hi\\\"\""));
        }

        [Test]
        public void EscapedSpaceOutsideQuotes()
        {
            CollectionAssert.AreEqual(
                new[] { "a b", "c" },
                ChatCommandParser.ParseArguments("a\\ b c"));
        }

        [Test]
        public void EmptyArgumentsReturnEmptyArray()
        {
            Assert.IsEmpty(ChatCommandParser.ParseArguments(null));
            Assert.IsEmpty(ChatCommandParser.ParseArguments("   "));
        }
    }
}
