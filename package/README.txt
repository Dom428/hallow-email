HALLOW EMAIL 1.1 - a private desktop email client, Hallow style

INSTALL: right-click the zip > Properties > Unblock, Extract All, then double-click INSTALL.bat (no admin needed).
It installs to %LOCALAPPDATA%\Programs\Hallow Email. Your data: %APPDATA%\HallowEmail (encrypted).

WHAT'S NEW IN 1.1
- Settings > Accounts says why an account needs attention, in plain words: the sign-in failed, an app password is
  needed, the server can't be reached, or the mailbox / provider limit is full.
- Hallow Remote and Hub can show unread mail per account and that reason (by your own account label - Hallow Email never
  adds your address).

WHAT'S NEW IN 1.0
- IMAP / SMTP accounts with app passwords: Gmail, Outlook / Hotmail, Yahoo, iCloud, or any provider. The add-account
  wizard shows how to make an app password. Optional OAuth sign-in for Gmail / Microsoft with a client ID you register.
- One inbox for every account, sorted into People / Newsletters / Receipts / Notifications (move a sender to change it),
  plus each account's folders, conversations, and search across the encrypted local cache. New mail arrives at once (IMAP IDLE).
- Privacy: remote images and tracking pixels blocked (Hallow Shield's lists when present), "Load images from this sender",
  links show where they really go and warn when the text names another site. HTML mail is shown in a locked-down WebView2
  (no scripts, nothing remote) or as plain text.
- Safety: phishing hints (brand-name senders, look-alike domains, SPF / DKIM / DMARC results, first-time senders),
  attachment flags (programs, macros, double extensions, archives with programs) and a Microsoft Defender scan before
  anything opens.
- A private spam filter that learns from your clicks (moves spam to Junk - never deletes), rules, suggestions, the
  Screener for first-time senders, labels and bulk actions.
- Unsubscribe finder with one-click unsubscribe (asks first). Undo send, Send later, Snooze, drafts saved to the server.
- Your own domain with Cloudflare Email Routing, and masked addresses (needs a Cloudflare API token you create).
- Hallow family link: Hallow Hub's icon, quiet notes for mail from People (not while you play), light and dark.

YOU NEED TO
- Make an app password for each account (the wizard explains it), or register your own OAuth client ID.
- For your own domain: turn on Cloudflare Email Routing and verify your inbox there (the "Your addresses" page explains it).

Licences: MailKit / MimeKit (MIT), Microsoft WebView2 SDK, .NET packages (MIT), Font Awesome Free (CC BY 4.0) - see licenses\.
