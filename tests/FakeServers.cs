// Hallow Email tests - local stand-ins only (127.0.0.1, random ports). Never a real mail server, never real mail.
//  FakeImap : a tiny IMAP4rev1 server - enough of the protocol for what MailKit sends: CAPABILITY, LOGIN, AUTHENTICATE PLAIN / XOAUTH2,
//             LIST, SELECT / EXAMINE, UID SEARCH, UID FETCH (FLAGS, INTERNALDATE, RFC822.SIZE, BODY.PEEK[HEADER], BODY.PEEK[]),
//             UID STORE (flags + keywords), UID MOVE, UID EXPUNGE / EXPUNGE, APPEND (literals), CREATE, IDLE (pushes EXISTS), NOOP, LOGOUT
//  FakeSmtp : EHLO, AUTH PLAIN / LOGIN, MAIL, RCPT, DATA - every message is captured, nothing goes anywhere
//  FakeHttp : HttpListener on 127.0.0.1 for the unsubscribe / Cloudflare / OAuth token stand-ins (records each request)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace HallowEmail.Tests
{
    public class FakeMsg { public uint Uid; public HashSet<string> Flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase); public byte[] Raw; public DateTime Date = DateTime.Now; }
    public class FakeFolder { public string Name; public string Attr; public uint Validity = 7; public uint Next = 1; public List<FakeMsg> Msgs = new List<FakeMsg>(); }

    public class FakeImap
    {
        public readonly int Port; readonly TcpListener l; public readonly object Lock = new object();
        public readonly Dictionary<string, FakeFolder> Folders = new Dictionary<string, FakeFolder>(StringComparer.OrdinalIgnoreCase);
        public string User = "alex@example.test", Pass = "app-pass-1234", Token;   // Token: the OAuth access token XOAUTH2 accepts
        public readonly List<string> Log = new List<string>(); public int Logins, Idles;
        readonly List<Session> sessions = new List<Session>();
        public FakeImap()
        {
            foreach (var f in new[] { new[] { "INBOX", "" }, new[] { "Sent", "\\Sent" }, new[] { "Drafts", "\\Drafts" }, new[] { "Junk", "\\Junk" }, new[] { "Trash", "\\Trash" }, new[] { "Archive", "\\Archive" }, new[] { "Receipts", "" } })
                Folders[f[0]] = new FakeFolder { Name = f[0], Attr = f[1] };
            l = new TcpListener(IPAddress.Loopback, 0); l.Start(); Port = ((IPEndPoint)l.LocalEndpoint).Port;
            new Thread(() => { while (true) { TcpClient c; try { c = l.AcceptTcpClient(); } catch { return; } var s = new Session(this, c); lock (Lock) sessions.Add(s); new Thread(s.Run) { IsBackground = true }.Start(); } }) { IsBackground = true }.Start();
        }
        public void Stop() { try { l.Stop(); } catch { } lock (Lock) foreach (var s in sessions) s.Close(); }
        public FakeMsg Add(string folder, byte[] raw, params string[] flags)
        {
            FakeMsg m;
            lock (Lock) { var f = Folders[folder]; m = new FakeMsg { Uid = f.Next++, Raw = raw }; foreach (var x in flags) m.Flags.Add(x); f.Msgs.Add(m); }
            Notify(folder); return m;
        }
        public void Notify(string folder) { List<Session> l2; lock (Lock) l2 = sessions.ToList(); foreach (var s in l2) s.Changed(folder); }
        public int Count(string folder) { lock (Lock) return Folders[folder].Msgs.Count; }
        public List<string> Subjects(string folder) { lock (Lock) return Folders[folder].Msgs.Select(m => Regex.Match(Encoding.UTF8.GetString(m.Raw), @"^Subject: (.*)$", RegexOptions.Multiline).Groups[1].Value.Trim()).ToList(); }
        public FakeMsg Find(string folder, string subject) { lock (Lock) return Folders[folder].Msgs.FirstOrDefault(m => Encoding.UTF8.GetString(m.Raw).Contains("Subject: " + subject)); }
        void L(string s) { lock (Log) { Log.Add(s); if (Log.Count > 3000) Log.RemoveAt(0); } }

        class Session
        {
            readonly FakeImap S; readonly TcpClient c; readonly Stream st; FakeFolder sel; bool readOnly; volatile bool idling; int known;
            readonly object w = new object(); string idleTag; readonly HashSet<string> pending = new HashSet<string>();
            public Session(FakeImap s, TcpClient c) { S = s; this.c = c; st = c.GetStream(); }
            public void Close() { try { c.Close(); } catch { } }
            void Send(string line) { var b = Encoding.UTF8.GetBytes(line + "\r\n"); lock (w) st.Write(b, 0, b.Length); }
            void SendRaw(byte[] b) { lock (w) st.Write(b, 0, b.Length); }
            public void Changed(string folder)
            {
                if (sel == null || !sel.Name.Equals(folder, StringComparison.OrdinalIgnoreCase)) return;
                if (idling) { try { Flush(); } catch { } } else lock (pending) pending.Add(folder);
            }
            void Flush()
            {
                int n; lock (S.Lock) n = sel == null ? 0 : sel.Msgs.Count;
                if (sel != null && n != known) { known = n; Send("* " + n + " EXISTS"); }
            }
            string ReadLine()
            {
                var buf = new List<byte>(); int b;
                while ((b = st.ReadByte()) >= 0) { if (b == '\n') break; buf.Add((byte)b); }
                if (b < 0 && buf.Count == 0) return null;
                return Encoding.UTF8.GetString(buf.ToArray()).TrimEnd('\r');
            }
            byte[] ReadN(int n) { var r = new byte[n]; int o = 0; while (o < n) { int k = st.Read(r, o, n - o); if (k <= 0) throw new IOException("eof"); o += k; } return r; }
            public void Run()
            {
                try
                {
                    Send("* OK [CAPABILITY IMAP4rev1 IDLE UIDPLUS MOVE SPECIAL-USE AUTH=PLAIN AUTH=XOAUTH2] Hallow test IMAP ready");
                    while (true)
                    {
                        var line = ReadLine(); if (line == null) return;
                        // a literal at the end: {n} (we answer "+") or {n+} (LITERAL+ - not offered, but harmless)
                        var lit = Regex.Match(line, @"\{(\d+)(\+?)\}$"); byte[] data = null;
                        if (lit.Success)
                        {
                            if (lit.Groups[2].Value != "+") Send("+ Ready for literal data");
                            data = ReadN(int.Parse(lit.Groups[1].Value)); var rest = ReadLine() ?? "";
                            line = line.Substring(0, lit.Index) + "{LIT}" + rest;
                        }
                        S.L("C: " + (line.StartsWith("A") && line.Contains(" LOGIN ") ? line.Split(' ')[0] + " LOGIN ****" : line));
                        var sp = line.IndexOf(' '); if (sp < 0) { Send("* BAD no command"); continue; }
                        var tag = line.Substring(0, sp); var rest2 = line.Substring(sp + 1); var cmdEnd = rest2.IndexOf(' ');
                        var cmd = (cmdEnd < 0 ? rest2 : rest2.Substring(0, cmdEnd)).ToUpperInvariant(); var args = cmdEnd < 0 ? "" : rest2.Substring(cmdEnd + 1);
                        if (cmd == "UID") { var e2 = args.IndexOf(' '); cmd = "UID " + args.Substring(0, e2).ToUpperInvariant(); args = args.Substring(e2 + 1); }
                        if (!Handle(tag, cmd, args, data)) return;
                    }
                }
                catch { }
                finally { Close(); }
            }
            static List<string> Atoms(string s)
            {
                var r = new List<string>(); int i = 0;
                while (i < s.Length)
                {
                    if (s[i] == ' ') { i++; continue; }
                    if (s[i] == '"') { var sb = new StringBuilder(); i++; while (i < s.Length && s[i] != '"') { if (s[i] == '\\' && i + 1 < s.Length) i++; sb.Append(s[i]); i++; } i++; r.Add(sb.ToString()); continue; }
                    if (s[i] == '(') { int d = 0, j = i; do { if (s[j] == '(') d++; if (s[j] == ')') d--; j++; } while (j < s.Length && d > 0); r.Add(s.Substring(i, j - i)); i = j; continue; }
                    int k = s.IndexOf(' ', i); if (k < 0) k = s.Length;
                    // keep BODY.PEEK[HEADER.FIELDS (A B)] in one piece
                    var tok = s.Substring(i, k - i); while (tok.Count(ch => ch == '[') > tok.Count(ch => ch == ']') && k < s.Length) { k = s.IndexOf(']', k); k = k < 0 ? s.Length : k + 1; tok = s.Substring(i, k - i); }
                    r.Add(tok); i = k;
                }
                return r;
            }
            static string Q(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
            FakeFolder Folder(string name) { FakeFolder f; lock (S.Lock) return S.Folders.TryGetValue(name, out f) ? f : null; }
            List<FakeMsg> Set(string set, bool uid)
            {
                var r = new List<FakeMsg>();
                lock (S.Lock)
                {
                    var msgs = sel.Msgs; uint max = msgs.Count == 0 ? 0 : uid ? msgs.Max(m => m.Uid) : (uint)msgs.Count;
                    foreach (var part in set.Split(','))
                    {
                        var ab = part.Split(':'); Func<string, uint> v = x => x == "*" ? max : uint.Parse(x);
                        uint a = v(ab[0]), b = ab.Length > 1 ? v(ab[1]) : a; if (a > b) { var t = a; a = b; b = t; }
                        for (int i = 0; i < msgs.Count; i++) { var key = uid ? msgs[i].Uid : (uint)(i + 1); if (key >= a && key <= b && !r.Contains(msgs[i])) r.Add(msgs[i]); }
                    }
                }
                return r;
            }
            string Flags(FakeMsg m) { return "(" + string.Join(" ", m.Flags) + ")"; }
            void Expunged(List<FakeMsg> gone)
            {
                lock (S.Lock)
                {
                    var idx = gone.Select(m => sel.Msgs.IndexOf(m) + 1).Where(i => i > 0).OrderByDescending(i => i).ToList();
                    foreach (var m in gone) sel.Msgs.Remove(m);
                    foreach (var i in idx) Send("* " + i + " EXPUNGE");
                    known = sel.Msgs.Count;
                }
            }
            bool Handle(string tag, string cmd, string args, byte[] data)
            {
                var a = Atoms(args);
                switch (cmd)
                {
                    case "CAPABILITY": Send("* CAPABILITY IMAP4rev1 IDLE UIDPLUS MOVE SPECIAL-USE AUTH=PLAIN AUTH=XOAUTH2"); Send(tag + " OK CAPABILITY completed"); return true;
                    case "NOOP": case "CHECK": Flush(); Send(tag + " OK done"); return true;
                    case "LOGOUT": Send("* BYE bye"); Send(tag + " OK LOGOUT completed"); return false;
                    case "ID": Send("* ID NIL"); Send(tag + " OK ID completed"); return true;
                    case "LOGIN":
                        if (a.Count >= 2 && a[0] == S.User && a[1] == S.Pass) { S.Logins++; Send(tag + " OK LOGIN completed"); } else Send(tag + " NO [AUTHENTICATIONFAILED] Invalid credentials");
                        return true;
                    case "AUTHENTICATE":
                        {
                            string b64 = a.Count > 1 ? a[1] : null;
                            if (b64 == null) { Send("+ "); b64 = ReadLine(); }
                            string dec = ""; try { dec = Encoding.UTF8.GetString(Convert.FromBase64String(b64 ?? "")); } catch { }
                            bool ok = false;
                            if (a[0].Equals("PLAIN", StringComparison.OrdinalIgnoreCase)) { var p = dec.Split('\0'); ok = p.Length == 3 && p[1] == S.User && p[2] == S.Pass; }
                            else if (a[0].Equals("XOAUTH2", StringComparison.OrdinalIgnoreCase)) ok = S.Token != null && dec == "user=" + S.User + "\u0001auth=Bearer " + S.Token + "\u0001\u0001";
                            if (ok) { S.Logins++; Send(tag + " OK AUTHENTICATE completed"); }
                            else { if (a[0].Equals("XOAUTH2", StringComparison.OrdinalIgnoreCase)) { Send("+ eyJzdGF0dXMiOiI0MDEifQ=="); ReadLine(); } Send(tag + " NO [AUTHENTICATIONFAILED] Invalid credentials"); }
                            return true;
                        }
                    case "ENABLE": Send(tag + " OK ENABLE completed"); return true;
                    case "NAMESPACE": Send("* NAMESPACE ((\"\" \"/\")) NIL NIL"); Send(tag + " OK NAMESPACE completed"); return true;
                    case "LIST": case "LSUB":
                        {
                            var pattern = a.Count > 1 ? a[1] : "";
                            if (pattern == "") { Send("* " + cmd + " (\\Noselect) \"/\" \"\""); Send(tag + " OK " + cmd + " completed"); return true; }
                            var rx = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("%", "[^/]*") + "$", RegexOptions.IgnoreCase);
                            List<FakeFolder> fs; lock (S.Lock) fs = S.Folders.Values.ToList();
                            foreach (var f in fs.Where(f => rx.IsMatch(f.Name))) Send("* " + cmd + " (\\HasNoChildren" + (f.Attr.Length > 0 ? " " + f.Attr : "") + ") \"/\" " + Q(f.Name));
                            Send(tag + " OK " + cmd + " completed"); return true;
                        }
                    case "STATUS":
                        {
                            var f = Folder(a[0]); if (f == null) { Send(tag + " NO no such mailbox"); return true; }
                            lock (S.Lock) Send("* STATUS " + Q(f.Name) + " (MESSAGES " + f.Msgs.Count + " UIDNEXT " + f.Next + " UIDVALIDITY " + f.Validity + " UNSEEN " + f.Msgs.Count(m => !m.Flags.Contains("\\Seen")) + ")");
                            Send(tag + " OK STATUS completed"); return true;
                        }
                    case "SELECT": case "EXAMINE":
                        {
                            var f = Folder(a[0]); if (f == null) { Send(tag + " NO no such mailbox"); return true; }
                            sel = f; readOnly = cmd == "EXAMINE";
                            lock (S.Lock)
                            {
                                known = f.Msgs.Count;
                                Send("* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)");
                                Send("* OK [PERMANENTFLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft \\*)] Limited");
                                Send("* " + f.Msgs.Count + " EXISTS"); Send("* 0 RECENT");
                                Send("* OK [UIDVALIDITY " + f.Validity + "] UIDs valid"); Send("* OK [UIDNEXT " + f.Next + "] Predicted next UID");
                            }
                            Send(tag + " OK [" + (readOnly ? "READ-ONLY" : "READ-WRITE") + "] " + cmd + " completed"); return true;
                        }
                    case "CLOSE": case "UNSELECT": sel = null; Send(tag + " OK done"); return true;
                    case "CREATE":
                        lock (S.Lock) if (!S.Folders.ContainsKey(a[0])) S.Folders[a[0]] = new FakeFolder { Name = a[0], Attr = "" };
                        Send(tag + " OK CREATE completed"); return true;
                    case "SUBSCRIBE": Send(tag + " OK done"); return true;
                    case "IDLE":
                        {
                            S.Idles++; idleTag = tag; Send("+ idling"); idling = true;
                            lock (pending) pending.Clear();
                            Flush();
                            var done = ReadLine(); idling = false;
                            Send(tag + " OK IDLE terminated"); return done != null;
                        }
                    case "UID SEARCH": case "SEARCH":
                        {
                            if (sel == null) { Send(tag + " BAD no mailbox selected"); return true; }
                            List<uint> ids; lock (S.Lock) ids = cmd == "UID SEARCH" ? sel.Msgs.Select(m => m.Uid).ToList() : Enumerable.Range(1, sel.Msgs.Count).Select(i => (uint)i).ToList();
                            Send("* SEARCH" + string.Concat(ids.Select(x => " " + x))); Send(tag + " OK SEARCH completed"); return true;
                        }
                    case "UID FETCH": case "FETCH":
                        {
                            if (sel == null) { Send(tag + " BAD no mailbox selected"); return true; }
                            bool uid = cmd == "UID FETCH"; var items = a.Count > 1 ? a[1].Trim('(', ')').ToUpperInvariant() : "";
                            foreach (var m in Set(a[0], uid))
                            {
                                int seq; lock (S.Lock) seq = sel.Msgs.IndexOf(m) + 1; if (seq <= 0) continue;
                                var parts = new List<string> { "UID " + m.Uid };
                                if (items.Contains("FLAGS")) parts.Add("FLAGS " + Flags(m));
                                if (items.Contains("INTERNALDATE")) parts.Add("INTERNALDATE \"" + m.Date.ToString("dd-MMM-yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture) + "\"");
                                if (items.Contains("RFC822.SIZE")) parts.Add("RFC822.SIZE " + m.Raw.Length);
                                byte[] lit = null; string litName = null;
                                if (items.Contains("BODY.PEEK[HEADER]") || items.Contains("BODY[HEADER]")) { var t = Encoding.UTF8.GetString(m.Raw); var e = t.IndexOf("\r\n\r\n"); lit = Encoding.UTF8.GetBytes(e < 0 ? t : t.Substring(0, e + 4)); litName = "BODY[HEADER]"; }
                                else if (items.Contains("BODY.PEEK[]") || items.Contains("BODY[]") || items.Contains("RFC822")) { lit = m.Raw; litName = "BODY[]"; if (items.Contains("BODY[]") && !items.Contains("PEEK") && !readOnly) m.Flags.Add("\\Seen"); }
                                if (lit == null) { Send("* " + seq + " FETCH (" + string.Join(" ", parts) + ")"); continue; }
                                lock (w)
                                {
                                    SendRaw(Encoding.UTF8.GetBytes("* " + seq + " FETCH (" + string.Join(" ", parts) + " " + litName + " {" + lit.Length + "}\r\n"));
                                    SendRaw(lit); SendRaw(Encoding.UTF8.GetBytes(")\r\n"));
                                }
                            }
                            Send(tag + " OK FETCH completed"); return true;
                        }
                    case "UID STORE": case "STORE":
                        {
                            if (sel == null || readOnly) { Send(tag + " NO read-only"); return true; }
                            var op = a[1].ToUpperInvariant(); var fl = a[2].Trim('(', ')').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var m in Set(a[0], cmd == "UID STORE"))
                            {
                                lock (S.Lock)
                                {
                                    if (op.StartsWith("+")) foreach (var f in fl) m.Flags.Add(f);
                                    else if (op.StartsWith("-")) foreach (var f in fl) m.Flags.Remove(f);
                                    else { m.Flags.Clear(); foreach (var f in fl) m.Flags.Add(f); }
                                }
                                if (!op.EndsWith(".SILENT")) Send("* " + (sel.Msgs.IndexOf(m) + 1) + " FETCH (UID " + m.Uid + " FLAGS " + Flags(m) + ")");
                            }
                            Send(tag + " OK STORE completed"); return true;
                        }
                    case "UID MOVE": case "UID COPY":
                        {
                            var dest = Folder(a[1]); if (dest == null) { Send(tag + " NO [TRYCREATE] no such mailbox"); return true; }
                            var ms = Set(a[0], true); var src = new List<uint>(); var dst = new List<uint>();
                            lock (S.Lock) foreach (var m in ms) { var n = new FakeMsg { Uid = dest.Next++, Raw = m.Raw, Date = m.Date }; foreach (var f in m.Flags) n.Flags.Add(f); n.Flags.Remove("\\Deleted"); dest.Msgs.Add(n); src.Add(m.Uid); dst.Add(n.Uid); }
                            if (ms.Count > 0) Send("* OK [COPYUID " + dest.Validity + " " + string.Join(",", src) + " " + string.Join(",", dst) + "] moved");
                            if (cmd == "UID MOVE") Expunged(ms);
                            S.Notify(dest.Name);
                            Send(tag + " OK " + cmd.Substring(4) + " completed"); return true;
                        }
                    case "EXPUNGE": case "UID EXPUNGE":
                        {
                            List<FakeMsg> gone; lock (S.Lock) gone = (cmd == "UID EXPUNGE" ? Set(a[0], true) : sel.Msgs.ToList()).Where(m => m.Flags.Contains("\\Deleted")).ToList();
                            Expunged(gone); Send(tag + " OK EXPUNGE completed"); return true;
                        }
                    case "APPEND":
                        {
                            var f = Folder(a[0]); if (f == null || data == null) { Send(tag + " NO [TRYCREATE] no such mailbox"); return true; }
                            var m = new FakeMsg { Raw = data };
                            foreach (var x in a.Skip(1).Where(x => x.StartsWith("("))) foreach (var fl in x.Trim('(', ')').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) m.Flags.Add(fl);
                            lock (S.Lock) { m.Uid = f.Next++; f.Msgs.Add(m); }
                            S.Notify(f.Name);
                            Send(tag + " OK [APPENDUID " + f.Validity + " " + m.Uid + "] APPEND completed"); return true;
                        }
                }
                Send(tag + " BAD unknown command " + cmd); return true;
            }
        }
    }

    public class FakeSmtp
    {
        public readonly int Port; readonly TcpListener l; public string User = "alex@example.test", Pass = "app-pass-1234", Token;
        public readonly List<string[]> Mail = new List<string[]>();   // from, to (comma), data
        public int Auths;
        public FakeSmtp()
        {
            l = new TcpListener(IPAddress.Loopback, 0); l.Start(); Port = ((IPEndPoint)l.LocalEndpoint).Port;
            new Thread(() => { while (true) { TcpClient c; try { c = l.AcceptTcpClient(); } catch { return; } new Thread(() => Run(c)) { IsBackground = true }.Start(); } }) { IsBackground = true }.Start();
        }
        public void Stop() { try { l.Stop(); } catch { } }
        public int Count { get { lock (Mail) return Mail.Count; } }
        void Run(TcpClient c)
        {
            try
            {
                using (c) using (var s = c.GetStream())
                {
                    var r = new StreamReader(s, Encoding.ASCII); var w = new StreamWriter(s, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
                    w.WriteLine("220 hallow-test SMTP ready"); string from = null; var to = new List<string>(); bool authed = false;
                    while (true)
                    {
                        var line = r.ReadLine(); if (line == null) return; var up = line.ToUpperInvariant();
                        if (up.StartsWith("EHLO") || up.StartsWith("HELO")) { w.WriteLine("250-hallow-test"); w.WriteLine("250-AUTH PLAIN LOGIN XOAUTH2"); w.WriteLine("250 8BITMIME"); }
                        else if (up.StartsWith("AUTH PLAIN"))
                        {
                            var b = line.Length > 11 ? line.Substring(11).Trim() : null; if (string.IsNullOrEmpty(b)) { w.WriteLine("334 "); b = r.ReadLine(); }
                            var p = Encoding.UTF8.GetString(Convert.FromBase64String(b)).Split('\0');
                            if (p.Length == 3 && p[1] == User && p[2] == Pass) { authed = true; Auths++; w.WriteLine("235 2.7.0 ok"); } else w.WriteLine("535 5.7.8 bad credentials");
                        }
                        else if (up.StartsWith("AUTH LOGIN"))
                        {
                            w.WriteLine("334 VXNlcm5hbWU6"); var u = Encoding.UTF8.GetString(Convert.FromBase64String(r.ReadLine()));
                            w.WriteLine("334 UGFzc3dvcmQ6"); var pw = Encoding.UTF8.GetString(Convert.FromBase64String(r.ReadLine()));
                            if (u == User && pw == Pass) { authed = true; Auths++; w.WriteLine("235 2.7.0 ok"); } else w.WriteLine("535 5.7.8 bad credentials");
                        }
                        else if (up.StartsWith("AUTH XOAUTH2"))
                        {
                            var b = line.Length > 13 ? line.Substring(13).Trim() : null; if (string.IsNullOrEmpty(b)) { w.WriteLine("334 "); b = r.ReadLine(); }
                            var d = Encoding.UTF8.GetString(Convert.FromBase64String(b));
                            if (Token != null && d == "user=" + User + "\u0001auth=Bearer " + Token + "\u0001\u0001") { authed = true; Auths++; w.WriteLine("235 2.7.0 ok"); } else w.WriteLine("535 5.7.8 bad token");
                        }
                        else if (up.StartsWith("MAIL FROM:")) { if (!authed) { w.WriteLine("530 5.7.0 auth first"); continue; } from = Regex.Match(line, "<([^>]*)>").Groups[1].Value; to.Clear(); w.WriteLine("250 ok"); }
                        else if (up.StartsWith("RCPT TO:")) { to.Add(Regex.Match(line, "<([^>]*)>").Groups[1].Value); w.WriteLine("250 ok"); }
                        else if (up == "DATA")
                        {
                            w.WriteLine("354 go ahead"); var sb = new StringBuilder();
                            while (true) { var d = r.ReadLine(); if (d == null || d == ".") break; sb.Append(d.StartsWith("..") ? d.Substring(1) : d).Append("\r\n"); }
                            lock (Mail) Mail.Add(new[] { from, string.Join(",", to), sb.ToString() }); w.WriteLine("250 2.0.0 queued");
                        }
                        else if (up == "RSET" || up == "NOOP") w.WriteLine("250 ok");
                        else if (up == "QUIT") { w.WriteLine("221 bye"); return; }
                        else w.WriteLine("502 not implemented");
                    }
                }
            }
            catch { }
        }
    }

    public class FakeHttp
    {
        public readonly string Url; readonly HttpListener h = new HttpListener();
        public readonly List<string[]> Requests = new List<string[]>();   // method, path+query, auth header, body
        public Func<string, string, string, string, Tuple<int, string>> Answer;   // method, path, auth, body -> status, json
        public FakeHttp()
        {
            int port; var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
            Url = "http://127.0.0.1:" + port + "/"; h.Prefixes.Add(Url); h.Start();
            new Thread(() =>
            {
                while (h.IsListening)
                {
                    HttpListenerContext ctx; try { ctx = h.GetContext(); } catch { return; }
                    try
                    {
                        string body; using (var r = new StreamReader(ctx.Request.InputStream)) body = r.ReadToEnd();
                        var req = new[] { ctx.Request.HttpMethod, ctx.Request.Url.PathAndQuery, ctx.Request.Headers["Authorization"], body };
                        lock (Requests) Requests.Add(req);
                        var a = Answer == null ? Tuple.Create(200, "{}") : Answer(req[0], req[1], req[2], req[3]);
                        var b = Encoding.UTF8.GetBytes(a.Item2); ctx.Response.StatusCode = a.Item1; ctx.Response.ContentType = "application/json"; ctx.Response.OutputStream.Write(b, 0, b.Length); ctx.Response.Close();
                    }
                    catch { try { ctx.Response.Abort(); } catch { } }
                }
            }) { IsBackground = true }.Start();
        }
        public void Stop() { try { h.Stop(); } catch { } }
    }
}
