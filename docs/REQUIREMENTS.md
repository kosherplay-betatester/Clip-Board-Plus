# Delivery audit

This audit distinguishes implemented behavior from completed compatibility checks. It does not claim universal application, codec, or Windows-shell compatibility.

| Requested behavior | Current implementation and evidence | Remaining verification |
| --- | --- | --- |
| More history, configurable capacity | SQLite history; count, age, disk, per-item and payload-cache limits. Paging, pruning, pins, reopen and budget tests pass. A prior 10,000-item benchmark is recorded in VERIFICATION.md. | Performance at other capacities and on other hardware is not established. |
| Custom shortcut; retain Windows history | Button recorder supports single keys through three-key chords, including F12 and ScrLk. Dedicated global shortcut worker; registration conflicts reported. Registration and switching tests pass. Live Ctrl+Shift+V opening/search/paste passed in Notepad. Windows history now has independent preferences and policy detection. | User-specific shortcut utilities may conflict. |
| Replace Win+V | Native keyboard hook with paired-key suppression and Windows-menu masking. Installation, state transitions, and removal pass. | User confirmed Win+V opens the app while Windows history is disabled. Enabled-Windows-history coexistence remains unverified. |
| Simple, attractive, responsive interface | Native WPF dark/light UI; bounded rows, virtualization, background storage work and debounced indexed search. Current renders were inspected after startup changes. | Broad DPI/multiple-monitor coverage has not been performed. |
| Text and rich content | Unicode, HTML, RTF, bitmap and PNG clipboard round-trips pass. Paste focus restoration and real native Ctrl+V passed in an interactive target and Notepad. | Proprietary app formats and virtual attachments are explicitly unsupported; arbitrary target-app compatibility cannot be inferred from Notepad. |
| Image enlargement and full screen | Thumbnail opens image preview; bounded decode, zoom, pan and F11. Rendered preview and local OCR verified. | Unusual image codecs depend on Windows. |
| File/folder/groups as references | CF_HDROP references only; missing-source detection; replay requests COPY. Mixed format tests pass. Live Explorer capture and history replay copied a file and two folders with matching hashes and intact sources; the reference payload was 1,006 bytes. Evidence: explorer-transfer-results.json. | Network/offline source behavior beyond missing-source detection is not established. |
| Audio/video preview from source | Inline audio controls and video-player launch with play/pause/stop, timeline and 10-second skips. Local MP3, WAV and H.264 MP4 playback and cleanup pass. | HTTP media playback test was rejected by automatic approval review (`blocked by policy`); online playback remains unverified. Webpage links open in a browser. |
| Smart memory and disk use | Byte-bounded LRU payload cache, pressure eviction, small thumbnails, capped image decode, disk pruning, protected storage, and no media import for file references. Cache and disk tests pass. | Total process memory includes UI/runtime/decoders; the cache setting is not a whole-process RAM limit. |
| Productivity improvements | Pins, named snippets, indexed search, filters, plain-text paste, text transformations, combine, paste queue, OCR, exclusions and pause. Storage/snippet/search/OCR and clipboard replay evidence is recorded. | Combined UI workflows beyond those documented have not all been independently exercised. |
| Start with Windows | Optional Settings switch writes current-user Run registration with quoted executable path and --background; disabling removes it. Hidden initialization test passes without showing or activating the window. | Actual sign-out/sign-in is pending; it would interrupt the user's session. |
| Runnable deliverable | Self-contained Windows x64 folder/ZIP, source, build/test/publish scripts and README. Published executable smoke-tested. | Package must be kept in a permanent folder for startup registration to remain valid. |

Reports: `artifacts/test-results.json`, `artifacts/published-test-results.json`, `artifacts/interactive-paste-results.json`. Visual evidence: `artifacts/visual/`. All fixtures are synthetic. The outstanding end-to-end checks above prevent claiming the full requested scope is completely verified.

## 1.1 requested upgrades

- Clicked-image routing regression passes with two selected images and two open previews.
- Shortcut recorder verified live for F12, ScrLk and Ctrl+Shift+V; native configuration and suspension tested.
- App exclusions use a searchable open-app picker, executable browser and removable list.
- Windows history is independently configurable, with Automatic, On, Off and Unchanged modes. Policy restrictions are detected rather than removed. Isolated registry tests verify preference writes.
- New multi-resolution clipboard-and-plus icon is included in the application and tray.
- Source, Apache license, README, screenshots, contribution/security documentation, dependency notices and Windows CI workflow are pushed to the user's GitHub repository. Version 1.1.0 is published with the branded installer, portable ZIP and SHA-256 checksums. Both downloaded assets match their published hashes; the downloaded portable app passes 52 core and live update checks.
- Manual update checking and a release-page link are available in Settings; no automatic updating. A branded per-user installer passes isolated installation, upgrade and uninstall checks.
