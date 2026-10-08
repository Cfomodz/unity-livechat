# Changelog

## 0.3.0

- **Predictions.** Turn on `Predictions` and the broadcaster's login asks for
  `channel:manage:predictions`. `CreatePredictionAsync`, `GetPredictionsAsync`,
  `ResolvePredictionAsync`, `CancelPredictionAsync`, `LockPredictionAsync`, and
  `CancelOpenPredictionsAsync` to clear predictions an earlier run left open.
- **Polls.** Turn on `Polls` and the broadcaster's login asks for `channel:manage:polls`.
  `CreatePollAsync`, `GetPollsAsync` and `EndPollAsync`.
- **Users and streams.** `GetUserByLoginAsync`, `GetUserByIdAsync` and `GetStreamAsync` on the
  client, through the bot's login.
- New Helix calls: `GetUsersAsync`, `GetUserByIdAsync`, `GetStreamAsync`, `CreatePredictionAsync`,
  `GetPredictionsAsync`, `EndPredictionAsync`, `CreatePollAsync`, `GetPollsAsync`, `EndPollAsync`,
  and `UserFrom`, `StreamFrom`, `PredictionFrom` and `PollFrom` to read Twitch's JSON.
- A saved broadcaster login asks to log in again once when `Predictions` or `Polls` is turned on.

## 0.2.4

- `TwitchLiveChatClient.IsInChat(userId)`: whether a viewer is in the channel's chat, from
  Twitch's chatter list, refreshed every minute. Null when the list isn't available (the bot
  isn't a moderator, or it hasn't loaded yet). The bot now asks for `moderator:read:chatters`,
  so a saved bot login asks to log in again once.
- `TwitchHelix.GetChatterIdsAsync`.

## 0.2.3

- `EnsureRewardsAsync` now keeps the app's rewards in line with the list it's given: rewards it
  already created are updated to the spec (cost, prompt, user input) as they're reopened, so
  changing a price in code changes it on Twitch. With `removeUnlisted: true`, the app's rewards
  that are no longer listed are hidden, their pending redemptions refunded, and deleted.
- New Helix calls: `UpdateCustomRewardAsync` with a full spec, and `DeleteCustomRewardAsync`.

## 0.2.2

- **Rewards follow the app's lifecycle.** `EnsureRewardsAsync` now hides each existing reward,
  refunds redemptions made while the app wasn't running, and shows it again; on quit
  (`HideRewardsOnQuit`, on by default) it hides them, so viewers can't redeem rewards nobody
  will handle. Hiding first means a new redemption can't be refunded by mistake.
- `GetOtherRewardsAsync` lists the channel's rewards this app didn't create. Twitch only lets the
  creating app change or delete a reward, so these need removing by hand.
- **Stream info.** Set `StreamInfo` (title, category by name, tags) and it's applied once the
  broadcaster logs in, or call `SetStreamInfoAsync` later. `StreamInfoStatus` reports the result.
  The broadcaster logs in when `ChannelPoints` is on or `StreamInfo` has anything set, and is asked
  for `channel:manage:broadcast` only when stream info is used. A saved broadcaster login without
  that scope asks to log in again once.
- `BroadcasterScopes` is split into `ChannelPointsScopes` and `StreamInfoScopes`.
- A Twitch app registered as a Confidential client now fails with an explanation. Its tokens
  can't be refreshed without a secret, so they expired within hours, and the client used to
  treat that as an ordinary expiry: it deleted the login and asked for a new one.
- `TwitchCustomReward` has `IsEnabled` and `IsPaused`. New Helix calls: `UpdateCustomRewardAsync`,
  `GetUnfulfilledRedemptionIdsAsync`, `FindCategoryIdAsync`, `ModifyChannelInformationAsync`.

## 0.2.1

- Fix: real Twitch messages threw `InvalidOperationException: Cannot access child value on JValue`.
  Twitch sends unused objects as JSON null (`"cheer": null`), and indexing into them threw.
  Every nested read now goes through a null-safe helper, with a test using Twitch-shaped payloads.

