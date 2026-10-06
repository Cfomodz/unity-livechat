# unity-livechat

Live chat clients and a chat command router for Unity (`com.cfomodz.livechat`).

- **Twitch:** chat, bits, subs, gifts, raids, follows and channel points through
  [EventSub](https://dev.twitch.tv/docs/eventsub/) over WebSocket and the Helix API, with a
  separate bot account. No server and no client secret: accounts log in with Twitch's device
  code flow.
- **Local debug client:** an on-screen chat box, plus simulated subs, raids and redemptions, for
  testing without going live.
- **Command router:** parses `!command args` messages and dispatches them to
  `ChatCommandHandler` components, with per-command permissions
  (anyone, subscriber, VIP, moderator, broadcaster).
- A YouTube client stub.

Extracted from [Interactive-Livestream-Chaos-League](https://github.com/Cfomodz/Interactive-Livestream-Chaos-League),
with history. Requires Unity **2022.3** or newer, on platforms with `System.Net.WebSockets`
(desktop; not WebGL).

## Install

Add the package to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.cfomodz.livechat": "https://github.com/Cfomodz/unity-livechat.git#v0.2.0"
  }
}
```

It depends on Unity's `com.unity.nuget.newtonsoft-json`, which Package Manager installs for you.
Upgrading from 0.1.x: see the [changelog](CHANGELOG.md).

## Commands

```csharp
using LiveChat.Commands;

public class JumpCommand : ChatCommandHandler
{
    private void Awake()
    {
        SetDefaults("jump", "Make the player jump.", "!jump [height]",
            ChatCommandPermission.Anyone, "j");
    }

    public override void Execute(ChatCommandContext context)
    {
        float height = context.Args.Length > 0 && float.TryParse(context.Args[0], out var h) ? h : 1f;
        // ... make something jump ...
        context.Reply($"{context.DisplayName} jumped {height}!");
    }
}
```

Put a chat client, your handlers and a `ChatCommandRouter` on one GameObject. The router finds
the client on the same GameObject and every `ChatCommandHandler` under it (or under
`Handler Root`) on `Awake`; call `router.RefreshHandlers()` if you add handlers later.
`ChatCommandContext` gives you the parsed `Command`, `Args` / `ArgsRaw`, the sender
(`Username`, `DisplayName`, `IsSubscriber`, `IsVip`, `IsModerator`, `IsBroadcaster`), the
original `LiveChatMessage` (including `Bits`), and `Reply` / `Say` to answer in chat.

If you don't want commands, subscribe to `client.MessageReceived` yourself.

## Stream events

Every client raises the same events, so a game handles them once and can test them with the
debug client:

| Event | Raised for |
|---|---|
| `MessageReceived` | Chat messages. Cheers have `LiveChatMessage.Bits` set |
| `Subscribed` | New subs and shared resubs |
| `CommunityGiftStarted` | "X is gifting N subs", followed by N `SubscriptionGifted` |
| `SubscriptionGifted` | One gifted sub (with `CommunityGiftId` set if it's part of a community gift) |
| `Raided` | Incoming raids, with the viewer count |
| `Followed` | New followers |
| `ChannelPointsRedeemed` | Channel point redemptions. Call `CompleteRedemption(redemption, fulfilled)` to fulfil it, or cancel it to refund the points |

## Twitch

### One-time setup

1. **Register an app** at [dev.twitch.tv/console](https://dev.twitch.tv/console/apps): any name,
   OAuth redirect URL `http://localhost`, category "Chat Bot", and **Client Type: Public**. Copy
   its Client ID. It isn't a secret, but a public client can only use the device code flow,
   which is what this package uses.
2. **Create a bot account** on Twitch (a normal account, e.g. `mychannel_bot`).
3. **Make it a moderator** of your channel: type `/mod mychannel_bot` in your chat. The bot needs
   that to see follows, and moderators may send 100 messages per 30 s instead of 20.

### Connecting

```csharp
using LiveChat;
using LiveChat.Twitch;

var twitch = gameObject.AddComponent<TwitchLiveChatClient>();
twitch.ClientId = "your-public-client-id";
twitch.BotLogin = "mychannel_bot";      // optional: refuse any other account at the bot login
twitch.ChannelPoints = true;            // optional: the broadcaster logs in too
twitch.StreamInfo = new TwitchStreamInfo // optional: set when the broadcaster logs in
{
    Title = "Digging with chat",
    Category = "Games + Demos",
    Tags = new[] { "English", "Interactive" }
};
twitch.TokenFilePath = "...";           // optional: defaults to persistentDataPath/livechat-tokens.json
twitch.AuthorizationRequired += auth => Debug.Log(auth.Instructions); // or show it on screen
twitch.Connect(new LiveChatConnectConfig { ChannelName = "mychannel" });
```

