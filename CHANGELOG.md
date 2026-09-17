# Changelog

## 1.1.0

### Corrected release — build 2

- Double-click pastes the clicked row, independently of stale selection or a pending paste queue.
- Asynchronous refresh preserves current multi-selection instead of restoring an earlier selection.
- Captures destination window and native focused control when the shortcut fires; verifies focus, window identity and clipboard contents before injecting paste.
- Serializes copy/paste operations and waits for mouse/shortcut release to prevent overlapping actions from replacing a pending paste.
- Adds copy icons per row and beside Recent clips, Ctrl+C for selected history, and a text-only caption action.
- Multi-copy combines text and file references; copied image pixels can be exported as PNG attachments. The receiving app chooses supported formats.
- Setup asks permission before closing the running app. Declining leaves it running; silent setup refuses to close it without interactive consent.

- Added a branded per-user installer with shortcut/startup choices, upgrade support, and Windows Apps uninstall.
- Added an on-demand update check and direct release-page link; no automatic updating.

- Added a button-based shortcut recorder for one to three keys, including F12 and Scroll Lock.
- Added shortcut suspension during recording and restoration on cancel.
- Fixed thumbnail preview routing so the clicked clip is used rather than the previous selection.
- Added inline audio and video controls: play, pause, stop, seek, elapsed/duration, and 10-second skips. Video opens a separate player.
- Added an app-exclusion picker with search, executable browsing, and removable entries.
- Added independent Windows clipboard-history preferences with policy detection and automatic behavior based on the shortcut.
- Replaced the generic plus icon with a clipboard-and-plus icon at multiple resolutions.
- Preserved encrypted history and upgraded existing media rows on demand.

## 1.0.0

Initial local build: native Windows history, encrypted disk storage, configurable budgets, search, pins/snippets, image previews/OCR, file references, media previews, custom shortcut/Win+V hook, tray and optional sign-in startup.
