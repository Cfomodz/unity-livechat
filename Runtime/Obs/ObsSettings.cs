using System;

namespace LiveChat.Obs
{
    /// <summary>
    /// OBS control settings: OBS control through obs-websocket v5 (built into OBS 28+).
    /// Missing, or without "enabled": true, means the game never connects to OBS.
    /// </summary>
    [Serializable]
    public class ObsSettings
    {
        /// <summary>Connect to OBS at all.</summary>
        public bool enabled;
        /// <summary>The WebSocket server's URL. Empty means ws://127.0.0.1:4455, OBS's default.</summary>
        public string url;
        /// <summary>The WebSocket server's password from OBS. Secret: never log it or show it.</summary>
        public string password;
        /// <summary>The scene the "starting" shortcut switches to (e.g. a "Starting soon" scene). Optional.</summary>
        public string startingScene;
        /// <summary>The scene with the game capture (the "game" shortcut). Going live switches to it first. Optional.</summary>
        public string gameScene;

        public string Url => string.IsNullOrWhiteSpace(url) ? ObsProtocol.DefaultUrl : url.Trim();

        /// <summary>Never includes the password.</summary>
        public override string ToString() => $"OBS at {Url} (enabled: {enabled})";
    }
}
