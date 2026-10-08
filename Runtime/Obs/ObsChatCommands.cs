using System;
using System.Threading.Tasks;

namespace LiveChat.Obs
{
    /// <summary>
    /// The broadcaster's OBS chat commands, without depending on how a game routes commands. With
    /// prefix "dd": !ddscene &lt;starting|game|name&gt;, !ddgolive, !ddend (asks to confirm), !ddobs.
    /// They're the only way a game should start or stop the stream; only let the broadcaster use them.
    /// </summary>
    public sealed class ObsChatCommands
    {
        private readonly ObsController _obs;
        private readonly ObsStopConfirmation _confirmation = new ObsStopConfirmation();

        public ObsChatCommands(ObsController obs, string prefix)
        {
            _obs = obs;
            prefix = (prefix ?? string.Empty).Trim().TrimStart('!').ToLowerInvariant();
            Scene = prefix + "scene";
            GoLive = prefix + "golive";
            End = prefix + "end";
            Status = prefix + "obs";
        }

        /// <summary>Command names, without the "!".</summary>
        public string Scene { get; }
        public string GoLive { get; }
        public string End { get; }
        public string Status { get; }

        /// <summary>One line listing the commands, for help text.</summary>
        public string Usage => $"!{Scene} <starting|game|scene name>, !{GoLive}, !{End}, !{Status}";

        public bool Handles(string command) => Matches(command, Scene) || Matches(command, GoLive) || Matches(command, End) || Matches(command, Status);

        /// <summary>
        /// Runs <paramref name="command"/> (without the "!") and returns the reply, or null if it isn't
        /// an OBS command. <paramref name="now"/> is a clock in seconds, for !end's confirmation window.
        /// </summary>
        public async Task<string> Run(string command, string args, double now)
        {
            if (_obs == null)
                return null;
            args = (args ?? string.Empty).Trim();

            if (Matches(command, Scene))
                return await _obs.SwitchScene(args);
            if (Matches(command, GoLive))
                return await _obs.GoLive();
            if (Matches(command, Status))
                return await _obs.DescribeStatus();
            if (!Matches(command, End))
                return null;

            if (!_obs.IsConnected)
            {
                _confirmation.Disarm();
                return await _obs.DescribeStatus();
            }
            if (_obs.StreamActive == false && !_confirmation.IsArmed(now))
                return "OBS isn't streaming.";

            switch (_confirmation.Check(args, now))
            {
                case ObsStopConfirmation.Outcome.Cancelled:
                    return "OK, the stream keeps going.";
                case ObsStopConfirmation.Outcome.Armed:
                    return $"This ENDS the stream. Type !{End} again within {_confirmation.WindowSeconds:0} s "
                        + $"(or !{End} confirm) to end it, or !{End} cancel.";
                default:
                    return await _obs.EndStream();
            }
        }

        private static bool Matches(string command, string name) =>
            string.Equals((command ?? string.Empty).Trim().TrimStart('!'), name, StringComparison.OrdinalIgnoreCase);
    }
}
