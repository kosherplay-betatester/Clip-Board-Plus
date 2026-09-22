# Changelog

## 1.1.2 — 2026-09-22

- Explicit **Paste exact text** (Ctrl+Enter) and **Copy exact text** actions preserve source code without rich formatting, trimming, reindentation, newline conversion or escape expansion.
- Preserve authoritative whitespace-only/invisible plain text and leading/trailing Unicode BOM characters. Reject malformed Unicode before serialization can replace it.
- Store original payload fields once, without duplicating the entire code as a persisted search field. Stream deduplication hashing and build bounded list previews; existing 1.1.1 fingerprints remain compatible.
- Bound indexing of giant/minified clips to 262,144 text characters and 16,384 prefix tokens. Full original content is retained. Cache budgets continue to exclude oversized payloads.
- If rich formatting alone exceeds the item budget, retain the complete original plain text and report the formatting omission. Oversized plain text gets a visible rejection notice instead of truncation.
- Added exact-character fixtures covering C#/Python/JavaScript-like code, mixed line endings, indentation, trailing spaces, escaped characters, multilingual Unicode and invisible characters. A 32 MiB / 16-million-character clip passed encrypted save/reopen/native-clipboard equality testing.

## 1.1.1 — 2026-09-22

- Normal Paste and Enter use the selected clip. The queue has its own **Paste next queued** button and no longer silently overrides selection.
- Incoming captures no longer reorder visible rows. A **New clips** button refreshes them; reopening the panel also refreshes. Query results cannot replace a row during a double-click, and stale search results are discarded.
- Clipboard writes carry unique receipts, are checked before automatic paste, and remain serialized through input delivery. Focus changes cancel automatic delivery and leave the requested clip ready for manual paste, with a tray notice when needed.
- Database IDs are never reused after deletion. Existing databases migrate transactionally while preserving payloads, pins, and indexes.
- HTML/RTF-only captures gain readable, searchable text. Existing blank previews repair in bounded background batches; original rich data remains intact. Whitespace-only clips get a clear label.
- A slower earlier image preview cannot replace a more recently requested source. Partial keyboard injection releases synthetic modifier keys.
- Added regression tests for legacy migration, text recovery, deletion identity, competing writers, external clipboard replacement, rapid Unicode captures, stale queries, and selection/queue routing. See `docs/VERIFICATION-1.1.1.md` for measured results and remaining compatibility checks.


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
