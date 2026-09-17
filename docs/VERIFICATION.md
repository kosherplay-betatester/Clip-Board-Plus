# Verification record — 1.1.0

Tested locally on Windows 11 build 26200 with .NET SDK 10.0.303. Results are measurements on this machine, not guarantees for every PC, app, or media codec.

## Corrected 1.1 build 2 checks

- Final self-contained build passed 75 storage, selection, clipboard, preview, shortcut and media checks. The separate interactive run passed 58 checks, including three consecutive native pastes with different clicked rows while an older selection and nonempty queue remained present.
- Live packaged-app testing in Notepad opened history by Ctrl+Shift+V twice and double-clicked two different items. Both arrived at the original text caret in the correct order. Copy (2) also placed both selected texts on the clipboard in list order and pasted them correctly into Notepad.
- Regression coverage includes selection changes during an asynchronous refresh, preservation of multiple selections, five consecutive clipboard writes, mixed text/files/images, duplicate paths, missing-source rejection and single-item rich text preservation.
- Installer probe passed nine install/upgrade/uninstall checks. Setup now has a consent-first graceful shutdown path for build 2 and uses Restart Manager for older installed builds. Silent setup refuses to close a running production app.
- Build 2 binaries have file version 1.1.0.2. The public release remains 1.1.0 as requested; its old assets are replaced. Existing original 1.1 users should redownload the installer because that older update checker compares release versions, not replacement build numbers.
- Current history and media screenshots were rendered from synthetic fixtures and inspected.

## Original 1.1 release checks

- Downloaded both published 1.1.0 assets from GitHub Releases and verified them against the published SHA-256 checksums. Extracted the downloaded portable ZIP and ran its executable independently of the build directory: all 52 core and live update-check tests passed.
- Release build: zero warnings and zero errors.
- Branded installer passed nine isolated install/upgrade/uninstall checks, including shortcuts, startup registration, Windows Apps registration and preservation of user-created files. The installed app passed 51 core checks. Production startup preferences were unchanged.
- **64 checks passed** in the final self-contained build: storage, clipboard, shortcut, preview, Windows-preference, update-version comparison and local-media regressions. An earlier 1.1 self-contained build also passed the interactive native-paste check.
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

- Post-publication manual update check contacted the live GitHub latest-release endpoint and correctly reported 1.1.0 as current; all 52 checks in that run passed.

## Performance record

The final 1.1 self-contained build saved 10,000 synthetic text clips in 28.9 seconds. Across 100 indexed searches, median latency was 11.1 ms and p95 was 12.8 ms; reported storage was 69.9 MiB. The test process working set after the complete core suite and benchmark was 373.6 MiB, with zero bytes in the payload cache at sampling. This is a post-workload measurement, not idle RAM, a maximum, or a whole-process budget guarantee. All 51 core checks passed in this run.

The earlier 1.0 text-focused 10,000-entry benchmark wrote in 26.8 seconds, with indexed query median 10.8 ms and p95 11.4 ms; database plus WAL used 62.8 MiB. This is historical evidence, not a new 1.1 benchmark or a universal latency guarantee. Total process RAM includes .NET, UI, images and media decoders beyond the bounded serialized-payload cache.

## Remaining compatibility checks

- Actual Windows sign-in, other installed apps, elevated destinations, remote sessions, multiple monitors and uncommon scaling/codecs/languages.
- Policy-free Windows history enable/disable and Win+V interaction with the enabled Windows popup and other shortcut utilities.
- Online playback is implemented but not end-to-end verified: automatic approval review rejected the local HTTP media test with the reason `blocked by policy`. Local media playback passed.

### Final Windows integration check

Use the published installer or portable 1.1.0 build. These steps change Windows preferences; perform them in a session where those changes are intended. Record the previous settings so they can be restored afterward.

1. On a machine where Windows clipboard history is permitted, enable it in Windows Settings and confirm native Win+V opens its history. If the setting is managed by Shutup10++, remove that specific restriction there first; Clipboard Plus does not remove policies.
2. In Clipboard Plus, choose Win+V and Automatic Windows history behavior, then save. Copy two harmless test strings. Press Win+V and confirm only Clipboard Plus opens and both strings can be pasted.
3. Record Ctrl+Shift+V, retain Automatic, and save. Confirm Ctrl+Shift+V opens Clipboard Plus, Windows history reports on, and Win+V opens the Windows panel.
4. With the custom shortcut still selected, save Always off and then Always on. Confirm the effective Windows setting follows each choice; a saved preference alone is not proof that Windows applied it.
5. Enable Start with Windows. At the next normal sign-in, confirm Clipboard Plus is present in the tray without opening its panel, captures new copies, and responds to the chosen shortcut. Disable startup and confirm it stays closed after a subsequent sign-in.
6. For online media, explicitly play a trusted direct MP3/MP4 URL, check pause/seek/stop, then close the preview. A normal video webpage is not a direct media URL. This manual check has not been recorded as passed.

The release is available for use. The checks above remain open verification items, not completed test results.

Local reports and generated fixtures are in ignored `artifacts/`. Committed screenshots under `docs/images/` contain synthetic sample content only. No user clipboard database or user preferences are distributed.
