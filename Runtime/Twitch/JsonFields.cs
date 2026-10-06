using Newtonsoft.Json.Linq;

namespace LiveChat.Twitch
{
    internal static class JsonFields
    {
        /// <summary>
        /// The child <paramref name="key"/> of an object, or null. Twitch sends absent objects as JSON
        /// null (<c>"cheer": null</c>), and indexing into that throws, which C#'s <c>?.</c> doesn't catch.
        /// </summary>
        public static JToken Field(this JToken token, string key) => (token as JObject)?[key];
    }
}