## 0.2.0

Twitch now goes through EventSub and the Helix API instead of IRC and PubSub. Twitch shut
PubSub down on 2025-04-14 and recommends EventSub and Helix over IRC for chat bots.

**Breaking changes**

- `TwitchLiveChatClient` is rewritten. Set `ClientId` (a Twitch app registered as a **Public**
  client), optionally `BotLogin`, `ChannelPoints` and `TokenFilePath`, then call
  `Connect(new LiveChatConnectConfig { ChannelName = "channel" })`. There's no token to paste:
  the bot (and, for channel points, the broadcaster) log in with Twitch's device code flow, and
  tokens are saved, refreshed and validated hourly.
- `TwitchPubSubClient` and the bundled TwitchLib DLLs are removed.
- `LiveChatConnectConfig` only has `ChannelName`. `ChannelId` and `BotAccessToken` are gone.
- `LiveChatMessage.RawIrcMessage` and `IsFirstMessage` are gone (EventSub has no equivalent).
- `LiveChatBitsEvent` and `LiveChatRewardRedemption` are gone: bits arrive on
  `LiveChatMessage.Bits`, redemptions as `LiveChatChannelPointsRedemption`.

**New**

- Stream events on every client: `Subscribed`, `SubscriptionGifted`, `CommunityGiftStarted`,
  `Raided`, `Followed` and `ChannelPointsRedeemed`, plus `CompleteRedemption` to fulfil or refund.
- `TwitchLiveChatClient` uses a separate bot account. It reads chat, bits, subs, gifts and raids
  with only the bot's login; follows need the bot to be a moderator; channel points need the
  broadcaster's login. `EnsureRewardsAsync` creates the app's own channel point rewards, since
  only the app that created a reward can fulfil or refund its redemptions.
- `State`, `StatusText` and `PendingAuthorization` for an on-screen status and login prompt.
- Outgoing chat goes through `ChatSendQueue`: it stays under Twitch's rate limits (higher once
  the bot is seen with a moderator or VIP badge), drops repeats within 30 s and trims to 500 characters.
- EventSub sessions handle keepalive timeouts, `session_reconnect`, duplicate and stale messages,
  revocations, and reconnect with backoff.
- `LocalDebugLiveChatClient` can simulate subs, gifts, community gifts, raids, follows and
  redemptions, and a key (backquote by default) shows or hides its chat box.
- EditMode tests for the EventSub translator, the send queue and the token store.

## 0.1.2

- Fix: pressing Enter in `LocalDebugLiveChatClient`'s chat box now sends the message. `GUI.TextField`
  used up the Enter key before the client checked for it.
- Escape releases the chat box's keyboard focus, so a game's keyboard shortcuts work; click the box to type again.

## 0.1.1

- `LocalDebugLiveChatClient` simulates bits: `cheerN` tokens in a message (`!buy steel cheer100`)
  set `LiveChatMessage.Bits` the way Twitch does.

## 0.1.0

- First release as a standalone UPM package, extracted from
  `Cfomodz/Interactive-Livestream-Chaos-League` (`Assets/Scripts/LiveChat/`) with history.
- Live chat clients: `TwitchLiveChatClient`, `TwitchPubSubClient`, `YouTubeLiveChatClient` (stub).
- Chat command router: `ChatCommandRouter`, `ChatCommandHandler`, `ChatCommandParser`,
  built-in `!help`, `!ping` and `!echo` handlers.
- `LocalDebugLiveChatClient`: on-screen fake chat for testing without going live
  (merged from the Dig Dug and Pickaxe mini-games' copies).
- Ships the TwitchLib DLLs under `Runtime/Plugins/TwitchLib/` and depends on
  `com.unity.nuget.newtonsoft-json`, which TwitchLib needs.
- EditMode tests for `ChatCommandParser`.
