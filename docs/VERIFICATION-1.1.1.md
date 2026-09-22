# Clipboard Plus 1.1.1 verification

Tested on Windows 11 with .NET SDK 10.0.303 on 2026-09-22. The findings below distinguish clipboard preparation from actual input delivery to another application.

## Passed

- Release compilation: zero warnings and zero errors.
- 94 checks in the core, real Windows clipboard, local-media and benchmark run. The committed JSON report contains synthetic tests only.
- HTML-only UTF-8 streams, RTF-only content, Unicode, invisible/plain whitespace, malformed markup, and preservation of original rich formats.
- Upgrade from the old encrypted database schema, repair of an existing blank summary, restored search tokens, preserved pin and ID, 20 clear/add cycles without ID reuse, highest-ID deletion, and foreign-key integrity.
- Twenty sequential Unicode copies through the Windows clipboard listener, five fresh replay writes, unique write receipts, rejection of an external replacement, and deferral of a competing app writer until delivery completes.
- Twenty-four selected/clicked Paste requests with a stale queue present. Each published exactly the requested text. This exercises the complete selection/payload/copy fallback path without asserting destination input delivery.
- Visible rows remain stable after a new capture; selection survives refresh; an older search cannot overwrite a newer pending search.
- Local WAV/MP3/H.264 playback, pause/seek/stop/release, distinct-image preview routing, mixed text/file/image copy, missing-file rejection, file references, storage budgets, search, pins and shortcut configuration regressions.
- The self-contained package also passed all 94 checks. The installer probe passed nine checks covering install, upgrade, startup/shortcut/Apps registration, installed core tests, uninstall, and preservation of user files and production startup preferences.
- Synthetic dark-theme history was rendered and visually inspected. A misleading new-capture notice after startup maintenance was removed.

## Performance sample

10,000 synthetic text clips: 24.6 seconds total insertion, indexed search median 10.3 ms and p95 11.7 ms, 69.8 MiB database/WAL. The process working set was 377.3 MiB after the complete WPF/media/benchmark workload; the serialized-payload cache contained zero bytes. This is neither idle RAM nor a whole-process cap. Machine, content and installed codecs affect results.

## Not passed or not repeated

- The desktop automation helper failed to connect to its native pipe after recovery attempts. The interactive paste check timed out waiting for its Start button. An unattended attempt then reached the focus guard and correctly copied without injecting input because Windows kept another process in the foreground. The new 20-paste native-input test is available with `scripts/Test.ps1 -InputTest`, but is **not recorded as passed** for this release.
- New end-to-end checks in WhatsApp, Telegram, Gmail, Paint, video editors, elevated applications, remote sessions and multiple monitors were not completed. Previous-version Notepad evidence is historical, not proof of this version.
- A receiving application chooses supported formats. Mixed text plus attachments may need separate pastes. Clipboard checks reduce races but cannot guarantee that arbitrary third-party apps consume a clipboard synchronously, or that another clipboard manager never changes it after input is sent.
- Policy-free Windows history/Win+V coexistence, actual Windows sign-in and online-media playback remain open compatibility checks from the previous release; see `VERIFICATION.md`.

## Reproduce

Run `./scripts/Test.ps1 -Clipboard -Media -Benchmark` for the noninteractive suite. Clipboard tests temporarily use synthetic clipboard contents and restore the prior data object. Run suites sequentially in an unlocked desktop session. Run `./scripts/Test.ps1 -InputTest` separately, click **Start paste check**, and leave input untouched until it completes.

The installer retains its existing permission prompt before closing a running app. Silent setup refuses to close it. User history and settings live separately from program files; upgrades preserve them. The actual user profile was backed up locally before this upgrade and is never included in Git or release artifacts.
