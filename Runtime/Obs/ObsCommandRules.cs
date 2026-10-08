using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveChat.Obs
{
    /// <summary>Turns a chat argument ("game", "starting", or part of a name) into an OBS scene name.</summary>
    public static class ObsSceneResolver
    {
        /// <summary>
        /// "starting" / "game" use the configured scenes; otherwise an exact name, then a unique
        /// case-insensitive match, then a unique case-insensitive partial match.
        /// </summary>
        /// <returns>The scene, or null with <paramref name="error"/> set to a reply for chat.</returns>
        public static string Resolve(string argument, string startingScene, string gameScene, IReadOnlyList<string> scenes, out string error)
        {
            error = null;
            string wanted = argument?.Trim();
            if (string.IsNullOrEmpty(wanted))
            {
                error = "Which scene? " + Shortcuts(startingScene, gameScene) + "or a scene name.";
                return null;
            }

            string lower = wanted.ToLowerInvariant();
            if ((lower == "starting" || lower == "start") && !string.IsNullOrWhiteSpace(startingScene))
                wanted = startingScene.Trim();
            else if (lower == "game" && !string.IsNullOrWhiteSpace(gameScene))
                wanted = gameScene.Trim();

            scenes ??= Array.Empty<string>();
            if (scenes.Contains(wanted))
                return wanted;

            List<string> matches = scenes.Where(s => string.Equals(s, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                matches = scenes.Where(s => s != null && s.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (matches.Count == 1)
                return matches[0];
            if (matches.Count > 1)
            {
                error = $"\"{wanted}\" matches {matches.Count} scenes: {string.Join(", ", matches.Take(6))}";
                return null;
            }
            error = $"No OBS scene called \"{wanted}\". Scenes: {string.Join(", ", scenes.Take(10))}";
            return null;
        }

        private static string Shortcuts(string startingScene, string gameScene)
        {
            string text = "";
            if (!string.IsNullOrWhiteSpace(startingScene))
                text += "starting, ";
            if (!string.IsNullOrWhiteSpace(gameScene))
                text += "game, ";
            return text;
        }
    }

    /// <summary>
    /// The confirmation for ending the stream: a bare request only arms it; a second request within
    /// the window, or an explicit "confirm", fires it. "cancel" disarms.
    /// </summary>
    public sealed class ObsStopConfirmation
    {
        public enum Outcome
        {
            /// <summary>Asked for confirmation. Nothing happens to the stream.</summary>
            Armed,
            /// <summary>Confirmed: end the stream now.</summary>
            Confirmed,
            /// <summary>Disarmed by "cancel".</summary>
            Cancelled
        }

        public const double DefaultWindowSeconds = 15;

        private readonly double _windowSeconds;
        private double _armedUntil = double.NegativeInfinity;

        public ObsStopConfirmation(double windowSeconds = DefaultWindowSeconds)
        {
            _windowSeconds = windowSeconds;
        }

        public double WindowSeconds => _windowSeconds;

        public bool IsArmed(double now) => now <= _armedUntil;

        public Outcome Check(string argument, double now)
        {
            string arg = argument?.Trim().ToLowerInvariant() ?? "";
            if (arg == "cancel" || arg == "no")
            {
                Disarm();
                return Outcome.Cancelled;
            }
            if (arg == "confirm" || IsArmed(now))
            {
                Disarm();
                return Outcome.Confirmed;
            }
            _armedUntil = now + _windowSeconds;
            return Outcome.Armed;
        }

        public void Disarm() => _armedUntil = double.NegativeInfinity;
    }
}
