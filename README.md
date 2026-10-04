# unity-livechat

Live chat clients and a chat command router for Unity (`com.cfomodz.livechat`).

- **Clients:** Twitch chat (TwitchLib), Twitch PubSub (channel points, bits), a YouTube
  stub, and a local debug client with an on-screen chat box.
- **Command router:** parses `!command args` messages and dispatches them to
  `ChatCommandHandler` components, with per-command permissions
  (anyone, subscriber, VIP, moderator, broadcaster).

Extracted from [Interactive-Livestream-Chaos-League](https://github.com/Cfomodz/Interactive-Livestream-Chaos-League),
with history. Used by Chaos League and [chat-minigames](https://github.com/Cfomodz/chat-minigames).

Requires Unity **2022.3** or newer.

## Install

Add the package to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.cfomodz.livechat": "https://github.com/Cfomodz/unity-livechat.git#v0.1.0"
  }
}
```

Or use **Window → Package Manager → + → Add package from git URL…** with the same URL.

### TwitchLib is included

The package ships the TwitchLib DLLs (Client, PubSub, Communication, Unity, Api, Api.Helix and
their dependencies) under `Runtime/Plugins/TwitchLib/`. If your project already has copies of
any of these DLLs in `Assets/`, delete them. Two copies of the same DLL cause
"Multiple precompiled assemblies with the same name" errors.

The DLLs are auto-referenced, so your project's own scripts can keep using
`TwitchLib.Api` etc. directly.

## Quick start

1. Write a command:

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

   `ChatCommandContext` gives you the parsed `Command`, `Args` / `ArgsRaw`, the sender
   (`Username`, `DisplayName`, `IsSubscriber`, `IsVip`, `IsModerator`, `IsBroadcaster`), the
   original `LiveChatMessage`, and `Reply` / `Say` to answer in chat.

2. Put a chat client, your handlers and a router on a GameObject:

   ```csharp
   using LiveChat;
   using LiveChat.Commands;
   using LiveChat.Twitch;
   using UnityEngine;

   public class ChatSetup : MonoBehaviour
   {
       [SerializeField] private string _channel = "mychannel";
       [SerializeField] private string _botAccessToken = ""; // oauth token, don't commit it

       private void Start()
       {
           var client = gameObject.AddComponent<TwitchLiveChatClient>();
           gameObject.AddComponent<JumpCommand>();
           var router = gameObject.AddComponent<ChatCommandRouter>(); // finds the client on the same GameObject
           router.RefreshHandlers();

           client.Connect(new LiveChatConnectConfig
           {
               ChannelName = _channel,
               BotAccessToken = _botAccessToken
           });
       }
   }
   ```

   The router finds every `ChatCommandHandler` under its GameObject (or under `Handler Root`)
   on `Awake`. If you add handlers after that, call `router.RefreshHandlers()` or
   `router.RegisterHandler(handler)`.

   You can also set all of this up in the Inspector. The built-in `ChatCommandHelp`,
   `ChatCommandPing` and `ChatCommandEcho` handlers fill in their command name when added in
   the editor (`Reset`). From code, subclass them and call `SetDefaults` in `Awake`, as `JumpCommand` does.

3. If you don't want commands, subscribe to `client.MessageReceived` and handle every
   `LiveChatMessage` yourself.

## Testing without going live

Use `LocalDebugLiveChatClient` in place of the Twitch client. It draws a chat box in the
corner of the Game view. Type `!jump 3` and press Enter. Bot replies show in the box (and in
the Console).

- Toggle the simulated viewer's roles in the Inspector (subscriber, VIP, moderator,
  broadcaster) to test permissions.
- Call `SimulateIncoming("!vote up", "viewer42", isSubscriber: true)` from code to fake
  messages from many viewers, e.g. to test vote weighting.

## YouTube

`YouTubeLiveChatClient` is a **stub**: `Connect` raises an error saying it isn't
implemented yet. It exists so the client API has a second platform to stay honest against.
Wiring it to the YouTube Data API (`liveChatMessages.list` polling) is future work.

## Twitch PubSub

`TwitchPubSubClient` listens for channel point redemptions and bits. It raises its own
events rather than chat messages; see the source for the event list.

## Namespaces

| Namespace | Contents |
|---|---|
| `LiveChat` | `LiveChatClientBase`, `LiveChatMessage`, `LiveChatEmote`, `LiveChatConnectConfig`, `LocalDebugLiveChatClient` |
| `LiveChat.Commands` | Router, handlers, parser, permissions |
| `LiveChat.Twitch` | Twitch chat and PubSub clients |
| `LiveChat.YouTube` | YouTube client (stub) |

## Tests

EditMode tests for the command parser live in `Tests/Editor` (assembly `LiveChat.Tests`).
To run them in a project that consumes this package, list the package under `testables` in
that project's `Packages/manifest.json`:

```json
"testables": ["com.cfomodz.livechat"]
```

Then open **Window → General → Test Runner → EditMode**.

## Releasing

Bump `version` in `package.json`, add a `CHANGELOG.md` entry, merge to `main`, and tag
`vX.Y.Z`. Consumers pin the tag in their git URL.
