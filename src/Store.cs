// Hallow Email - the local mail cache (encrypted, Vault.cs) and the message model.
// cache\<account id>\index.bin : folders + message summaries (headers we need, flags, a text preview, the first ~4 KB of body text for search)
// cache\<account id>\m\<hash>.bin : whole messages (RFC 822) that were opened or pre-fetched
// The cache is a copy: the server keeps the mail. "Forget this account" deletes its cache folder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HallowEmail
{
    [DataContract]
    public class FolderInfo
    {
        [DataMember] public string Name;        // full name on the server ("INBOX", "[Gmail]/Sent Mail")
        [DataMember] public string Role;        // inbox, sent, drafts, junk, trash, archive, all, or null
        [DataMember] public uint UidValidity; [DataMember] public uint UidNext;
        [DataMember] public int Unread; [DataMember] public int Count;
        public string Short { get { var i = Name.LastIndexOfAny(new[] { '/', '.' }); return Name.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? "Inbox" : i >= 0 && i < Name.Length - 1 ? Name.Substring(i + 1) : Name; } }
    }

    [DataContract]
    public class MailItem
    {
        [DataMember] public string Account; [DataMember] public string Folder; [DataMember] public uint UidValidity; [DataMember] public uint Uid;
        [DataMember] public string MessageId; [DataMember] public string InReplyTo; [DataMember] public List<string> References;
        [DataMember] public string Subject; [DataMember] public string FromName; [DataMember] public string FromAddr;
        [DataMember] public string ReplyTo; [DataMember] public string To; [DataMember] public string Cc;
        [DataMember] public string Date;         // ISO 8601 (local)
        [DataMember] public bool Seen; [DataMember] public bool Flagged; [DataMember] public bool Answered; [DataMember] public bool Draft;
        [DataMember] public long Size; [DataMember] public bool HasAttachments;
        [DataMember] public string Preview; [DataMember] public string Text;   // Text = first ~4 KB of body text, for search / the spam filter
        [DataMember] public string ListUnsubscribe; [DataMember] public string ListUnsubscribePost; [DataMember] public string ListId; [DataMember] public string Precedence;
        [DataMember] public string AuthResults; [DataMember] public string SpamHeaders; [DataMember] public string AutoSubmitted;
        [DataMember] public string Category;     // people, newsletters, receipts, notifications
        [DataMember] public double Spam; [DataMember] public string SpamWhy; [DataMember] public bool NotSpam;
        [DataMember] public bool Screened;       // waiting in the Screener
        [DataMember] public string Thread;
        [DataMember] public bool BodyCached;
        [DataMember] public bool Classified;
        public string Key { get { return Account + "|" + Folder + "|" + UidValidity + "|" + Uid; } }
        public DateTime When { get { DateTime d; return DateTime.TryParse(Date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d) ? d : DateTime.MinValue; } }
        public string Sender { get { return string.IsNullOrEmpty(FromName) ? FromAddr : FromName; } }
        public string Domain { get { var a = FromAddr ?? ""; var i = a.LastIndexOf('@'); return i < 0 ? "" : a.Substring(i + 1).ToLowerInvariant(); } }
    }

    [DataContract]
    public class MailIndex
    {
        [DataMember] public List<FolderInfo> Folders = new List<FolderInfo>();
        [DataMember] public List<MailItem> Items = new List<MailItem>();
        [DataMember] public string Synced;
    }

    public class MailStore
    {
        public readonly string Dir; readonly Dictionary<string, MailIndex> idx = new Dictionary<string, MailIndex>();
        public readonly object Lock = new object();
        public bool Memory;   // --snap: nothing touches the disk
        public MailStore(string dir) { Dir = dir; }
        string AccDir(string acc) { return Path.Combine(Dir, Safe(acc)); }
        static string Safe(string s) { return Regex.Replace(s ?? "x", "[^A-Za-z0-9_-]", "_"); }
        static string Hash(string s) { using (var h = SHA1.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").ToLowerInvariant(); }

        public MailIndex Index(string acc)
        {
            lock (Lock)
            {
                MailIndex m; if (idx.TryGetValue(acc, out m)) return m;
                try { if (!Memory) m = Json.Read<MailIndex>(Vault.ReadFile(Path.Combine(AccDir(acc), "index.bin"))); } catch { m = null; }   // damaged = start again (the server has the mail)
                if (m == null) m = new MailIndex();
                if (m.Folders == null) m.Folders = new List<FolderInfo>(); if (m.Items == null) m.Items = new List<MailItem>();
                idx[acc] = m; return m;
            }
        }
        public void Save(string acc)
        {
            if (Memory) return;
            byte[] b; lock (Lock) b = Json.Bytes(Index(acc));
            Vault.WriteFile(Path.Combine(AccDir(acc), "index.bin"), b);
        }
        public List<MailItem> All() { lock (Lock) return idx.Values.SelectMany(m => m.Items).ToList(); }
        public List<MailItem> Items(string acc) { lock (Lock) return Index(acc).Items.ToList(); }
        public MailItem Find(string key) { lock (Lock) return idx.Values.SelectMany(m => m.Items).FirstOrDefault(i => i.Key == key); }
        public void Put(MailItem it)
        {
            lock (Lock)
            {
                var m = Index(it.Account); var k = it.Key;
                var old = m.Items.FindIndex(x => x.Key == k);
                if (old >= 0) m.Items[old] = it; else m.Items.Add(it);
            }
        }
        public void Remove(string acc, Func<MailItem, bool> which) { lock (Lock) Index(acc).Items.RemoveAll(x => which(x)); }
        public void Forget(string acc)
        {
            lock (Lock) idx.Remove(acc);
            if (!Memory) try { if (Directory.Exists(AccDir(acc))) Directory.Delete(AccDir(acc), true); } catch { }
        }

        // ---- whole messages
        string BodyPath(MailItem it) { return Path.Combine(AccDir(it.Account), "m", Hash(it.Key) + ".bin"); }
        readonly Dictionary<string, byte[]> mem = new Dictionary<string, byte[]>();
        public void PutBody(MailItem it, byte[] raw)
        {
            if (Memory) { lock (mem) mem[it.Key] = raw; return; }
            Vault.WriteFile(BodyPath(it), raw);
        }
        public byte[] Body(MailItem it)
        {
            if (Memory) { lock (mem) { byte[] b; return mem.TryGetValue(it.Key, out b) ? b : null; } }
            try { return Vault.ReadFile(BodyPath(it)); } catch { return null; }
        }
        public void DropBody(MailItem it) { if (Memory) { lock (mem) mem.Remove(it.Key); return; } try { File.Delete(BodyPath(it)); } catch { } }

        // ---- search across the cache: every word must appear in subject, sender, recipients or the cached text.
        // "from:x", "to:x", "subject:x", "has:attachment", "is:unread", "is:starred", "label:x" narrow it down.
        public static List<MailItem> Search(IEnumerable<MailItem> items, string query, Profile p)
        {
            var words = Tokens(query ?? ""); var r = new List<MailItem>();
            foreach (var it in items)
            {
                bool ok = true;
                foreach (var w in words)
                {
                    var lw = w.ToLowerInvariant();
                    if (lw.StartsWith("from:")) ok = Has(it.FromName, lw.Substring(5)) || Has(it.FromAddr, lw.Substring(5));
                    else if (lw.StartsWith("to:")) ok = Has(it.To, lw.Substring(3)) || Has(it.Cc, lw.Substring(3));
                    else if (lw.StartsWith("subject:")) ok = Has(it.Subject, lw.Substring(8));
                    else if (lw == "has:attachment") ok = it.HasAttachments;
                    else if (lw == "is:unread") ok = !it.Seen;
                    else if (lw == "is:starred") ok = it.Flagged;
                    else if (lw.StartsWith("label:")) { List<string> l; ok = p != null && p.Labels.TryGetValue(it.Key, out l) && l.Any(x => x.Equals(lw.Substring(6), StringComparison.OrdinalIgnoreCase)); }
                    else ok = Has(it.Subject, lw) || Has(it.FromName, lw) || Has(it.FromAddr, lw) || Has(it.To, lw) || Has(it.Cc, lw) || Has(it.Text, lw) || Has(it.Preview, lw);
                    if (!ok) break;
                }
                if (ok) r.Add(it);
            }
            return r.OrderByDescending(x => x.When).ToList();
        }
        static bool Has(string s, string w) { return s != null && s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0; }
        static List<string> Tokens(string q)
        {
            var r = new List<string>();
            foreach (Match m in Regex.Matches(q, "(\\w+:)?\"[^\"]*\"|\\S+")) { var v = m.Value.Replace("\"", ""); if (v.Length > 0) r.Add(v); }
            return r;
        }
    }

    // ---- conversations: by Message-ID / In-Reply-To / References, then by the same subject (without Re: / Fwd:) within 30 days
    public static class Threads
    {
        public static string Norm(string subject)
        {
            var s = (subject ?? "").Trim();
            for (int i = 0; i < 6; i++) { var n = Regex.Replace(s, @"^\s*((re|fw|fwd|aw|sv|wg)(\[\d+\])?\s*:\s*)", "", RegexOptions.IgnoreCase); if (n == s) break; s = n; }
            return Regex.Replace(s.ToLowerInvariant(), @"\s+", " ").Trim();
        }
        public static void Assign(List<MailItem> items)
        {
            // 1. Message-ID / In-Reply-To / References, in any order (union-find)
            var parent = new Dictionary<MailItem, MailItem>();
            Func<MailItem, MailItem> root = null; root = x => { var p = parent[x]; if (p == x) return x; var r = root(p); parent[x] = r; return r; };
            foreach (var it in items) parent[it] = it;
            var byId = new Dictionary<string, MailItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items) if (!string.IsNullOrEmpty(it.MessageId) && !byId.ContainsKey(it.MessageId)) byId[it.MessageId] = it;
            foreach (var it in items)
            {
                var refs = new List<string>(); if (it.References != null) refs.AddRange(it.References); if (!string.IsNullOrEmpty(it.InReplyTo)) refs.Add(it.InReplyTo);
                foreach (var r in refs) { MailItem o; if (r != null && byId.TryGetValue(r, out o)) { var a = root(it); var b = root(o); if (a != b) parent[a] = b; } }
            }
            // 2. a reply ("Re:", or with references we don't have) with the same subject within 30 days joins that conversation
            var bySubject = new Dictionary<string, KeyValuePair<MailItem, DateTime>>();
            foreach (var it in items.OrderBy(x => x.When))
            {
                var ns = Norm(it.Subject); if (ns.Length == 0) continue;
                bool reply = !string.IsNullOrEmpty(it.InReplyTo) || (it.References != null && it.References.Count > 0) || !string.Equals(ns, Regex.Replace((it.Subject ?? "").Trim().ToLowerInvariant(), @"\s+", " "), StringComparison.Ordinal);
                KeyValuePair<MailItem, DateTime> s;
                if (reply && bySubject.TryGetValue(ns, out s) && (it.When - s.Value).TotalDays <= 30) { var a = root(it); var b = root(s.Key); if (a != b) parent[a] = b; }
                bySubject[ns] = new KeyValuePair<MailItem, DateTime>(it, it.When);
            }
            // the conversation's id = its oldest message's
            foreach (var g in items.GroupBy(x => root(x)))
            {
                var first = g.OrderBy(x => x.When).First(); var id = "t:" + (first.MessageId ?? first.Key);
                foreach (var x in g) x.Thread = id;
            }
        }
    }
}
