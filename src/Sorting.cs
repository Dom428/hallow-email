// Hallow Email - smart sorting (People / Newsletters / Receipts / Notifications), rules, suggestions, the Screener and unsubscribe.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HallowEmail
{
    public static class Sorter
    {
        public static readonly string[] Categories = { "people", "newsletters", "receipts", "notifications" };
        public static string Title(string c) { switch (c) { case "newsletters": return "Newsletters"; case "receipts": return "Receipts"; case "notifications": return "Notifications"; default: return "People"; } }
        static readonly Regex ReceiptWords = new Regex(@"\b(receipt|invoice|order (confirmation|#|no\.?|number)|your order|order\s+\d{3,}|purchase|payment (received|confirmation)|has shipped|shipping confirmation|out for delivery|delivered|booking (confirmation|reference)|e-?ticket|tax invoice|subscription (renewed|confirmation)|refund|billing statement|your bill)\b", RegexOptions.IgnoreCase);
        static readonly Regex NotifyFrom = new Regex(@"^(no-?reply|do-?not-?reply|donotreply|notifications?|notify|alerts?|security|mailer-daemon|postmaster|automated|system|updates?|info-noreply|account(s)?-noreply)[@+._-]", RegexOptions.IgnoreCase);
        static readonly Regex NotifyWords = new Regex(@"\b(security alert|sign-?in|new login|verification code|password reset|two-factor|one-time code|your code|mentioned you|commented|replied to your|new follower|invited you|reminder:|was updated|build (failed|succeeded)|pull request)\b", RegexOptions.IgnoreCase);

        public static string Categorize(MailItem m, Profile p)
        {
            var o = Override(m.FromAddr, p); if (o != null) return o;
            var subj = m.Subject ?? ""; var from = (m.FromAddr ?? "").ToLowerInvariant();
            bool list = !string.IsNullOrEmpty(m.ListUnsubscribe) || !string.IsNullOrEmpty(m.ListId) || Regex.IsMatch(m.Precedence ?? "", "bulk|list", RegexOptions.IgnoreCase);
            bool auto = Regex.IsMatch(m.AutoSubmitted ?? "", "auto-(generated|replied)", RegexOptions.IgnoreCase) || NotifyFrom.IsMatch(from);
            if (ReceiptWords.IsMatch(subj)) return "receipts";
            if (auto && NotifyWords.IsMatch(subj)) return "notifications";
            if (list) return "newsletters";
            if (auto || NotifyWords.IsMatch(subj)) return "notifications";
            return "people";
        }
        // the user moved a sender: address, or the whole @domain
        public static string Override(string addr, Profile p)
        {
            if (p == null || string.IsNullOrEmpty(addr)) return null;
            string c; var a = addr.ToLowerInvariant();
            if (p.SenderCategory.TryGetValue(a, out c)) return c;
            if (p.SenderCategory.TryGetValue("@" + Domains.Of(a), out c)) return c;
            return null;
        }
    }

    // ---------------- rules
    public static class Rules
    {
        public static bool Matches(Rule r, MailItem m)
        {
            if (!r.On) return false;
            if (!string.IsNullOrEmpty(r.Account) && r.Account != m.Account) return false;
            bool any = false;
            if (!string.IsNullOrEmpty(r.From)) { any = true; if (!Has(m.FromAddr, r.From) && !Has(m.FromName, r.From)) return false; }
            if (!string.IsNullOrEmpty(r.Subject)) { any = true; if (!Has(m.Subject, r.Subject)) return false; }
            if (!string.IsNullOrEmpty(r.To)) { any = true; if (!Has(m.To, r.To) && !Has(m.Cc, r.To)) return false; }
            if (!string.IsNullOrEmpty(r.ListId)) { any = true; if (!Has(m.ListId, r.ListId)) return false; }
            if (r.HasAttachment.HasValue) { any = true; if (m.HasAttachments != r.HasAttachment.Value) return false; }
            return any;   // a rule with no conditions never matches everything by accident
        }
        static bool Has(string s, string w) { return s != null && s.IndexOf(w.Trim(), StringComparison.OrdinalIgnoreCase) >= 0; }
        public static List<Rule> For(MailItem m, Profile p) { return p.Rules.Where(r => Matches(r, m)).ToList(); }
        public static Rule FromMail(MailItem m)
        {
            return new Rule { Id = Guid.NewGuid().ToString("N").Substring(0, 10), Name = "Mail from " + (m.Sender ?? m.FromAddr), From = m.FromAddr, Account = null };
        }
        public static string Describe(Rule r)
        {
            var c = new List<string>(); var a = new List<string>();
            if (!string.IsNullOrEmpty(r.From)) c.Add("from contains \"" + r.From + "\"");
            if (!string.IsNullOrEmpty(r.Subject)) c.Add("subject contains \"" + r.Subject + "\"");
            if (!string.IsNullOrEmpty(r.To)) c.Add("to contains \"" + r.To + "\"");
            if (!string.IsNullOrEmpty(r.ListId)) c.Add("list is \"" + r.ListId + "\"");
            if (r.HasAttachment == true) c.Add("has an attachment"); if (r.HasAttachment == false) c.Add("has no attachment");
            if (!string.IsNullOrEmpty(r.MoveTo)) a.Add("move to " + r.MoveTo);
            if (!string.IsNullOrEmpty(r.Label)) a.Add("label \"" + r.Label + "\"");
            if (!string.IsNullOrEmpty(r.Category)) a.Add("show in " + Sorter.Title(r.Category));
            if (r.MarkRead) a.Add("mark read"); if (r.Star) a.Add("star"); if (r.SkipInbox) a.Add("skip the inbox (archive)"); if (r.Notify) a.Add("notify me");
            return "If " + (c.Count == 0 ? "(nothing)" : string.Join(" and ", c)) + ": " + (a.Count == 0 ? "(nothing)" : string.Join(", ", a));
        }
    }

    public class Suggestion { public string Id; public string Kind; public string Text; public string Sender; public string Folder; }
    public static class Suggestions
    {
        // "You always move mail from X to Y" (3+ moves, 80%+ to the same folder, no rule yet) and "You never open these newsletters"
        public static List<Suggestion> Make(Profile p, IEnumerable<MailItem> cache)
        {
            var r = new List<Suggestion>();
            foreach (var g in p.Moves.Where(x => !string.IsNullOrEmpty(x.Sender)).GroupBy(x => x.Sender.ToLowerInvariant()))
            {
                var top = g.GroupBy(x => x.Folder).OrderByDescending(x => x.Count()).First();
                if (top.Count() < 3 || top.Count() < 0.8 * g.Count()) continue;
                if (p.Rules.Any(x => !string.IsNullOrEmpty(x.From) && g.Key.IndexOf(x.From, StringComparison.OrdinalIgnoreCase) >= 0 && x.MoveTo == top.Key)) continue;
                var id = "move:" + g.Key + ">" + top.Key; if (p.Dismissed.Contains(id)) continue;
                r.Add(new Suggestion { Id = id, Kind = "move", Sender = g.Key, Folder = top.Key, Text = "You always move mail from " + g.Key + " to " + top.Key + " - make a rule?" });
            }
            foreach (var kv in p.Opens)
            {
                if (kv.Value == null || kv.Value.Length < 2 || kv.Value[0] < 5 || kv.Value[1] > 0) continue;
                var id = "never:" + kv.Key; if (p.Dismissed.Contains(id)) continue;
                if (p.Rules.Any(x => !string.IsNullOrEmpty(x.From) && kv.Key.IndexOf(x.From, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                r.Add(new Suggestion { Id = id, Kind = "never", Sender = kv.Key, Text = "You never open mail from " + kv.Key + " (" + kv.Value[0] + " so far) - unsubscribe or archive it automatically?" });
            }
            return r;
        }
        public static void NoteMove(Profile p, string sender, string folder)
        {
            if (string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(folder)) return;
            lock (p) { p.Moves.Add(new MoveNote { Sender = sender.ToLowerInvariant(), Folder = folder, When = DateTime.Now.ToString("s") }); if (p.Moves.Count > 400) p.Moves.RemoveRange(0, p.Moves.Count - 400); }
        }
        public static void NoteReceived(Profile p, MailItem m) { if (m.Category != "newsletters" || string.IsNullOrEmpty(m.FromAddr)) return; lock (p) { var k = m.FromAddr.ToLowerInvariant(); int[] v; if (!p.Opens.TryGetValue(k, out v)) p.Opens[k] = v = new int[2]; v[0]++; } }
        public static void NoteOpened(Profile p, MailItem m) { if (m.Category != "newsletters" || string.IsNullOrEmpty(m.FromAddr)) return; lock (p) { int[] v; if (p.Opens.TryGetValue(m.FromAddr.ToLowerInvariant(), out v)) v[1]++; } }
    }

    // ---------------- the Screener: first-time senders wait until you say yes (people you've written to never wait)
    public static class Screener
    {
        public static bool Waits(MailItem m, Profile p, bool on)
        {
            if (!on || string.IsNullOrEmpty(m.FromAddr)) return false;
            var a = m.FromAddr.ToLowerInvariant();
            if (p.Trusted.Contains(a) || p.ScreenerOk.Contains(a)) return false;
            return true;
        }
        // turning it on: everyone who has already written is let through, so only new senders wait
        public static void TurnOn(Profile p) { lock (p) foreach (var k in p.Known) if (!p.ScreenerOk.Contains(k)) p.ScreenerOk.Add(k); }
    }

    // ---------------- unsubscribe (List-Unsubscribe / RFC 8058 one-click). Nothing is sent without asking.
    public class UnsubPlan { public string Kind; public string Url; public string MailTo; public string MailSubject; public string MailBody; public string Describe; }   // Kind: oneclick, mailto, web, none
    public static class Unsubscribe
    {
        public static UnsubPlan Plan(string header, string post)
        {
            var urls = Regex.Matches(header ?? "", "<([^>]+)>").Cast<Match>().Select(m => m.Groups[1].Value.Trim()).ToList();
            if (urls.Count == 0 && !string.IsNullOrWhiteSpace(header)) urls = header.Split(',').Select(x => x.Trim()).ToList();
            var https = urls.FirstOrDefault(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            var http = urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
            var mail = urls.FirstOrDefault(u => u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));
            bool oneClick = (post ?? "").Replace(" ", "").IndexOf("List-Unsubscribe=One-Click", StringComparison.OrdinalIgnoreCase) >= 0;
            if (https != null && oneClick) return new UnsubPlan { Kind = "oneclick", Url = https, Describe = "One click: Hallow Email tells " + Privacy.HostOf(https) + " to unsubscribe you (a single web request, no page opens)." };
            if (mail != null)
            {
                var p = new UnsubPlan { Kind = "mailto" }; var rest = mail.Substring(7); var q = rest.IndexOf('?');
                p.MailTo = Uri.UnescapeDataString(q < 0 ? rest : rest.Substring(0, q));
                if (q >= 0) foreach (var kv in rest.Substring(q + 1).Split('&').Select(x => x.Split(new[] { '=' }, 2)).Where(x => x.Length == 2))
                    {
                        if (kv[0].Equals("subject", StringComparison.OrdinalIgnoreCase)) p.MailSubject = Uri.UnescapeDataString(kv[1].Replace('+', ' '));
                        if (kv[0].Equals("body", StringComparison.OrdinalIgnoreCase)) p.MailBody = Uri.UnescapeDataString(kv[1].Replace('+', ' '));
                    }
                if (string.IsNullOrEmpty(p.MailSubject)) p.MailSubject = "unsubscribe";
                p.Describe = "Sends a short \"" + p.MailSubject + "\" email to " + p.MailTo + " from your account.";
                return p;
            }
            if (https != null || http != null) { var u = https ?? http; return new UnsubPlan { Kind = "web", Url = u, Describe = "Opens the sender's unsubscribe page (" + Privacy.HostOf(u) + ") in your browser." }; }
            return new UnsubPlan { Kind = "none", Describe = "This sender doesn't offer an unsubscribe link in its headers." };
        }
        // RFC 8058: POST the URL with "List-Unsubscribe=One-Click", no cookies, no redirects followed to other hosts
        public static string OneClick(string url)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST"; req.ContentType = "application/x-www-form-urlencoded"; req.UserAgent = "HallowEmail"; req.AllowAutoRedirect = false; req.Timeout = 15000;
                var body = Encoding.ASCII.GetBytes("List-Unsubscribe=One-Click");
                req.ContentLength = body.Length; using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (var resp = (HttpWebResponse)req.GetResponse()) { var c = (int)resp.StatusCode; return c >= 200 && c < 400 ? null : "The sender answered " + c + "."; }
            }
            catch (WebException e) { var r = e.Response as HttpWebResponse; return r != null ? "The sender answered " + (int)r.StatusCode + "." : "Couldn't reach the sender: " + e.Message; }
            catch (Exception e) { return e.Message; }
        }
        public class Row { public string Sender; public string Name; public int Count; public int Opened; public string Last; public string Header; public string Post; public string Account; public string Kind; }
        // newsletters by volume (from the cache)
        public static List<Row> Finder(IEnumerable<MailItem> items)
        {
            return items.Where(m => !string.IsNullOrEmpty(m.ListUnsubscribe) || m.Category == "newsletters")
                .GroupBy(m => (m.FromAddr ?? "").ToLowerInvariant()).Where(g => g.Key.Length > 0)
                .Select(g =>
                {
                    var newest = g.OrderByDescending(x => x.When).First(); var withHeader = g.Where(x => !string.IsNullOrEmpty(x.ListUnsubscribe)).OrderByDescending(x => x.When).FirstOrDefault();
                    return new Row { Sender = g.Key, Name = newest.Sender, Count = g.Count(), Opened = g.Count(x => x.Seen), Last = newest.Date, Header = withHeader == null ? null : withHeader.ListUnsubscribe, Post = withHeader == null ? null : withHeader.ListUnsubscribePost, Account = newest.Account, Kind = withHeader == null ? "none" : Plan(withHeader.ListUnsubscribe, withHeader.ListUnsubscribePost).Kind };
                }).OrderByDescending(r => r.Count).ThenBy(r => r.Sender).ToList();
        }
    }
}
