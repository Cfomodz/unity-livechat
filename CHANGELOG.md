# Changelog

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
