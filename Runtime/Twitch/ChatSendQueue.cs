using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveChat.Twitch
{
    /// <summary>
    /// Outgoing chat throttle. Twitch ignores a bot for an hour if it sends more than 20 messages
    /// per 30 s (100 if it's a moderator or VIP), and at most one per second when it isn't. This
    /// queue stays under those limits with some headroom, drops a message repeated within
    /// <see cref="DuplicateWindowSeconds"/>, and drops the oldest when too many pile up.
    /// Time is passed in, in seconds, so it can be tested.
    /// </summary>
    public class ChatSendQueue
    {
        public const int MaxLength = 500;
        public const double WindowSeconds = 30;

        public struct Outgoing
        {
            public string Text;
            public string ReplyToMessageId;
        }

        private readonly Queue<Outgoing> _queue = new Queue<Outgoing>();
        private readonly Queue<double> _sentAt = new Queue<double>();
        private readonly Dictionary<string, double> _lastQueued = new Dictionary<string, double>(StringComparer.Ordinal);
        private double _lastSentAt = double.NegativeInfinity;

        /// <summary>Moderator/VIP/broadcaster limits apply.</summary>
        public bool Privileged { get; set; }
        public double DuplicateWindowSeconds { get; set; } = 30;
        public int MaxQueued { get; set; } = 20;
        public int Count => _queue.Count;

        private int MaxPerWindow => Privileged ? 90 : 18;
        private double MinInterval => Privileged ? 0.1 : 1.05;

        /// <summary>Queues a message. Returns false if it was empty or a recent duplicate.</summary>
        public bool Enqueue(string text, string replyToMessageId, double now)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            text = text.Trim();
            if (text.Length > MaxLength)
                text = text.Substring(0, MaxLength - 1) + "…";

            foreach (string stale in _lastQueued.Where(pair => now - pair.Value > DuplicateWindowSeconds).Select(pair => pair.Key).ToList())
                _lastQueued.Remove(stale);
            if (_lastQueued.ContainsKey(text))
                return false;
            _lastQueued[text] = now;

            _queue.Enqueue(new Outgoing { Text = text, ReplyToMessageId = replyToMessageId });
            while (_queue.Count > Math.Max(1, MaxQueued))
                _queue.Dequeue();
            return true;
        }

        /// <summary>The next message, if the rate limits allow sending one now.</summary>
        public bool TryDequeue(double now, out Outgoing message)
        {
            message = default;
            while (_sentAt.Count > 0 && now - _sentAt.Peek() >= WindowSeconds)
                _sentAt.Dequeue();
            if (_queue.Count == 0 || _sentAt.Count >= MaxPerWindow || now - _lastSentAt < MinInterval)
                return false;

            message = _queue.Dequeue();
            _sentAt.Enqueue(now);
            _lastSentAt = now;
            return true;
        }

        public void Clear() => _queue.Clear();
    }
}
