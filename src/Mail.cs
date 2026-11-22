// Hallow Email - the mail engine: one background worker per account (MailKit IMAP: sync, IDLE push, actions), SMTP sending through a
// local queue (Undo send / Send later), drafts saved to the server, snoozes, and sorting / spam / rules on new mail.
// Passwords and tokens come from secrets.dat only when connecting; they are never logged (the log only says "signed in").
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace HallowEmail
{
    // a message opened for reading
    public class BodyView
    {
        public string Html; public string Text; public List<AttachPart> Attachments = new List<AttachPart>();
        public Dictionary<string, string> Cid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public MimeMessage Message;
    }
    public class AttachPart { public string Name; public string Type; public byte[] Data; public AttachInfo Info; }

    public static class Parse
    {
        public static BodyView Body(byte[] raw)
        {
            var v = new BodyView();
            using (var ms = new MemoryStream(raw)) v.Message = MimeMessage.Load(ms);
            var m = v.Message;
            try { v.Html = m.HtmlBody; } catch { }
            try { v.Text = m.TextBody; } catch { }
            if (v.Text == null && v.Html != null) v.Text = Privacy.ToText(v.Html);
            foreach (var part in m.BodyParts.OfType<MimePart>())
            {
                if (!string.IsNullOrEmpty(part.ContentId) && part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase) && !part.IsAttachment)
                {
                    using (var ms = new MemoryStream()) { part.Content.DecodeTo(ms); if (ms.Length < 4 * 1024 * 1024 && !part.ContentType.MimeType.Contains("svg")) v.Cid[part.ContentId.Trim('<', '>')] = "data:" + part.ContentType.MimeType + ";base64," + Convert.ToBase64String(ms.ToArray()); }
                    continue;
                }
                if (part.IsAttachment || (part.FileName != null && !(part is TextPart && part.ContentDisposition == null)))
                {
                    byte[] data; using (var ms = new MemoryStream()) { part.Content.DecodeTo(ms); data = ms.ToArray(); }
                    var a = new AttachPart { Name = part.FileName ?? "attachment", Type = part.ContentType.MimeType, Data = data };
                    a.Info = Attachments.Inspect(a.Name, data); v.Attachments.Add(a);
                }
            }
            return v;
        }
        public static string Preview(string text) { var t = Regex.Replace(text ?? "", @"\s+", " ").Trim(); return t.Length > 180 ? t.Substring(0, 180) : t; }
        public static string Clip(string text, int n) { if (text == null) return null; var t = Regex.Replace(text, @"[ \t]+", " "); return t.Length > n ? t.Substring(0, n) : t; }

        // a cache item from the headers the server sent (BODY.PEEK[HEADER])
        public static MailItem Item(string acc, FolderInfo f, UniqueId uid, MessageFlags? flags, HeaderList h, long size, DateTimeOffset? internalDate)
        {
            var it = new MailItem { Account = acc, Folder = f.Name, UidValidity = f.UidValidity, Uid = uid.Id, Size = size };
            Func<string, string> H = k => { var v = h == null ? null : h[k]; return string.IsNullOrEmpty(v) ? null : v.Trim(); };
            it.Subject = H("Subject") ?? "";
            InternetAddressList l;
            if (H("From") != null && InternetAddressList.TryParse(H("From"), out l)) { var mb = l.Mailboxes.FirstOrDefault(); if (mb != null) { it.FromName = mb.Name; it.FromAddr = (mb.Address ?? "").ToLowerInvariant(); } }
            if (H("Reply-To") != null && InternetAddressList.TryParse(H("Reply-To"), out l)) { var mb = l.Mailboxes.FirstOrDefault(); if (mb != null) it.ReplyTo = (mb.Address ?? "").ToLowerInvariant(); }
            it.To = H("To"); it.Cc = H("Cc");
            DateTimeOffset d; it.Date = (H("Date") != null && DateUtils.TryParse(H("Date"), out d) ? d : internalDate ?? DateTimeOffset.Now).LocalDateTime.ToString("s");
            it.MessageId = Id(H("Message-ID")); it.InReplyTo = Id(H("In-Reply-To"));
            it.References = H("References") == null ? null : MimeUtils.EnumerateReferences(H("References")).Take(30).ToList();
            it.ListUnsubscribe = H("List-Unsubscribe"); it.ListUnsubscribePost = H("List-Unsubscribe-Post"); it.ListId = H("List-Id"); it.Precedence = H("Precedence"); it.AutoSubmitted = H("Auto-Submitted");
            if (h != null)
            {
                var ar = h.FirstOrDefault(x => x.Field.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase));   // the topmost = your own provider's
                it.AuthResults = ar == null ? null : ar.Value.Trim();
                var sh = h.Where(x => x.Field.StartsWith("X-Spam", StringComparison.OrdinalIgnoreCase) || x.Field.Equals("X-Microsoft-Antispam", StringComparison.OrdinalIgnoreCase) || x.Field.Equals("X-Forefront-Antispam-Report", StringComparison.OrdinalIgnoreCase)).Select(x => x.Field + ": " + x.Value.Trim()).ToList();
                if (sh.Count > 0) it.SpamHeaders = string.Join("\n", sh).Substring(0, Math.Min(2000, string.Join("\n", sh).Length));
                var ctype = H("Content-Type") ?? ""; it.HasAttachments = ctype.StartsWith("multipart/mixed", StringComparison.OrdinalIgnoreCase);
            }
            if (flags.HasValue) { var fl = flags.Value; it.Seen = fl.HasFlag(MessageFlags.Seen); it.Flagged = fl.HasFlag(MessageFlags.Flagged); it.Answered = fl.HasFlag(MessageFlags.Answered); it.Draft = fl.HasFlag(MessageFlags.Draft); }
            return it;
        }
        static string Id(string v) { if (v == null) return null; var m = Regex.Match(v, "<([^>]+)>"); return m.Success ? m.Groups[1].Value : v.Trim(); }
    }

    public class Engine
    {
        public readonly Profile P; public readonly MailSettings S; public readonly MailStore Store;
        readonly Dictionary<string, Worker> workers = new Dictionary<string, Worker>();
        public event Action Changed; public event Action<List<MailItem>> NewMail;
        public readonly List<string> Log = new List<string>();
        public int MaxPerFolder = 400, PrefetchBodies = 40;
        public bool Offline;   // --snap
        public Engine(Profile p, MailSettings s, MailStore store)
        {
            P = p; S = s; Store = store;
            if (P.Bayes == null) { P.Bayes = Spam.Seeded(); }
        }
        public void Note(string s) { lock (Log) { Log.Add(DateTime.Now.ToString("HH:mm:ss") + " " + s); if (Log.Count > 200) Log.RemoveAt(0); } }
        public void Fire() { var h = Changed; if (h != null) try { h(); } catch { } }
        internal void FireNew(List<MailItem> l) { var h = NewMail; if (h != null && l.Count > 0) try { h(l); } catch { } }

        public void Start()
        {
            if (Offline) return;
            foreach (var a in P.Accounts.Where(x => !x.Off)) StartAccount(a);
        }
        public void StartAccount(Account a)
        {
            lock (workers) { if (workers.ContainsKey(a.Id)) return; var w = new Worker(this, a); workers[a.Id] = w; w.Start(); }
        }
        public void StopAccount(string id) { Worker w; lock (workers) { if (!workers.TryGetValue(id, out w)) return; workers.Remove(id); } w.Stop(); }
        public void StopAll() { List<Worker> l; lock (workers) { l = workers.Values.ToList(); workers.Clear(); } foreach (var w in l) w.Stop(); }
        public Worker W(string acc) { lock (workers) { Worker w; return workers.TryGetValue(acc, out w) ? w : null; } }
        public string State(string acc) { var w = W(acc); return w == null ? "off" : w.State; }
        public void SyncNow() { lock (workers) foreach (var w in workers.Values) w.Kick(true); }

        // ---------------- reading
        public BodyView Open(MailItem it, out string error)
        {
            error = null;
            var raw = Store.Body(it);
            if (raw == null)
            {
                var w = W(it.Account); if (w == null) { error = "The account is offline, and this message isn't in the cache yet."; return null; }
                raw = w.Run(c => w.FetchBody(c, it), 60000, out error) as byte[];
                if (raw == null) { if (error == null) error = "Couldn't download the message."; return null; }
            }
            BodyView v;
            try { v = Parse.Body(raw); } catch (Exception e) { error = "The message couldn't be read: " + e.Message; return null; }
            if (!it.Seen) { it.Seen = true; Store.Put(it); Suggestions.NoteOpened(P, it); Do(it.Account, c => { var w = W(it.Account); w.Flag(c, it, MessageFlags.Seen, true); }); }
            return v;
        }

        // ---------------- classification of new mail: category, spam, Screener, rules. Returns what to do on the server.
        public class Plan { public string MoveTo; public bool MarkRead, Star, Archive, Notify, ToJunk; public List<string> Labels = new List<string>(); }
        public bool Badware(string host) { return Trackers.IsBadware(host); }
        public Plan Classify(MailItem m, BodyView body, FolderInfo f)
        {
            var plan = new Plan(); var addr = (m.FromAddr ?? "").ToLowerInvariant();
            bool first; lock (P) first = addr.Length > 0 && !P.Known.Contains(addr);
            m.Category = Sorter.Categorize(m, P);
            if (f.Role == "inbox" && S.SpamFilter && !m.NotSpam)
            {
                var input = new Spam.Input { Mail = m, FirstTime = first };
                lock (P) input.Trusted = P.Trusted.Contains(addr);
                if (body != null && body.Html != null)
                {
                    var clean = Privacy.Sanitize(body.Html, false, null);
                    input.Html = body.Html; input.Links = clean.Links; input.Images = clean.RemoteImages + body.Cid.Count;
                    input.LinkHosts = clean.Links.Where(l => l.Host != null).Select(l => l.Host).Distinct().ToList();
                }
                else if (body != null && body.Text != null) input.LinkHosts = Regex.Matches(body.Text, @"https?://([^/\s:?#]+)").Cast<Match>().Select(x => x.Groups[1].Value).Distinct().ToList();
                var acc = P.Find(m.Account);
                var v = Spam.Judge(P.Bayes, input, acc == null ? "normal" : acc.Sensitivity, Badware);
                m.Spam = Math.Round(v.Score, 3); m.SpamWhy = v.IsSpam ? v.Summary : null;
                if (v.IsSpam) { plan.ToJunk = true; Note("spam: score " + m.Spam.ToString("0.00")); }
            }
            if (!plan.ToJunk && f.Role == "inbox")
            {
                foreach (var r in Rules.For(m, P))
                {
                    r.Hits++;
                    if (!string.IsNullOrEmpty(r.MoveTo)) plan.MoveTo = r.MoveTo;
                    if (!string.IsNullOrEmpty(r.Label)) plan.Labels.Add(r.Label);
                    if (!string.IsNullOrEmpty(r.Category)) m.Category = r.Category;
                    plan.MarkRead |= r.MarkRead; plan.Star |= r.Star; plan.Archive |= r.SkipInbox; plan.Notify |= r.Notify;
                }
                lock (P)
                {
                    if (P.ScreenerNo.Contains(addr)) { plan.Archive = true; m.Screened = false; }
                    else m.Screened = plan.MoveTo == null && !plan.Archive && Screener.Waits(m, P, S.Screener);
                }
            }
            lock (P)
            {
                if (addr.Length > 0 && !P.Known.Contains(addr)) { P.Known.Add(addr); if (P.Known.Count > 20000) P.Known.RemoveAt(0); }
                foreach (var l in plan.Labels) AddLabel(m, l);
            }
            if (f.Role == "inbox") Suggestions.NoteReceived(P, m);
            m.Classified = true;
            return plan;
        }
        public void AddLabel(MailItem m, string label)
        {
            lock (P) { List<string> l; if (!P.Labels.TryGetValue(m.Key, out l)) P.Labels[m.Key] = l = new List<string>(); if (!l.Contains(label)) l.Add(label); }
        }
        public List<string> LabelsOf(MailItem m) { lock (P) { List<string> l; return P.Labels.TryGetValue(m.Key, out l) ? l.ToList() : new List<string>(); } }

        // ---------------- actions (run on the account's worker; the cache is updated at once)
        public void Do(string acc, Action<ImapClient> a) { var w = W(acc); if (w != null) w.Enqueue(a); }
        public string DoWait(string acc, Func<ImapClient, object> f, int ms = 30000)
        {
            var w = W(acc); if (w == null) return "The account isn't connected.";
            string err; w.Run(f, ms, out err); return err;
        }
        public string SetSeen(MailItem m, bool seen) { m.Seen = seen; Store.Put(m); Fire(); Do(m.Account, c => W(m.Account).Flag(c, m, MessageFlags.Seen, seen)); return null; }
        public string SetStar(MailItem m, bool on) { m.Flagged = on; Store.Put(m); Fire(); Do(m.Account, c => W(m.Account).Flag(c, m, MessageFlags.Flagged, on)); return null; }
        public string Move(MailItem m, string role, string folderName, bool wait = false)
        {
            var idx = Store.Index(m.Account); FolderInfo dest;
            lock (Store.Lock) dest = folderName != null ? idx.Folders.FirstOrDefault(x => x.Name == folderName) : idx.Folders.FirstOrDefault(x => x.Role == role);
            if (dest == null && role == "archive") dest = new FolderInfo { Name = "Archive", Role = "archive" };   // created on the server when first used
            if (dest == null) return role == "trash" ? "This account has no Trash folder." : role == "junk" ? "This account has no Junk / Spam folder." : "No folder called " + (folderName ?? role) + ".";
            if (dest.Name == m.Folder) return null;
            if (role == null) Suggestions.NoteMove(P, m.FromAddr, dest.Name);
            Store.Remove(m.Account, x => x.Key == m.Key); Fire();
            Func<ImapClient, object> f = c => { W(m.Account).MoveMsg(c, m, dest); return null; };
            if (wait) return DoWait(m.Account, f);
            Do(m.Account, c => f(c)); return null;
        }
        public string Archive(MailItem m) { return Move(m, "archive", null); }
        public string Trash(MailItem m) { return Move(m, "trash", null); }
        // "Spam" / "Not spam": trains the filter and moves the mail
        public string MarkSpam(MailItem m, bool spam)
        {
            var body = Store.Body(m); BodyView v = null; try { if (body != null) v = Parse.Body(body); } catch { }
            var hosts = v != null && v.Html != null ? Privacy.Sanitize(v.Html, false, null).Links.Where(l => l.Host != null).Select(l => l.Host).Distinct().ToList() : null;
            var tokens = Spam.Tokens(m.Subject, m.Text, m.FromAddr, hosts, Spam.HeaderTokens(m));
            lock (P.Bayes) { Spam.Train(P.Bayes, tokens, spam); }
            m.NotSpam = !spam; m.SpamWhy = spam ? "You marked it as spam." : null;
            if (!spam) lock (P) { var a = (m.FromAddr ?? "").ToLowerInvariant(); if (a.Length > 0 && !P.ScreenerOk.Contains(a)) P.ScreenerOk.Add(a); }
            P.Save();
            return Move(m, spam ? "junk" : "inbox", null);
        }
        public string SaveDraft(string acc, MimeMessage msg, uint replaceUid)
        {
            return DoWait(acc, c => { W(acc).AppendTo(c, "drafts", msg, MessageFlags.Draft | MessageFlags.Seen, replaceUid); return null; });
        }

        // ---------------- sending: everything goes through the local queue (Undo send = held for a few seconds; Send later = held until then)
        public Queued Queue(Account a, MimeMessage msg, DateTime sendAt, bool undoable)
        {
            byte[] raw; using (var ms = new MemoryStream()) { msg.WriteTo(ms); raw = ms.ToArray(); }
            var q = new Queued { Id = Guid.NewGuid().ToString("N").Substring(0, 12), Account = a.Id, SendAt = sendAt.ToString("o"), Mime = Convert.ToBase64String(raw), Undoable = undoable,
                To = string.Join(", ", msg.To.Mailboxes.Select(x => x.Address)), Subject = msg.Subject };
            lock (P) P.Outbox.Add(q);
            P.Save(); Note("queued mail for " + sendAt.ToString("HH:mm:ss")); Fire();
            return q;
        }
        public bool Undo(string id)
        {
            lock (P) { var q = P.Outbox.FirstOrDefault(x => x.Id == id); if (q == null || sending.Contains(id)) return false; P.Outbox.Remove(q); }
            P.Save(); Note("send undone"); Fire(); return true;
        }
        readonly HashSet<string> sending = new HashSet<string>();
        public Func<Account, MimeMessage, string> Sender;   // tests can replace it
        public event Action<Queued, string> Sent;
        // called every second: sends whatever is due (Send later needs the app running - due mail goes out when it next starts)
        public void Tick()
        {
            List<Queued> due;
            lock (P) due = P.Outbox.Where(q => !sending.Contains(q.Id) && DateTime.Parse(q.SendAt, null, System.Globalization.DateTimeStyles.RoundtripKind) <= DateTime.Now && q.Tries < 5 && (q.Error == null || q.Tries < 5)).ToList();
            foreach (var q in due)
            {
                lock (P) sending.Add(q.Id);
                var qq = q;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    string err = null; MimeMessage msg = null; var a = P.Find(qq.Account);
                    try
                    {
                        if (a == null) err = "The account was removed.";
                        else { using (var ms = new MemoryStream(Convert.FromBase64String(qq.Mime))) msg = MimeMessage.Load(ms); err = (Sender ?? SendNow)(a, msg); }
                    }
                    catch (Exception e) { err = e.Message; }
                    lock (P)
                    {
                        sending.Remove(qq.Id);
                        if (err == null) P.Outbox.RemoveAll(x => x.Id == qq.Id);
                        else { qq.Error = err; qq.Tries++; qq.SendAt = DateTime.Now.AddMinutes(Math.Min(30, 2 * qq.Tries)).ToString("o"); }
                    }
                    if (err == null && a != null && msg != null)
                    {
                        lock (P) foreach (var r in msg.To.Mailboxes.Concat(msg.Cc.Mailboxes)) { var x = (r.Address ?? "").ToLowerInvariant(); if (x.Length > 0 && !P.Trusted.Contains(x)) P.Trusted.Add(x); }
                        if (!a.SentSavedByServer) Do(a.Id, c => W(a.Id).AppendTo(c, "sent", msg, MessageFlags.Seen, 0));
                    }
                    Note(err == null ? "sent" : "send failed (try " + qq.Tries + ")");
                    P.Save(); var h = Sent; if (h != null) try { h(qq, err); } catch { }
                    Fire();
                });
            }
            // snoozes that are due come back
            List<Snooze> back; lock (P) { back = P.Snoozed.Where(s => DateTime.Parse(s.Until) <= DateTime.Now).ToList(); foreach (var s in back) P.Snoozed.Remove(s); }
            if (back.Count > 0) { P.Save(); var h = SnoozeDue; if (h != null) try { h(back); } catch { } Fire(); }
        }
        public event Action<List<Snooze>> SnoozeDue;
        public void SnoozeIt(MailItem m, DateTime until) { lock (P) { P.Snoozed.RemoveAll(s => s.Key == m.Key); P.Snoozed.Add(new Snooze { Key = m.Key, Until = until.ToString("s"), Subject = m.Subject }); } P.Save(); Fire(); }
        public bool IsSnoozed(MailItem m) { lock (P) return P.Snoozed.Any(s => s.Key == m.Key); }

        public static SecureSocketOptions Sec(string s, string host)
        {
            if (s == "none")
            {
                if (host != "127.0.0.1" && host != "localhost" && host != "::1") throw new InvalidOperationException("Unencrypted connections are only allowed to this PC (tests).");
                return SecureSocketOptions.None;
            }
            return s == "starttls" ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;
        }
        public string SendNow(Account a, MimeMessage msg)
        {
            using (var smtp = new SmtpClient())
            {
                smtp.Timeout = 60000;
                smtp.Connect(a.SmtpHost, a.SmtpPort, Sec(a.SmtpSecurity, a.SmtpHost));
                var mech = Mechanism(a); if (mech != null) smtp.Authenticate(mech); else smtp.Authenticate(a.UserName ?? a.Email, Secrets.Get("pw:" + a.Id) ?? "");
                smtp.Send(msg); smtp.Disconnect(true);
            }
            return null;
        }
        // OAuth: a fresh access token from the stored refresh token (null for password accounts)
        readonly Dictionary<string, OAuth.Tokens> tokens = new Dictionary<string, OAuth.Tokens>();
        public SaslMechanism Mechanism(Account a)
        {
            var p = OAuth.For(a.Auth); if (p == null) return null;
            OAuth.Tokens t; lock (tokens) tokens.TryGetValue(a.Id, out t);
            if (t == null || t.Access == null || t.Expires <= DateTime.UtcNow)
            {
                var rt = Secrets.Get("rt:" + a.Id); if (rt == null) throw new AuthenticationException("Sign in again (Settings > Accounts) - there's no saved sign-in.");
                t = OAuth.Refresh(p, a.OAuthClientId, Secrets.Get("cs:" + a.Id), rt);
                if (t.Access == null) throw new AuthenticationException("Sign in again: " + t.Error);
                if (t.Refresh != rt) Secrets.Set("rt:" + a.Id, t.Refresh);
                lock (tokens) tokens[a.Id] = t;
            }
            return new SaslMechanismOAuth2(a.UserName ?? a.Email, t.Access);
        }

        // ---- status for the family link: unread count per account only (no subjects, no senders)
        public List<Dictionary<string, object>> UnreadPerAccount()
        {
            var r = new List<Dictionary<string, object>>();
            foreach (var a in P.Accounts)
            {
                int n; lock (Store.Lock) { var idx = Store.Index(a.Id); var inbox = idx.Folders.FirstOrDefault(f => f.Role == "inbox"); n = inbox == null ? 0 : idx.Items.Count(i => i.Folder == inbox.Name && !i.Seen); }
                var w = W(a.Id); var code = w == null ? null : w.AttentionCode; bool chosen = LabelUserChosen(a); var label = chosen ? a.Label.Trim() : GenericLabel(a, P.Accounts.IndexOf(a));
                r.Add(new Dictionary<string, object> { { "account", string.IsNullOrEmpty(a.Label) ? label : a.Label }, { "unread", n }, { "state", Simple(State(a.Id)) },
                    // 1.1: an opaque id (random, never derived from the address), the label with where it came from, and why it needs attention
                    { "id", a.Id }, { "label", label }, { "labelUserChosen", chosen }, { "labelHasAddress", chosen && Attention.LooksLikeAddress(label) },
                    { "needsAttention", code != null }, { "attention", code }, { "attentionText", Attention.Text(code) } });
            }
            return r;
        }
        // the label is the user's own when they typed it (not empty, not the wizard's untouched default such as "Gmail")
        public static bool LabelUserChosen(Account a)
        {
            if (a == null || string.IsNullOrWhiteSpace(a.Label)) return false;
            var p = Preset.Get(a.Preset); var def = p == null || p.Id == "custom" ? "" : p.Name.Split(' ')[0];
            return a.Label.Trim() != def;
        }
        // a label made up for Remote / Hub when the user didn't choose one: the provider's name, or "Account 2" - never the address
        public static string GenericLabel(Account a, int index)
        {
            var p = Preset.Get(a.Preset);
            return p != null && p.Id != "custom" ? p.Name.Split(' ')[0] : "Account " + (index + 1);
        }
        static string Simple(string s) { return s == null ? "off" : s.StartsWith("error") ? "error" : s; }
    }

    // ---------------- why an account needs attention: a short code and plain words. Decided from the exception type and a few
    // well-known server phrases; the server's own text, the address and any mail content are never passed on.
    public static class Attention
    {
        public const string SignIn = "sign-in-failed", AppPassword = "app-password-needed", Unreachable = "server-unreachable", Quota = "quota", Other = "error";
        public static readonly string[] Codes = { SignIn, AppPassword, Unreachable, Quota, Other };
        public static string Classify(Exception e)
        {
            if (e == null) return null;
            var all = new List<Exception>(); for (var x = e; x != null && all.Count < 8; x = x.InnerException) all.Add(x);
            if (e is AggregateException) all.AddRange(((AggregateException)e).InnerExceptions);
            var m = string.Join(" | ", all.Select(x => x.Message ?? "")).ToLowerInvariant();
            if (m.Contains("overquota") || m.Contains("over quota") || m.Contains("quota exceeded") || m.Contains("exceeded quota") || m.Contains("mailbox is full") || m.Contains("mailbox full")
                || m.Contains("[limit]") || m.Contains("too many simultaneous connections") || m.Contains("too many connections") || m.Contains("bandwidth limit") || m.Contains("exceeded command or bandwidth")) return Quota;
            bool auth = all.Any(x => x is AuthenticationException) || m.Contains("authenticationfailed") || m.Contains("invalid credentials");
            if (auth || m.Contains("application-specific password") || m.Contains("app password"))
            {
                if (m.Contains("application-specific password") || m.Contains("app password") || m.Contains("apppassword") || m.Contains("web browser") || m.Contains("weblogin")
                    || m.Contains("basic authentication is disabled") || m.Contains("5.7.9") || m.Contains("5.7.139")) return AppPassword;
                return SignIn;
            }
            if (all.Any(x => x is System.Net.Sockets.SocketException || x is TimeoutException || x is IOException || x is SslHandshakeException || x is ServiceNotConnectedException || x is ImapProtocolException)
                || m.Contains("didn't answer in time") || m.Contains("no such host") || m.Contains("actively refused") || m.Contains("timed out")) return Unreachable;
            return Other;
        }
        public static string Text(string code)
        {
            switch (code)
            {
                case null: return null;
                case SignIn: return "The mail server didn't accept the sign-in. Check the password in Settings > Accounts, or sign in again.";
                case AppPassword: return "The provider wants an app password (or its own sign-in) instead of your normal password. Create one in your account's security settings and paste it in Settings > Accounts.";
                case Unreachable: return "Couldn't reach the mail server (offline, a wrong server name or a firewall). Hallow Email keeps trying.";
                case Quota: return "The provider says a limit was reached: the mailbox is full or there were too many connections. Free up space or wait; Hallow Email keeps trying.";
                default: return "Something went wrong syncing this account. Hallow Email keeps trying; Settings > Accounts > Edit > Test shows more.";
            }
        }
        public static bool LooksLikeAddress(string s) { return s != null && System.Text.RegularExpressions.Regex.IsMatch(s, @"[^@\s]+@[^@\s]+\.[^@\s]+"); }
    }

    // ---------------- one account's IMAP connection
    public class Worker
    {
        readonly Engine E; public readonly Account A;
        readonly BlockingCollection<Action<ImapClient>> q = new BlockingCollection<Action<ImapClient>>();
        CancellationTokenSource stop = new CancellationTokenSource(), idleDone, wait;
        Thread t; public volatile string State = "starting"; public volatile string AttentionCode; /* null while fine (Attention.Codes) */ bool full = true; DateTime lastFull = DateTime.MinValue;
        public bool IdleSupported; public int Idles;
        public Worker(Engine e, Account a) { E = e; A = a; }
        public void Start() { t = new Thread(Loop) { IsBackground = true, Name = "imap " + A.Id, Priority = ThreadPriority.BelowNormal }; t.Start(); }
        public void Stop() { stop.Cancel(); Kick(false); }
        public void Kick(bool fullSync) { if (fullSync) full = true; var d = idleDone; if (d != null) try { d.Cancel(); } catch { } var w = wait; if (w != null) try { w.Cancel(); } catch { } }
        public void Enqueue(Action<ImapClient> a) { q.Add(a); Kick(false); }
        // runs f on the worker and waits for it
        public object Run(Func<ImapClient, object> f, int ms, out string error)
        {
            object r = null; string err = null; var done = new ManualResetEventSlim();
            Enqueue(c => { try { r = f(c); } catch (Exception e) { err = Clean(e.Message); } finally { done.Set(); } });
            if (!done.Wait(ms)) { error = "The server didn't answer in time."; return null; }
            error = err; return r;
        }
        string Clean(string m) { var pw = Secrets.Get("pw:" + A.Id); return pw != null && pw.Length >= 4 ? (m ?? "").Replace(pw, "<secret>") : m; }

        void Loop()
        {
            int fails = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using (var c = new ImapClient())
                    {
                        c.Timeout = 60000; State = "connecting";
                        c.Connect(A.ImapHost, A.ImapPort, Engine.Sec(A.ImapSecurity, A.ImapHost), stop.Token);
                        var mech = E.Mechanism(A);
                        if (mech != null) c.Authenticate(mech, stop.Token); else c.Authenticate(A.UserName ?? A.Email, Secrets.Get("pw:" + A.Id) ?? "", stop.Token);
                        E.Note(A.Id + ": signed in"); fails = 0; AttentionCode = null;
                        IdleSupported = c.Capabilities.Contains(ImapCapability.Idle);
                        Folders(c); full = true;
                        while (!stop.IsCancellationRequested && c.IsConnected)
                        {
                            Action<ImapClient> a;
                            while (q.TryTake(out a)) { try { a(c); } catch (Exception e) { E.Note(A.Id + ": action failed: " + Clean(e.Message)); if (!c.IsConnected) throw; } }
                            if (full || (DateTime.Now - lastFull).TotalMinutes >= 5) { full = false; State = "syncing"; SyncAll(c); lastFull = DateTime.Now; State = "synced"; E.Fire(); continue; }
                            if (q.Count > 0) continue;
                            if (IdleSupported) IdleInbox(c); else Pause(60000);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    fails++; AttentionCode = Attention.Classify(e); State = "error: " + Clean(e is AuthenticationException ? "the server didn't accept the sign-in (" + e.Message + ")" : e.Message);
                    E.Note(A.Id + ": " + State); E.Fire();
                    // anything that was waiting gets an answer (the error)
                    Action<ImapClient> a; while (q.TryTake(out a)) { try { a(null); } catch { } }
                    Pause(Math.Min(300000, 2000 * fails * fails));
                }
            }
            State = "off";
        }
        void Pause(int ms) { wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); try { wait.Token.WaitHandle.WaitOne(ms); } finally { wait = null; } }

        // IMAP IDLE on the inbox: the server pushes new mail; we stop idling for actions, a resync every 5 min, or every 9 min anyway
        void IdleInbox(ImapClient c)
        {
            var inbox = c.Inbox; if (!inbox.IsOpen) inbox.Open(FolderAccess.ReadOnly);
            bool changed = false;
            EventHandler<EventArgs> h = (s, e) => { changed = true; var d = idleDone; if (d != null) try { d.Cancel(); } catch { } };
            inbox.CountChanged += h;
            idleDone = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(0.1, Math.Min(9, 5 - (DateTime.Now - lastFull).TotalMinutes))));
            try { State = "idle"; Idles++; if (q.Count == 0) c.Idle(idleDone.Token, stop.Token); }
            finally { inbox.CountChanged -= h; idleDone = null; }
            if (changed) { E.Note(A.Id + ": new mail pushed"); State = "syncing"; SyncFolder(c, Info(c.Inbox), true); State = "synced"; E.Fire(); }
        }

        // ---- folders
        public static string RoleOf(IMailFolder f)
        {
            var a = f.Attributes; var n = f.FullName.ToLowerInvariant(); var s = f.Name.ToLowerInvariant();
            if (a.HasFlag(FolderAttributes.Inbox) || n == "inbox") return "inbox";
            if (a.HasFlag(FolderAttributes.Sent) || s == "sent" || s == "sent items" || s == "sent mail" || s == "sent messages") return "sent";
            if (a.HasFlag(FolderAttributes.Drafts) || s == "drafts") return "drafts";
            if (a.HasFlag(FolderAttributes.Junk) || s == "junk" || s == "spam" || s == "junk e-mail" || s == "junk email" || s == "bulk mail") return "junk";
            if (a.HasFlag(FolderAttributes.Trash) || s == "trash" || s == "deleted items" || s == "deleted messages" || s == "bin") return "trash";
            if (a.HasFlag(FolderAttributes.All)) return "all";
            if (a.HasFlag(FolderAttributes.Archive) || s == "archive" || s == "archives") return "archive";
            return null;
        }
        List<IMailFolder> remote = new List<IMailFolder>();
        void Folders(ImapClient c)
        {
            var ns = c.PersonalNamespaces.Count > 0 ? c.PersonalNamespaces[0] : new FolderNamespace('/', "");
            var all = c.GetFolders(ns, false, stop.Token).Where(f => f.CanOpen || f.Attributes.HasFlag(FolderAttributes.Inbox)).ToList();
            if (!all.Any(f => RoleOf(f) == "inbox")) all.Insert(0, c.Inbox);
            remote = all;
            var idx = E.Store.Index(A.Id);
            lock (E.Store.Lock)
            {
                var old = idx.Folders.ToDictionary(f => f.Name, f => f);
                idx.Folders = all.Select(f => { FolderInfo o; old.TryGetValue(f.FullName, out o); return new FolderInfo { Name = f.FullName, Role = RoleOf(f), UidValidity = o == null ? 0 : o.UidValidity, Unread = o == null ? 0 : o.Unread, Count = o == null ? 0 : o.Count }; }).ToList();
                // Gmail's "All Mail" works as the archive when there's no Archive folder
                if (!idx.Folders.Any(f => f.Role == "archive")) { var allm = idx.Folders.FirstOrDefault(f => f.Role == "all"); if (allm != null) allm.Role = "archive"; }
                var names = new HashSet<string>(idx.Folders.Select(f => f.Name));
                idx.Items.RemoveAll(i => !names.Contains(i.Folder));
            }
        }
        FolderInfo Info(IMailFolder f) { var idx = E.Store.Index(A.Id); lock (E.Store.Lock) return idx.Folders.FirstOrDefault(x => x.Name == f.FullName) ?? new FolderInfo { Name = f.FullName, Role = RoleOf(f) }; }
        IMailFolder Remote(ImapClient c, string name)
        {
            var f = remote.FirstOrDefault(x => x.FullName == name);
            return f ?? c.GetFolder(name, stop.Token);
        }
        void SyncAll(ImapClient c)
        {
            foreach (var f in remote.ToList())
            {
                if (stop.IsCancellationRequested) return;
                var info = Info(f); if (info.Role == "trash") continue;
                if (f.Attributes.HasFlag(FolderAttributes.All) || f.Attributes.HasFlag(FolderAttributes.Flagged) || f.Attributes.HasFlag(FolderAttributes.Important)) continue;   // Gmail's All Mail / Starred / Important are views of the same mail
                try { SyncFolder(c, info, false); } catch (Exception e) { if (!c.IsConnected) throw; E.Note(A.Id + ": " + info.Name + ": " + Clean(e.Message)); }
            }
            lock (E.Store.Lock) { var idx = E.Store.Index(A.Id); idx.Synced = DateTime.Now.ToString("s"); Threads.Assign(idx.Items); }
            E.Store.Save(A.Id); E.P.Save();
        }
        static readonly string[] HeaderFields = null;
        public void SyncFolder(ImapClient c, FolderInfo info, bool save)
        {
            var f = Remote(c, info.Name);
            if (f.IsOpen && f.Access != FolderAccess.None) { } else f.Open(FolderAccess.ReadOnly, stop.Token);
            var idx = E.Store.Index(A.Id);
            if (info.UidValidity != 0 && f.UidValidity != info.UidValidity) { E.Store.Remove(A.Id, i => i.Folder == info.Name); E.Note(A.Id + ": " + info.Name + " was reset on the server"); }
            bool initial = info.UidValidity == 0 || f.UidValidity != info.UidValidity;   // the first look at this folder: sort, but don't move or notify
            info.UidValidity = f.UidValidity;
            var uids = f.Count == 0 ? new List<UniqueId>() : f.Search(SearchQuery.All, stop.Token).ToList();
            var serverSet = new HashSet<uint>(uids.Select(u => u.Id));
            Dictionary<uint, MailItem> have;
            lock (E.Store.Lock) have = idx.Items.Where(i => i.Folder == info.Name).ToDictionary(i => i.Uid, i => i);
            // gone on the server
            var gone = have.Keys.Where(u => !serverSet.Contains(u)).ToList();
            if (gone.Count > 0) { var g = new HashSet<uint>(gone); E.Store.Remove(A.Id, i => i.Folder == info.Name && g.Contains(i.Uid)); }
            // new ones (the newest MaxPerFolder)
            var fresh = uids.Where(u => !have.ContainsKey(u.Id)).OrderByDescending(u => u.Id).Take(Math.Max(0, E.MaxPerFolder - (have.Count - gone.Count))).OrderBy(u => u.Id).ToList();
            var added = new List<MailItem>();
            if (fresh.Count > 0)
            {
                foreach (var batch in Batches(fresh, 100))
                    foreach (var s in f.Fetch(batch, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.Size | MessageSummaryItems.Headers, stop.Token))
                    {
                        var it = Parse.Item(A.Id, info, s.UniqueId, s.Flags, s.Headers, s.Size ?? 0, s.InternalDate);
                        added.Add(it);
                    }
            }
            // flags of the ones we have (newest 500)
            var keep = uids.Where(u => have.ContainsKey(u.Id)).OrderByDescending(u => u.Id).Take(500).ToList();
            if (keep.Count > 0)
                foreach (var s in f.Fetch(keep, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, stop.Token))
                {
                    MailItem it; if (!have.TryGetValue(s.UniqueId.Id, out it) || !s.Flags.HasValue) continue;
                    var fl = s.Flags.Value; it.Seen = fl.HasFlag(MessageFlags.Seen); it.Flagged = fl.HasFlag(MessageFlags.Flagged); it.Answered = fl.HasFlag(MessageFlags.Answered);
                }
            // bodies for the newest new mail (search, preview, spam filter); classify inbox mail
            var notify = new List<MailItem>(); var plans = new List<KeyValuePair<MailItem, Engine.Plan>>();
            int bodies = 0;
            foreach (var it in added.OrderByDescending(x => x.Uid))
            {
                BodyView v = null;
                if (bodies < E.PrefetchBodies && it.Size < 5 * 1024 * 1024)
                {
                    try { var raw = FetchBodyFrom(f, it); if (raw != null) { v = Parse.Body(raw); Fill(it, v); bodies++; } } catch (Exception e) { if (!c.IsConnected) throw; E.Note(A.Id + ": body: " + Clean(e.Message)); }
                }
                if (info.Role == "inbox")
                {
                    var plan = E.Classify(it, v, info);
                    if (initial) { it.Screened = false; if (plan.ToJunk) it.SpamWhy = null; E.Store.Put(it); continue; }
                    plans.Add(new KeyValuePair<MailItem, Engine.Plan>(it, plan));
                    if (!plan.ToJunk && plan.MoveTo == null && !plan.Archive && !it.Screened && !it.Seen && !plan.MarkRead && it.Category == "people") notify.Add(it);
                    else if (plan.Notify) notify.Add(it);
                }
                else { it.Category = Sorter.Categorize(it, E.P); lock (E.P) { var a = (it.FromAddr ?? "").ToLowerInvariant(); if (a.Length > 0 && !E.P.Known.Contains(a)) E.P.Known.Add(a); if (info.Role == "sent") AddTrusted(it); } }
                E.Store.Put(it);
            }
            lock (E.Store.Lock)
            {
                var items = idx.Items.Where(i => i.Folder == info.Name).ToList();
                info.Count = items.Count; info.Unread = items.Count(i => !i.Seen);
            }
            // what the rules / spam filter decided, on the server
            foreach (var kv in plans) Apply(c, kv.Key, kv.Value);
            if (added.Count > 0 || gone.Count > 0) E.Note(A.Id + ": " + info.Name + " +" + added.Count + " -" + gone.Count);
            if (save) { lock (E.Store.Lock) Threads.Assign(idx.Items); E.Store.Save(A.Id); E.P.Save(); }
            if (notify.Count > 0) E.FireNew(notify);
        }
        void AddTrusted(MailItem sent)
        {
            InternetAddressList l;
            foreach (var h in new[] { sent.To, sent.Cc }) if (h != null && InternetAddressList.TryParse(h, out l)) foreach (var m in l.Mailboxes) { var a = (m.Address ?? "").ToLowerInvariant(); if (a.Length > 0 && !E.P.Trusted.Contains(a)) E.P.Trusted.Add(a); }
        }
        public static void Fill(MailItem it, BodyView v)
        {
            it.Text = Parse.Clip(v.Text, 4000); it.Preview = Parse.Preview(v.Text); it.HasAttachments = v.Attachments.Count > 0; it.BodyCached = true;
        }
        void Apply(ImapClient c, MailItem it, Engine.Plan p)
        {
            try
            {
                if (p.MarkRead) Flag(c, it, MessageFlags.Seen, true);
                if (p.Star) Flag(c, it, MessageFlags.Flagged, true);
                if (p.Labels.Count > 0) Keywords(c, it, p.Labels);
                FolderInfo dest = null; var idx = E.Store.Index(A.Id);
                lock (E.Store.Lock)
                {
                    if (p.ToJunk) dest = idx.Folders.FirstOrDefault(f => f.Role == "junk");
                    else if (p.MoveTo != null) dest = idx.Folders.FirstOrDefault(f => f.Name.Equals(p.MoveTo, StringComparison.OrdinalIgnoreCase) || f.Short.Equals(p.MoveTo, StringComparison.OrdinalIgnoreCase));
                    else if (p.Archive) dest = idx.Folders.FirstOrDefault(f => f.Role == "archive") ?? new FolderInfo { Name = "Archive", Role = "archive" };
                }
                if (p.ToJunk && dest == null) { E.Note(A.Id + ": no Junk folder - spam stays in the inbox, marked"); return; }
                if (dest != null && dest.Name != it.Folder) { E.Store.Remove(A.Id, x => x.Key == it.Key); MoveMsg(c, it, dest); }
            }
            catch (Exception e) { if (!c.IsConnected) throw; E.Note(A.Id + ": rule action failed: " + Clean(e.Message)); }
        }
        static IEnumerable<List<UniqueId>> Batches(List<UniqueId> l, int n) { for (int i = 0; i < l.Count; i += n) yield return l.Skip(i).Take(n).ToList(); }

        // ---- the actions (worker thread). c == null means the connection failed: throw so the waiter gets an error.
        static void Need(ImapClient c) { if (c == null) throw new InvalidOperationException("The account isn't connected right now."); }
        IMailFolder OpenRw(ImapClient c, string name)
        {
            var f = Remote(c, name);
            if (!f.IsOpen || f.Access != FolderAccess.ReadWrite) f.Open(FolderAccess.ReadWrite, stop.Token);
            return f;
        }
        public byte[] FetchBody(ImapClient c, MailItem it)
        {
            Need(c);
            var f = Remote(c, it.Folder); if (!f.IsOpen) f.Open(FolderAccess.ReadOnly, stop.Token);
            var raw = FetchBodyFrom(f, it);
            try { var v = Parse.Body(raw); Fill(it, v); E.Store.Put(it); } catch { }
            return raw;
        }
        byte[] FetchBodyFrom(IMailFolder f, MailItem it)
        {
            var m = f.GetMessage(new UniqueId(it.UidValidity, it.Uid), stop.Token);
            byte[] raw; using (var ms = new MemoryStream()) { m.WriteTo(ms); raw = ms.ToArray(); }
            E.Store.PutBody(it, raw); return raw;
        }
        public void Flag(ImapClient c, MailItem it, MessageFlags fl, bool on)
        {
            Need(c); var f = OpenRw(c, it.Folder); var u = new UniqueId(it.UidValidity, it.Uid);
            if (on) f.AddFlags(u, fl, true, stop.Token); else f.RemoveFlags(u, fl, true, stop.Token);
        }
        public void Keywords(ImapClient c, MailItem it, List<string> labels)
        {
            Need(c); var f = OpenRw(c, it.Folder);
            if (!f.PermanentFlags.HasFlag(MessageFlags.UserDefined)) return;   // the server doesn't keep keywords: local labels only
            var kw = new HashSet<string>(labels.Select(l => Regex.Replace(l, @"[^A-Za-z0-9_-]", "_")));
            f.AddFlags(new UniqueId(it.UidValidity, it.Uid), MessageFlags.None, kw, true, stop.Token);
        }
        public void MoveMsg(ImapClient c, MailItem it, FolderInfo dest)
        {
            Need(c);
            var target = remote.FirstOrDefault(x => x.FullName == dest.Name);
            if (target == null)
            {
                var ns = c.PersonalNamespaces.Count > 0 ? c.PersonalNamespaces[0] : new FolderNamespace('/', "");
                var root = c.GetFolder(ns);
                target = root.Create(dest.Name, true, stop.Token); remote.Add(target);
                lock (E.Store.Lock) { var idx = E.Store.Index(A.Id); if (!idx.Folders.Any(x => x.Name == target.FullName)) idx.Folders.Add(new FolderInfo { Name = target.FullName, Role = dest.Role }); }
            }
            var f = OpenRw(c, it.Folder);
            f.MoveTo(new UniqueId(it.UidValidity, it.Uid), target, stop.Token);
            E.Store.DropBody(it); full = true;
        }
        public void DeleteFrom(ImapClient c, string role, uint uid)
        {
            Need(c);
            FolderInfo fi; lock (E.Store.Lock) fi = E.Store.Index(A.Id).Folders.FirstOrDefault(x => x.Role == role);
            if (fi == null || uid == 0) return;
            var rw = OpenRw(c, fi.Name); var u = new UniqueId(rw.UidValidity, uid);
            rw.AddFlags(u, MessageFlags.Deleted, true, stop.Token);
            if (c.Capabilities.Contains(ImapCapability.UidPlus)) rw.Expunge(new[] { u }, stop.Token); else rw.Expunge(stop.Token);
            E.Store.Remove(A.Id, x => x.Folder == fi.Name && x.Uid == uid); full = true;
        }
        public void AppendTo(ImapClient c, string role, MimeMessage msg, MessageFlags flags, uint replaceUid)
        {
            Need(c);
            FolderInfo fi; lock (E.Store.Lock) fi = E.Store.Index(A.Id).Folders.FirstOrDefault(x => x.Role == role);
            if (fi == null) throw new InvalidOperationException("This account has no " + (role == "drafts" ? "Drafts" : "Sent") + " folder.");
            var f = Remote(c, fi.Name);
            f.Append(msg, flags, stop.Token);
            if (replaceUid != 0)
            {
                var rw = OpenRw(c, fi.Name); var u = new UniqueId(rw.UidValidity, replaceUid);
                rw.AddFlags(u, MessageFlags.Deleted, true, stop.Token);
                if (c.Capabilities.Contains(ImapCapability.UidPlus)) rw.Expunge(new[] { u }, stop.Token); else rw.Expunge(stop.Token);
            }
            full = true;
        }
    }
}
