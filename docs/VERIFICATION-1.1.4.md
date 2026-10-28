# Clipboard Plus 1.1.4 — verification

## Telegram black images — root cause (measured, not assumed)

Seven Telegram clips in a copy of the real history database (720×1280 and 588×1280, 0.3–1.1 MB each) were decoded: **every pixel had alpha = 0** while RGB spanned 0–255. The stored PNG therefore contained the photo but declared it fully transparent, which renders black and pastes black. Screenshots and other apps in the same database had alpha 255 and were unaffected. Channel order, premultiplication, stride and row direction were not the cause.

Fix: an image whose pixels are all alpha 0 but carry colour is treated as opaque RGB32 (`ImageFidelity`). Any non-zero alpha — real transparency — leaves the bytes untouched. Existing rows are repaired by a background pass (`RepairImagesBatchAsync`, `user_version` 3); a consistent database backup was taken first.

## Automated results (this machine, Windows 11 build 26200, .NET SDK 10.0.303)

- Deterministic suite (`scripts/Test.ps1`): **94 checks passed**, build 0 warnings/errors. New checks compare pixel values for: Telegram-style PNG → opaque with identical RGB; transparent / graduated-alpha / opaque / empty images unchanged byte-for-byte; 24-bit bottom-up CF_DIB row order and BGR channel order; 32-bit DIB with unused alpha; capture → publish PNG equality; repair of stored images with thumbnail refresh; multi-selection Paste order without prior Copy; visible rows.
- Visible rows (fully inside the list): 100% (1040 DIP work area) **7**, 175% (578 DIP window) **5**, 200% (504 DIP window) **4**. Requirement is met at 100% and 175% on a 1080p display; 200% on 1080p shows four.
- Real-clipboard suite (`scripts/Test.ps1 -Clipboard`) adds a retry-until-saved check and a real-clipboard Telegram-style PNG capture. On this PC the suite is unreliable *regardless of the code under test*: the 1.1.3 baseline passed 1 of 3 runs and failed in different places (clipboard held open by other clipboard clients such as the installed Clipboard Plus, Telegram and Windows). See the release notes for the number of clean passes obtained.

## Not verified

- A live right-click → Copy in Telegram followed by paste into Paint/Telegram. The root cause was established from stored data and reproduced with a synthetic PNG of the same shape; the live path was not driven by automation.
- Paste of a multi-item selection into specific third-party apps (each app picks the formats it accepts).
- 200% scaling on a 1080p display shows four rows, not five.
