using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace LiveChat.Twitch
{
    /// <summary>A Twitch user access token and who it belongs to.</summary>
    [Serializable]
    public class TwitchToken
    {
        public string AccessToken;
        /// <summary>Single-use for public clients: every refresh returns a new one, which must be saved.</summary>
        public string RefreshToken;
        public string[] Scopes = Array.Empty<string>();
        public string UserId;
        public string Login;
        public DateTime ExpiresAtUtc;

        public bool HasScopes(IEnumerable<string> required)
        {
            if (required == null)
                return true;
            HashSet<string> granted = new HashSet<string>(Scopes ?? Array.Empty<string>(), StringComparer.Ordinal);
            return required.All(granted.Contains);
        }
    }

    /// <summary>
    /// Keeps tokens in one JSON file, keyed by role ("bot", "broadcaster"). The file holds secrets:
    /// keep it out of version control.
    /// </summary>
    public class TwitchTokenStore
    {
        private readonly string _path;
        private readonly object _lock = new object();

        public TwitchTokenStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A token file path is required.", nameof(path));
            _path = path;
        }

        public string Path => _path;

        public TwitchToken Load(string role)
        {
            lock (_lock)
                return ReadAll().TryGetValue(role, out TwitchToken token) ? token : null;
        }

        public void Save(string role, TwitchToken token)
        {
            lock (_lock)
            {
                Dictionary<string, TwitchToken> all = ReadAll();
                all[role] = token;
                WriteAll(all);
            }
        }

        public void Delete(string role)
        {
            lock (_lock)
            {
                Dictionary<string, TwitchToken> all = ReadAll();
                if (all.Remove(role))
                    WriteAll(all);
            }
        }

        private Dictionary<string, TwitchToken> ReadAll()
        {
            if (!File.Exists(_path))
                return new Dictionary<string, TwitchToken>();
            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, TwitchToken>>(File.ReadAllText(_path))
                    ?? new Dictionary<string, TwitchToken>();
            }
            catch (JsonException)
            {
                // A corrupt file just means logging in again
                return new Dictionary<string, TwitchToken>();
            }
        }

        private void WriteAll(Dictionary<string, TwitchToken> all)
        {
            string directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Write then swap, so a crash mid-write can't lose the only copy of a single-use refresh token
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(all, Formatting.Indented));
            if (File.Exists(_path))
                File.Replace(temp, _path, null);
            else
                File.Move(temp, _path);
        }
    }
}