The first time, `Connect` asks for a login: `PendingAuthorization` (and the
`AuthorizationRequired` event) say which account to log in as and the code to enter at
twitch.tv/activate (`VerificationUri` opens it with the code filled in). Log in **as the bot**
for the bot login. With `ChannelPoints` on, a second prompt asks for the broadcaster. After
that, tokens are saved in `TokenFilePath` and refreshed automatically; a refresh token unused for
30 days expires and you log in again. **Keep the token file out of version control.**

`State` and `StatusText` describe the connection for an on-screen display, and `StatusChanged`
fires when they change. Errors are raised on `Error`.

### What each login allows

| Feature | Needs |
|---|---|
| Chat, bits, subs, gifts, raids | The bot's login (`user:read:chat`, `user:write:chat`) |
| Follows | The bot to be a moderator (`moderator:read:followers`) |
| Who's in chat (`IsInChat`) | The bot to be a moderator (`moderator:read:chatters`) |
| Channel points | The broadcaster's login (`channel:manage:redemptions`) and an affiliate or partner channel |
| Stream title, category and tags | The broadcaster's login (`channel:manage:broadcast`) |

Only the app that created a reward can fulfil, refund, hide or delete it, so let the game create
its rewards with `await twitch.EnsureRewardsAsync(specs)` once `ChannelPointsConnected` fires. It
creates any that are missing (matched by title) and returns their IDs. Rewards it already made
are hidden, redemptions viewers made while the app wasn't running are refunded, and the rewards
are updated to the spec (cost, prompt, input) and shown again, so a price changed in code
changes on Twitch. Pass `removeUnlisted: true` to also delete the app's rewards that are no longer
in the list (pending redemptions are refunded first). When the app quits it hides them (`HideRewardsOnQuit`), so viewers can't redeem
rewards nobody will handle. Redemptions of rewards made elsewhere still arrive, but
`CompleteRedemption` can't change them; `GetOtherRewardsAsync` lists those rewards so you can
delete leftovers in the Creator Dashboard.

### Stream info

Set `StreamInfo` before `Connect` and the title, category (by name, matched exactly or by search)
and tags are applied as soon as the broadcaster logs in; call `SetStreamInfoAsync` to change them
later. Tags replace the channel's tags: up to 10, each up to 25 letters or digits.
`StreamInfoStatus` says whether it worked.

### Rate limits

Replies go through a queue that stays under Twitch's limits (20 messages per 30 s and one per
second for a normal account, 100 per 30 s for a moderator or VIP), drops a message repeated
within 30 s, and trims messages to 500 characters. The client notices the bot's moderator badge
on its own messages and raises the limit.

## Testing without going live

Use `LocalDebugLiveChatClient` in place of the Twitch client. It draws a chat box in the
bottom-left corner of the Game view: type `!jump 3` and press Enter. Bot replies show in the box
(and in the Console). Backquote shows or hides the box; Escape releases its keyboard focus.

- Toggle the simulated viewer's roles in the Inspector (subscriber, VIP, moderator,
  broadcaster) to test permissions.
- `SimulateIncoming("!vote up", "viewer42", isSubscriber: true)` fakes messages from many viewers.
- Add `cheerN` to a message (`!buy steel cheer100`) to simulate bits.
- `SimulateSubscription`, `SimulateGiftSubscription`, `SimulateCommunityGift`, `SimulateRaid`,
  `SimulateFollow` and `SimulateRedemption` raise the stream events.

## YouTube

`YouTubeLiveChatClient` is a **stub**: `Connect` raises an error saying it isn't implemented yet.

## Namespaces

| Namespace | Contents |
|---|---|
| `LiveChat` | `LiveChatClientBase`, `LiveChatMessage`, the event types, `LiveChatConnectConfig`, `LocalDebugLiveChatClient` |
| `LiveChat.Commands` | Router, handlers, parser, permissions |
| `LiveChat.Twitch` | `TwitchLiveChatClient`, and the pieces it's built from: `TwitchAccount`, `TwitchOAuth`, `TwitchHelix`, `EventSubSocket`, `EventSubTranslator`, `ChatSendQueue`, `TwitchTokenStore` |
| `LiveChat.YouTube` | YouTube client (stub) |

## Tests

EditMode tests (command parser, EventSub translator, send queue, token store) live in
`Tests/Editor` (assembly `LiveChat.Tests`). To run them in a project that consumes this
package, list the package under `testables` in that project's `Packages/manifest.json`:

```json
"testables": ["com.cfomodz.livechat"]
```

## Releasing

Bump `version` in `package.json`, add a `CHANGELOG.md` entry, merge to `main`, and tag
`vX.Y.Z`. Consumers pin the tag in their git URL.
