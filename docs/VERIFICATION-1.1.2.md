# Clipboard Plus 1.1.2 verification

Windows 11, .NET SDK 10.0.303, 2026-09-22. This extends the [1.1.1 reliability record](VERIFICATION-1.1.1.md).

## Code fidelity

110 checks passed in the combined core, real clipboard, large-code and local-media suite. Release compilation had zero warnings/errors. See `test-results-v1.1.2.json` for the synthetic test results.

- Five code fixtures pass exact ordinal string equality through capture, encrypted storage, and exact-text replay. They cover C#/Python/JavaScript-like syntax, tabs, leading/trailing whitespace, CRLF/LF/CR and mixed line endings, literal backslashes/escapes, quotes, brackets, Unicode combining characters, Hebrew/Arabic/CJK, emoji, zero-width characters and BOMs.
- All five fixtures also pass through the real Windows clipboard with exact character equality and no rich-text substitution.
- A clip exceeding 16 million characters (32 MiB as UTF-16) passes capture, encrypted save, disk reopen and native clipboard restoration. Equality includes the final sentinel and trailing whitespace. The test uses a 64 MB item limit and 1 MB cache; the clip bypasses that cache, and payload storage is below 1.5 bytes per character for this mostly ASCII fixture, proving the old duplicate search string is absent.
- Oversized rich formatting retains complete plain text with a capture note. Oversized plain text is explicitly rejected. Malformed Unicode is rejected instead of being silently replaced. Streaming fingerprints match the prior 1.1.1 format for cross-source deduplication.
- The prior migration, stable selection, queue routing, concurrent-writer, Unicode capture, image and local-media regressions pass alongside these tests.

## Scope and limits

Exact clipboard text does not override the destination editor's own auto-indent or format-on-paste features. File byte encoding is not a clipboard-text property. Windows text clipboard formats are not a substitute for arbitrary binary data.

Maximum single clip remains configurable from 1 to 64 MB, with the existing 16 MB default. Limits are enforced without truncation. Search indexing intentionally covers the first 262,144 text characters and at most 16,384 prefix tokens; later text in a giant clip may not match search. Full text storage and restoration are unaffected.

End-to-end native key injection into external editors was not newly verified. The desktop automation helper was unavailable and the unattended focus attempt in 1.1.1 fell back to copy. These clipboard equality tests must not be represented as certification of every editor or messaging app. Remaining Windows policy, sign-in, online-media and destination compatibility checks are listed in the earlier records.

## Reproduce

Run `./scripts/Test.ps1 -LargeCode -Media` in a desktop session, with other test suites stopped. LargeCode includes the real clipboard integration checks and temporarily changes the clipboard to synthetic content, restoring its prior data object afterward. The optional `-InputTest` separately requires clicking Start and allowing the foreground/input test to finish.
