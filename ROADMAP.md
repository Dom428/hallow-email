# Hallow Email roadmap

## Done

| Version | What |
|---|---|
| 1.0 | IMAP / SMTP accounts on MailKit 5.0.0 / MimeKit.Core 5.0.0 with app passwords (Gmail, Outlook / Hotmail, Yahoo, iCloud presets with app-password help; custom servers), optional OAuth 2.0 for Gmail / Microsoft with the user's own client ID (PKCE, loopback redirect, refresh token DPAPI-encrypted, XOAUTH2). Unified inbox with People / Newsletters / Receipts / Notifications (headers + sender patterns + subject words, per-sender / per-domain overrides), per-account folders, conversations, an AES-256 + HMAC cache keyed by DPAPI, search with operators, IMAP IDLE push. Privacy: HTML rebuilt with MimeKit's tokenizer (no scripts / frames / forms / handlers / remote CSS), remote images blocked by default, "Load images from this sender", trackers always blocked (1x1 / hidden images, Hallow Shield's lists when on disk + a built-in list, unique-ID URLs), real link destinations and text-vs-domain warnings, a locked-down WebView2 reader or plain text. Safety: phishing hints (brand display names, look-alike domains, SPF / DKIM / DMARC from Authentication-Results, Reply-To, first-time sender), attachment flags and a Defender scan (MpCmdRun at Idle) before opening. Private spam filter (naive Bayes with Robinson / Fisher combining, seed corpus, rule signals with reasons, moves to Junk, trusted senders never flagged, Low / Normal / High). Rules, suggestions, labels (local + IMAP keywords), the Screener, bulk actions. Unsubscribe finder with RFC 8058 one-click (asks first). Compose: rich / plain, attachments, signatures, Send as, drafts on the server, Undo send, Send later, Snooze, Outbox with retries. Cloudflare Email Routing guide and masked addresses via the API (token DPAPI-encrypted). Family link (pipe incl. `compose`, status.json with unread counts only, Hub tray hosting and notify - held during games), light / dark, accent, crash reports, updater |
| 1.1 | Per-account status for Hallow Remote 1.18 / Hub 2.17 (added fields only): opaque `id`, `label` with `labelUserChosen` / `labelHasAddress` (no addresses unless you typed one as the label), unread per account, and `needsAttention` / `attention` / `attentionText` (sign-in-failed, app-password-needed, server-unreachable, quota, error - a code and plain words, never server text or mail content); a top-level `needsAttention` count; an `accounts` pipe command; the reason on Settings > Accounts. Tests against the local IMAP / SMTP stand-in (scripted sign-in answers, a closed port, the pipe of a test copy); 5-minute kill timeout. |

## Later

| Idea | Why it isn't in 1.0 |
|---|---|
| Hallow Keys integration (masked address while signing up) | The hook is documented (`masked-new <site>` over the family pipe, only after a confirmation in Hallow Email); Keys isn't changed in this round. |
| Full offline copy of every message | 1.0 caches the newest 400 messages per folder and pre-fetches 40 bodies per sync (opened mail is cached too); older mail opens from the server. |
| Gmail's All Mail / labels as folders | Gmail's All Mail, Starred and Important are views of the same mail; 1.0 skips syncing them (no duplicates) and uses All Mail only as the archive target. |
| S/MIME and PGP | Needs MimeKit's cryptography part and BouncyCastle; not shipped in 1.0. |
| DKIM checked by the app itself | 1.0 trusts your own provider's Authentication-Results verdict (the topmost header), which needs no DNS lookups. |
| Send later / snooze while the app is closed | Would need a server-side scheduler; 1.0 sends overdue mail as soon as it starts. |
| Remote images through a privacy proxy | Allowed images load directly from the sender's server (https only); trackers are never loaded. |
| Contacts / address book and autocomplete | Recipients you write to are remembered as trusted senders only. |
| Calendar invites (.ics) | Shown as attachments. |
| Default mail app (mailto:) registration | `HallowEmail.exe mailto:...` works; registering as the default handler is left to Windows' Default apps later. |
