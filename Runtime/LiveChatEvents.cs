using System;

namespace LiveChat
{
    public enum LiveChatSubscriptionPlan
    {
        Unknown,
        Prime,
        Tier1,
        Tier2,
        Tier3
    }

    /// <summary>A viewer subscribed or shared a resub.</summary>
    [Serializable]
    public class LiveChatSubscriptionEvent
    {
        public string UserId;
        public string Username;
        public string DisplayName;
        public LiveChatSubscriptionPlan Plan;
        /// <summary>Months subscribed in total, including this one. 1 for a new sub.</summary>
        public int CumulativeMonths;
        /// <summary>Months bought at once (multi-month subs).</summary>
        public int DurationMonths;
        public bool IsResub;
        /// <summary>The message the viewer shared with a resub, if any.</summary>
        public string Message;
    }

    /// <summary>
    /// One gifted sub. A community gift of N subs raises one <see cref="LiveChatCommunityGiftEvent"/>
    /// followed by N of these, each with <see cref="CommunityGiftId"/> set.
    /// </summary>
    [Serializable]
    public class LiveChatGiftSubscriptionEvent
    {
        public string GifterUserId;
        public string GifterUsername;
        public string GifterDisplayName;
        public bool GifterIsAnonymous;
        public string RecipientUserId;
        public string RecipientUsername;
        public string RecipientDisplayName;
        public LiveChatSubscriptionPlan Plan;
        public int DurationMonths;
        /// <summary>Set when this gift is part of a community gift, otherwise null.</summary>
        public string CommunityGiftId;
    }

    /// <summary>A viewer gifted several subs to the community at once.</summary>
    [Serializable]
    public class LiveChatCommunityGiftEvent
    {
        public string Id;
        public string GifterUserId;
        public string GifterUsername;
        public string GifterDisplayName;
        public bool GifterIsAnonymous;
        public int Count;
        public LiveChatSubscriptionPlan Plan;
    }

    [Serializable]
    public class LiveChatRaidEvent
    {
        public string FromUserId;
        public string FromUsername;
        public string FromDisplayName;
        public int Viewers;
        public string ProfileImageUrl;
    }

    [Serializable]
    public class LiveChatFollowEvent
    {
        public string UserId;
        public string Username;
        public string DisplayName;
    }

    /// <summary>
    /// A channel point reward redemption. Finish it with
    /// <see cref="LiveChatClientBase.CompleteRedemption"/>: fulfil it, or cancel it to refund the points.
    /// </summary>
    [Serializable]
    public class LiveChatChannelPointsRedemption
    {
        public string RedemptionId;
        public string RewardId;
        public string RewardTitle;
        public int Cost;
        public string UserId;
        public string Username;
        public string DisplayName;
        public string UserInput;
    }
}
