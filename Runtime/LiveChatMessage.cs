using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiveChat
{
    [Serializable]
    public class LiveChatMessage
    {
        public string MessageId;
        public string UserId;
        public string Username;
        public string DisplayName;
        public string Channel;
        public string RawMessage;
        public Color UsernameColor;
        public bool IsSubscriber;
        /// <summary>Bits cheered with this message (0 if it isn't a cheer).</summary>
        public int Bits;
        public bool IsModerator;
        public bool IsBroadcaster;
        public bool IsVip;
        /// <summary>The message was sent by the account the client is logged in as.</summary>
        public bool IsMe;
        public List<LiveChatEmote> Emotes;
    }
}
