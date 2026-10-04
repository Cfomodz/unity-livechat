# Changelog

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
