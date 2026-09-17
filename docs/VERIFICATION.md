# Verification record — 1.1.0

Tested locally on Windows 11 build 26200 with .NET SDK 10.0.303. Results are measurements on this machine, not guarantees for every PC, app, or media codec.

## Current release checks

- Release build: zero warnings and zero errors.
- **63 checks passed** in the storage, clipboard, shortcut, preview, Windows-preference and local-media regression suite. The final self-contained release passed **64 checks** with the interactive native-paste check included.
- Two distinct image payloads opened correctly while both rows were selected and the first preview remained open. Thumbnail actions now carry the clicked row directly.
- Inline MP3 controls played, paused, sought, stopped and released their decoder. Video row controls opened a player, played H.264 MP4, paused, sought, stopped and released it. WAV and MP4 preview checks also passed.
- Legacy encrypted media summaries upgraded on demand and exposed the original source reference without importing source media bytes.
- F12 and Scroll Lock hook configuration, recorder suspension/restoration, single-key parsing, three-key limits, and recorded key formatting passed.
- Live recorder UI captured **F12**, **Ctrl+Shift+V**, and **ScrLk**, and accepted the chosen key back into Settings.
- Windows history Automatic/On/Off/Unchanged decisions passed. Preference writes were verified using an isolated registry test key; the user's Windows privacy preferences were not changed by those tests.
- Policy detection correctly reports the current machine's disabled clipboard-history policy. The app does not claim it can override Shutup10++/administrator policy. Live enable/disable on an unrestricted machine remains a compatibility check.
- Dark/light history, Settings, image preview, and inline media rows were rendered and inspected. The new icon appears in the panel and native window titles.
- Earlier live Notepad testing verified shortcut opening, search and multiline paste. The 1.1 self-contained build also passed the interactive native-paste check; focus restoration is covered by the optional `-InputTest`.
- Earlier live Explorer testing replaced the system clipboard with text, then restored a saved file plus two folders from history. Destination file hashes matched all three original files and sources stayed intact. The saved reference payload was 1,006 bytes.
- User reported that Win+V opens Clipboard Plus successfully with Windows clipboard history disabled by Shutup10++. Coexistence with Windows history enabled has not been independently verified.

The test suite also covers protected storage/reopen, keyed search, paging, duplicates, named snippets, pins, expiry, disk eviction, bounded cache, rich/plain text, bitmap/PNG formats, bounded thumbnails, mixed file/folder references, missing sources, background initialization without showing/focusing a window, and real clipboard update/replay with pause and exclusion flags.

## Performance record

The earlier 1.0 text-focused 10,000-entry benchmark wrote in 26.8 seconds, with indexed query median 10.8 ms and p95 11.4 ms; database plus WAL used 62.8 MiB. This is historical evidence, not a new 1.1 benchmark or a universal latency guarantee. Total process RAM includes .NET, UI, images and media decoders beyond the bounded serialized-payload cache.

## Remaining compatibility checks

- Actual Windows sign-in, other installed apps, elevated destinations, remote sessions, multiple monitors and uncommon scaling/codecs/languages.
- Policy-free Windows history enable/disable and Win+V interaction with the enabled Windows popup and other shortcut utilities.
- Online playback is implemented but not end-to-end verified: automatic approval review rejected the local HTTP media test with the reason `blocked by policy`. Local media playback passed.

Local reports and generated fixtures are in ignored `artifacts/`. Committed screenshots under `docs/images/` contain synthetic sample content only. No user clipboard database or user preferences are distributed.
