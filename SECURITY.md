# Security

Please do not post passwords, tokens, private clipboard contents, or personal files in public issues. Use GitHub's private vulnerability reporting when available; otherwise open an issue containing only a request for a private reporting channel.

Clipboard payloads, summaries, thumbnails and paths use Windows DPAPI scoped to the current user. Search uses keyed word hashes. Metadata such as time/type/count remains visible. This does not protect data from software running as the same Windows user.

App exclusions and clipboard opt-out flags are best-effort privacy features, not a reliable secret detector. Pause capture when handling sensitive information. Previewing an online source contacts that source only on request. The app does not remove Windows administrator policies or bypass integrity boundaries for paste.
