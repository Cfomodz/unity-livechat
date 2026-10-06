using System;
using UnityEngine;

namespace LiveChat
{
    public abstract class LiveChatClientBase : MonoBehaviour
    {
        public event Action Connected;
        public event Action<string> JoinedChannel;
        public event Action Disconnected;
        public event Action<Exception> Error;
        public event Action<LiveChatMessage> MessageReceived;

        public event Action<LiveChatSubscriptionEvent> Subscribed;
        public event Action<LiveChatGiftSubscriptionEvent> SubscriptionGifted;
        public event Action<LiveChatCommunityGiftEvent> CommunityGiftStarted;
        public event Action<LiveChatRaidEvent> Raided;
        public event Action<LiveChatFollowEvent> Followed;
        public event Action<LiveChatChannelPointsRedemption> ChannelPointsRedeemed;

        public string DefaultChannel { get; protected set; }
        public bool IsConnected { get; protected set; }

        public abstract void Connect(LiveChatConnectConfig config);
        public abstract void Disconnect();
        public abstract void SendMessage(string channel, string message);
        public abstract void SendReply(string channel, string replyToMessageId, string message);

        /// <summary>
        /// Finishes a channel point redemption: <paramref name="fulfilled"/> marks it done, false
        /// cancels it and refunds the viewer's points. Clients without channel points ignore it.
        /// </summary>
        public virtual void CompleteRedemption(LiveChatChannelPointsRedemption redemption, bool fulfilled)
        {
        }

        /// <summary>Sends to <see cref="DefaultChannel"/>. Hides Unity's Component.SendMessage(methodName), which this class doesn't use.</summary>
        public new void SendMessage(string message)
        {
            if (string.IsNullOrEmpty(DefaultChannel))
                return;

            SendMessage(DefaultChannel, message);
        }

        public void SendReply(string replyToMessageId, string message)
        {
            if (string.IsNullOrEmpty(DefaultChannel))
                return;

            SendReply(DefaultChannel, replyToMessageId, message);
        }

        protected void RaiseConnected() => Connected?.Invoke();
        protected void RaiseJoinedChannel(string channel) => JoinedChannel?.Invoke(channel);
        protected void RaiseDisconnected() => Disconnected?.Invoke();
        protected void RaiseError(Exception exception) => Error?.Invoke(exception);
        protected void RaiseMessageReceived(LiveChatMessage message) => MessageReceived?.Invoke(message);
        protected void RaiseSubscribed(LiveChatSubscriptionEvent e) => Subscribed?.Invoke(e);
        protected void RaiseSubscriptionGifted(LiveChatGiftSubscriptionEvent e) => SubscriptionGifted?.Invoke(e);
        protected void RaiseCommunityGiftStarted(LiveChatCommunityGiftEvent e) => CommunityGiftStarted?.Invoke(e);
        protected void RaiseRaided(LiveChatRaidEvent e) => Raided?.Invoke(e);
        protected void RaiseFollowed(LiveChatFollowEvent e) => Followed?.Invoke(e);
        protected void RaiseChannelPointsRedeemed(LiveChatChannelPointsRedemption e) => ChannelPointsRedeemed?.Invoke(e);
    }
}
