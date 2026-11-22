// Hallow Email - a private desktop email client, Hallow style: IMAP / SMTP accounts (app passwords, or OAuth with your own client ID),
// a unified inbox with People / Newsletters / Receipts / Notifications, an encrypted local cache with search and conversations, IMAP IDLE
// push, remote images and trackers blocked, phishing hints, Defender-scanned attachments, a private spam filter, rules, the Screener,
// an unsubscribe finder, Undo send / Send later / Snooze, Cloudflare masked addresses, and the Hallow family link.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using WF = System.Windows.Forms;

[assembly: AssemblyTitle("Hallow Email")]
[assembly: AssemblyProduct("Hallow Email")]
[assembly: AssemblyDescription("A private desktop email client, Hallow style")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace HallowEmail
{
    public static class Program
    {
        public const string Version = "1.1";
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);

        // MailKit / MimeKit and their helpers sit next to the exe; older references (System.Memory 4.0.1.x etc.) are answered with the copy we ship
        public static void Resolver()
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                try { var n = new AssemblyName(e.Name).Name; var p = Path.Combine(dir, n + ".dll"); return File.Exists(p) ? Assembly.LoadFrom(p) : null; } catch { return null; }
            };
        }

        [STAThread]
        public static void Main(string[] args)
        {
            Resolver();
            int ci = Array.IndexOf(args, "--cmd");
            if (ci >= 0) { Environment.Exit(Family.RunCmd(ci + 1 < args.Length ? args[ci + 1] : "")); return; }
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            int si = Array.IndexOf(args, "--snap");
            if (si >= 0 && si + 1 < args.Length) { RunSnap(args[si + 1]); return; }
            var mailto = args.FirstOrDefault(a => a.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));
            var leaf = MailSettings.TestDir != null ? "Test" + Path.GetFileName(MailSettings.TestDir.TrimEnd('\\')) : "";
            bool created;
            var mutex = new Mutex(true, @"Local\HallowEmail" + leaf, out created);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HallowEmailShow" + leaf);
            if (!created)
            {
                if (mailto != null) Family.Send("Email", "compose " + mailto.Substring(7), 3000); else showEvent.Set();
                return;
            }
            Crash.Install();
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal; } catch { }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LoadTheme(app);
            app.DispatcherUnhandledException += (s, e) => { Crash.Write(e.Exception); e.Handled = true; Environment.Exit(1); };
            var c = new Controller(args.Contains("--tray") && mailto == null);
            if (mailto != null) c.Compose(Uri.UnescapeDataString(mailto.Substring(7)));
            new Thread(() => { while (showEvent.WaitOne()) app.Dispatcher.BeginInvoke(new Action(() => c.ShowMain(null))); }) { IsBackground = true }.Start();
            app.Run();
            GC.KeepAlive(mutex);
        }
        static void RunSnap(string dir)
        {
            Snap.On = true; MailSettings.Frozen = true;
            var sa = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LoadTheme(sa);
            SnapShots.All(dir);
        }

        public static string AccentHex = Accent.Default;
        public static bool Light; public static bool? ForceLight;   // ForceLight: --snap renders both
        public static void LoadTheme(Application app, MailSettings st = null)
        {
            if (st == null) st = MailSettings.Load();
            AccentHex = Accent.Current(st.UseHallowAccent);
            Light = ForceLight ?? Look.Current(st);
            var x = Res("HallowEmail.Theme.xaml"); if (Light) x = Look.ToLight(x);
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Accent.ApplyTo(x, Light ? Look.DarkAccent(AccentHex) : Ui.Readable(AccentHex))));
        }
        static string Res(string name) { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) using (var r = new StreamReader(s)) return r.ReadToEnd(); }
    }

    // the app: tray icon, family pipe, the mail engine, notifications, look, updates
    public class Controller
    {
        public readonly MailSettings S = MailSettings.Load();
        public readonly Profile P; public readonly MailStore Store; public readonly Engine E;
        public readonly bool SnapMode;
        readonly Dispatcher ui;
        public MainWindow Main; WF.NotifyIcon tray; bool hubHosts, hubLists, quitting, balloonOwn;
        readonly List<string> log = new List<string>();
        DispatcherTimer tick, statusTimer; FileSystemWatcher accentWatch; bool statusDirty;
        public int Notifications;
        public readonly List<string[]> NotifyLog = new List<string[]>();   // tests: the notes that would have gone to Hub

        public Controller(bool trayOnly, bool snapMode = false, Profile snapProfile = null, MailStore snapStore = null)
        {
            SnapMode = snapMode; ui = Dispatcher.CurrentDispatcher;
            P = snapProfile ?? Profile.Load();
            Store = snapStore ?? new MailStore(Path.Combine(MailSettings.Dir, "cache"));
            E = new Engine(P, S, Store) { Offline = snapMode };
            foreach (var a in P.Accounts) Store.Index(a.Id);
            if (snapMode) return;
            if (!File.Exists(Path.Combine(MailSettings.Dir, "profile.bin"))) P.Save();   // the cache key exists from the first start
            E.Changed += () => { statusDirty = true; ui.BeginInvoke(new Action(() => { if (Main != null) Main.MailChanged(); })); };
            E.NewMail += l => ui.BeginInvoke(new Action(() => NewMail(l)));
            E.Sent += (q, err) => ui.BeginInvoke(new Action(() => { if (err != null) Say("Couldn't send \"" + q.Subject + "\": " + err + (q.Tries < 5 ? " - it'll try again." : "")); else Say("Sent: " + q.Subject); }));
            E.SnoozeDue += l => ui.BeginInvoke(new Action(() => { foreach (var s in l) Notify("Snoozed mail is back", s.Subject ?? "(no subject)", false); }));
            Family.Serve("Email", Command);
            E.Start();
            tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; tick.Tick += (s, e) => { E.Tick(); if (Main != null) Main.TickUndo(); }; tick.Start();
            if (!MailSettings.Headless)
            {
                SetupTray();
                var ht = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; ht.Tick += (s, e) => CheckHost(); ht.Start(); CheckHost();
                accentWatch = Accent.Watch(() => ui.BeginInvoke(new Action(ApplyLook)));
                var lt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; lt.Tick += (s, e) => { if (Main != null) ApplyLook(); }; lt.Start();
                if (MailSettings.TestDir == null || Environment.GetEnvironmentVariable("HALLOW_EMAIL_API") != null)
                {
                    var upd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
                    upd.Tick += (s, e) => { upd.Interval = TimeSpan.FromHours(6); DateTime last; if (!DateTime.TryParse(S.LastUpdateCheck, out last) || (DateTime.Now - last).TotalHours >= 24) CheckForUpdate(false); };
                    upd.Start();
                }
                ui.BeginInvoke(new Action(ShowCrashNotice), DispatcherPriority.ApplicationIdle);
            }
            WriteStatus();
            statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) }; int n = 0;
            statusTimer.Tick += (s, e) => { if (statusDirty || ++n >= 6) { statusDirty = false; n = 0; WriteStatus(); } }; statusTimer.Start();
            if (!trayOnly && !MailSettings.Headless) ShowMain(null);
        }
        public void Log(string s) { E.Note(s); }

        // ---------------- window
        public void ShowMain(string page)
        {
            if (MailSettings.Headless || SnapMode) return;
            if (Main == null) { ApplyLook(); Main = new MainWindow(this); }
            if (page != null) Main.Show(page);
            Main.Show(); if (Main.WindowState == WindowState.Minimized) Main.WindowState = WindowState.Normal; Main.Activate();
        }
        public void MainClosed()
        {
            Main = null;
            if (!S.TrayHintShown && !quitting) { S.TrayHintShown = true; S.Save(); Notify("Hallow Email is still here", "It keeps your mail in sync from the tray. Right-click the icon to quit.", true); }
        }
        public void Compose(string to)
        {
            if (MailSettings.Headless || SnapMode) { Log("compose to " + (string.IsNullOrEmpty(to) ? "(nobody)" : "an address")); return; }
            if (P.Accounts.Count == 0) { ShowMain("accounts"); Say("Add an account first."); return; }
            var w = new ComposeWindow(this, ComposeState.New(this, to)); w.Show(); w.Activate();
        }
        public void Quit()
        {
            if (quitting) return; quitting = true;
            try { E.StopAll(); } catch { }
            try { P.Save(); foreach (var a in P.Accounts) Store.Save(a.Id); } catch { }
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            WriteStatus(false);
            if (Main != null) Main.Exiting = true;
            Application.Current.Shutdown();
        }
        public void Say(string text) { if (Main != null) Main.Say(text); Log("say: " + text); }

        // ---------------- new mail: a quiet note (through Hallow Hub when it hosts the icon) for People only; nothing while you play
        void NewMail(List<MailItem> l)
        {
            if (Main != null) Main.MailChanged();
            string why; var note = NotePlan(l, S, () => Games.Running(S.ExtraGames), out why);
            if (note == null) { if (why != null) Log(why); return; }
            Notify(note[0], note[1], false);
        }
        // what to say about new mail (People, or a rule's "notify me"), or null: switched off, or a game is running
        public static string[] NotePlan(List<MailItem> l, MailSettings s, Func<bool> game, out string why)
        {
            why = null;
            var show = l.Where(m => !m.Screened && m.SpamWhy == null).ToList();
            if (show.Count == 0 || !s.NotifyPeople) return null;
            if (!s.NotifyDuringGames && game()) { why = "new mail note held: a game is running"; return null; }
            var first = show[0];
            return new[] { show.Count == 1 ? "New mail from " + (first.Sender ?? "someone") : show.Count + " new messages", show.Count == 1 ? (first.Subject ?? "") : "From " + string.Join(", ", show.Select(m => m.Sender).Distinct().Take(3)) };
        }

        // ---------------- tray, the Hallow icon (Hub hosts it), notifications
        void SetupTray()
        {
            tray = new WF.NotifyIcon { Text = "Hallow Email", Visible = true };
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            var menu = new WF.ContextMenuStrip();
            var open = new WF.ToolStripMenuItem("Open Hallow Email"); open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold); open.Click += (s, e) => ShowMain("inbox"); menu.Items.Add(open);
            foreach (var it in MenuItems().Cast<Dictionary<string, object>>())
            {
                var id = (string)it["id"]; if (id == "-") { menu.Items.Add(new WF.ToolStripSeparator()); continue; }
                var mi = new WF.ToolStripMenuItem((string)it["label"]); mi.Click += (s, e) => Invoke(id); menu.Items.Add(mi);
            }
            menu.Items.Add(new WF.ToolStripSeparator());
            var quit = new WF.ToolStripMenuItem("Quit"); quit.Click += (s, e) => Quit(); menu.Items.Add(quit);
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowMain(null); };
        }
        public void CheckHost()
        {
            if (tray == null) return;
            if (!S.UseHallowIcon) { SetHosted(false, false); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool on = false, lists = false;
                try { var r = Family.Send("Hub", "tray-host", 1500); if (r != null && r.StartsWith("ok {")) HubHosting(r.Substring(3), out on, out lists); } catch { }
                ui.BeginInvoke(new Action(() => SetHosted(on && S.UseHallowIcon, lists)));
            });
        }
        // Hub's "tray-host" answer: on = Hub's Hallow icon is showing; lists = Hub's menu has Hallow Email ("apps" has "Email", or
        // "generic":true). Email only hides its own icon when Hub lists it.
        public static void HubHosting(string json, out bool on, out bool lists)
        {
            on = false; lists = false;
            try
            {
                var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); object v;
                on = d != null && d.TryGetValue("on", out v) && v is bool && (bool)v;
                if (!on) return;
                if (d.TryGetValue("generic", out v) && v is bool && (bool)v) { lists = true; return; }
                if (d.TryGetValue("apps", out v) && v is System.Collections.IEnumerable && !(v is string))
                    foreach (var a in (System.Collections.IEnumerable)v) if (a is string && string.Equals((string)a, "Email", StringComparison.OrdinalIgnoreCase)) lists = true;
            }
            catch { on = false; lists = false; }
        }
        void SetHosted(bool on, bool lists) { hubHosts = on; hubLists = on && lists; if (tray != null && !balloonOwn) tray.Visible = !hubLists; }
        public void Notify(string title, string text, bool own)
        {
            Notifications++; Log("notify: " + (title.StartsWith("New mail") ? "new mail" : title));
            lock (NotifyLog) NotifyLog.Add(new[] { title, text });
            if (tray == null || MailSettings.TestDir != null) return;
            if (!hubHosts || own && !hubLists) { tray.ShowBalloonTip(5000, title, text, WF.ToolTipIcon.None); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (Family.Error(Family.Send("Hub", "notify Email\t" + title + "\t" + text, 2000), "no answer") == null) return;
                ui.BeginInvoke(new Action(() => { balloonOwn = true; tray.Visible = true; tray.ShowBalloonTip(5000, title, text, WF.ToolTipIcon.None); var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) }; t.Tick += (s, e) => { t.Stop(); balloonOwn = false; SetHosted(hubHosts, hubLists); }; t.Start(); }));
            });
        }
        // the accent and light / dark: reloaded only when one of them changed
        public void ApplyLook()
        {
            if (SnapMode) return;
            var a = Accent.Current(S.UseHallowAccent); var light = Look.Current(S);
            if (a == Program.AccentHex && light == Program.Light) return;
            Program.LoadTheme(Application.Current, S); Log("look: " + (Program.Light ? "light" : "dark"));
            if (Main != null) { Main.Rebuild(); Ui.TitleBar(Main); }
            foreach (Window w in Application.Current.Windows) { var cw = w as ComposeWindow; if (cw != null) cw.Rebuild(); }
        }
        public void SetLook(string look) { S.Look = look == "light" || look == "dark" ? look : "hallow"; S.Save(); ApplyLook(); }
        GhRelease pending; public GhRelease PendingUpdate { get { return pending; } }
        public void CheckForUpdate(bool manual)
        {
            if (manual) Say("Checking GitHub for a newer Hallow Email...");
            new Thread(() =>
            {
                var r = Updater.Latest();
                ui.BeginInvoke(new Action(() =>
                {
                    S.LastUpdateCheck = DateTime.Now.ToString("o"); S.Save();
                    if (r == null) { if (manual) Say("Couldn't reach GitHub - try again later."); return; }
                    if (!Updater.IsNewer(r)) { if (manual) Say("You have the newest version (v" + Program.Version + ")."); return; }
                    pending = r; if (Main != null) Main.ShowUpdate("Update to v" + r.Version.ToString(2));
                    Say("Hallow Email v" + r.Version.ToString(2) + " is out - click Update.");
                }));
            }) { IsBackground = true }.Start();
        }
        public void InstallUpdate()
        {
            if (pending == null) return;
            if (!Dialogs.Confirm(Main, "Install Hallow Email v" + pending.Version.ToString(2) + "?", "It downloads from GitHub, checks the file and puts the new version in place. Your accounts, mail cache and settings stay." + (P.Outbox.Count > 0 ? " Mail waiting to be sent stays queued." : ""), "Update", false)) return;
            var r = pending; Say("Updating...");
            new Thread(() => { var err = Updater.Install(r); ui.BeginInvoke(new Action(() => Say(err ?? "Installing the update..."))); }) { IsBackground = true }.Start();
        }
        void ShowCrashNotice()
        {
            var f = Crash.Last(); if (f == null) return;
            var name = Path.GetFileName(f); if (name == S.LastCrashShown) return;
            S.LastCrashShown = name; S.Save();
            CrashNotice.Make(f).Show();
        }
        const string RunName = "HallowEmail";
        string RunFile { get { return Path.Combine(MailSettings.Dir, "test-autostart.txt"); } }
        public bool StartsWithWindows()
        {
            if (MailSettings.TestDir != null || SnapMode) return File.Exists(RunFile);
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return k != null && k.GetValue(RunName) != null; } catch { return false; }
        }
        public void SetStartWithWindows(bool on)
        {
            if (SnapMode) return;
            if (MailSettings.TestDir != null) { if (on) File.WriteAllText(RunFile, "\"HallowEmail.exe\" --tray"); else if (File.Exists(RunFile)) File.Delete(RunFile); return; }
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) { if (on) k.SetValue(RunName, "\"" + Assembly.GetExecutingAssembly().Location + "\" --tray"); else k.DeleteValue(RunName, false); } } catch { }
        }

        // ---------------- accounts
        public void AddAccount(Account a, string password, string refreshToken, string clientSecret)
        {
            lock (P) { P.Accounts.RemoveAll(x => x.Id == a.Id); P.Accounts.Add(a); }
            if (password != null) Secrets.Set("pw:" + a.Id, password);
            if (refreshToken != null) Secrets.Set("rt:" + a.Id, refreshToken);
            if (clientSecret != null) Secrets.Set("cs:" + a.Id, clientSecret);
            lock (P) { var me = (a.Email ?? "").ToLowerInvariant(); if (me.Length > 0 && !P.Trusted.Contains(me)) P.Trusted.Add(me); }
            P.Save(); E.StopAccount(a.Id); E.StartAccount(a); Log("account added (" + (a.Preset ?? "custom") + ")"); WriteStatus();
        }
        public void RemoveAccount(Account a)
        {
            E.StopAccount(a.Id); lock (P) P.Accounts.RemoveAll(x => x.Id == a.Id); Secrets.RemoveAccount(a.Id); Store.Forget(a.Id);
            lock (P) P.Outbox.RemoveAll(q => q.Account == a.Id);
            P.Save(); Log("account removed"); WriteStatus();
        }

        // ---------------- family link: status.json and the pipe
        public Dictionary<string, object> StatusData(bool running)
        {
            var acc = E.UnreadPerAccount();
            var d = new Dictionary<string, object> { { "app", "Email" }, { "version", Program.Version }, { "updated", Family.Time(DateTime.Now) }, { "running", running },
                { "accounts", acc }, { "unread", acc.Sum(x => (int)x["unread"]) }, { "needsAttention", acc.Count(x => (bool)x["needsAttention"]) } };
            lock (P) { d["outbox"] = P.Outbox.Count; d["snoozed"] = P.Snoozed.Count; }
            d["screener"] = S.Screener; d["spamFilter"] = S.SpamFilter; d["look"] = Program.Light ? "light" : "dark"; d["startsWithWindows"] = StartsWithWindows();
            d["lastCrash"] = Crash.LastTime() == null ? null : Family.Time(Crash.LastTime().Value);
            if (MailSettings.TestDir != null) { lock (E.Log) d["log"] = E.Log.ToArray(); d["notifications"] = Notifications; }
            return d;
        }
        public void WriteStatus(bool running = true) { if (!SnapMode) Family.WriteStatus(Family.Json(StatusData(running))); }
        string Command(string line)
        {
            int sp = line.IndexOf(' '); var cmd = sp < 0 ? line : line.Substring(0, sp); var arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            switch (cmd)
            {
                case "ping": return "ok " + Family.Json(new Dictionary<string, object> { { "app", "Email" }, { "version", Program.Version } });
                case "status": { var d = StatusData(true); Family.WriteStatus(Family.Json(d)); return "ok " + Family.Json(d); }
                // 1.1: just the accounts (opaque id, label, unread, attention) for Remote / Hub
                case "accounts": { var acc = E.UnreadPerAccount(); return "ok " + Family.Json(new Dictionary<string, object> { { "app", "Email" }, { "accounts", acc }, { "unread", acc.Sum(x => (int)x["unread"]) }, { "needsAttention", acc.Count(x => (bool)x["needsAttention"]) } }); }
                case "open": return OnUi(() => { var p = arg.Length > 0 ? MainWindow.PageId(arg) : null; if (arg.Length > 0 && p == null) return "error no page called " + arg; ShowMain(p); return "ok"; });
                case "compose":
                    {
                        var to = arg.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(arg.Substring(7)) : arg;
                        if (to.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(to.Split('?')[0], @"^[^@\s,;<>]+@[^@\s,;<>]+\.[^@\s,;<>]+$")) return "error that isn't an email address";
                        return OnUi(() => { Compose(to); return "ok"; });
                    }
                case "menu": return "ok " + Family.Json(new Dictionary<string, object> { { "app", "Email" }, { "items", MenuItems() } });
                case "invoke": return OnUi(() => Invoke(arg));
                case "sync": E.SyncNow(); return "ok";
                case "quit": new Thread(() => { Thread.Sleep(300); ui.BeginInvoke(new Action(Quit)); }) { IsBackground = true }.Start(); return "ok";
            }
            if (MailSettings.TestDir != null && cmd.StartsWith("test-")) return OnUi(() => TestCommand(cmd, arg));
            return "error unknown command";
        }
        string OnUi(Func<string> f)
        {
            string r = "error no answer in time";
            var op = ui.BeginInvoke(new Action(() => { try { r = f(); } catch (Exception e) { r = "error " + Family.OneLine(e.Message); } }));
            return op.Wait(TimeSpan.FromSeconds(5)) == DispatcherOperationStatus.Completed ? r : "error no answer in time";
        }
        public List<object> MenuItems()
        {
            return new List<object> {
                new Dictionary<string, object> { { "id", "compose" }, { "label", "New email" } },
                new Dictionary<string, object> { { "id", "inbox" }, { "label", "Inbox" } },
                new Dictionary<string, object> { { "id", "sync" }, { "label", "Check for mail now" } },
                new Dictionary<string, object> { { "id", "-" } },
                new Dictionary<string, object> { { "id", "unsubscribe" }, { "label", "Unsubscribe finder" } },
                new Dictionary<string, object> { { "id", "screener" }, { "label", "Screener" } } };
        }
        string Invoke(string id)
        {
            switch (id)
            {
                case "compose": Compose(null); return "ok";
                case "inbox": ShowMain("inbox"); return "ok";
                case "sync": E.SyncNow(); Say("Checking for mail..."); return "ok";
                case "unsubscribe": ShowMain("unsubscribe"); return "ok";
                case "screener": ShowMain("screener"); return "ok";
            }
            return "error unknown item";
        }
        // developer tests only (HALLOW_EMAIL_TESTDIR): what a person would click, without windows
        string TestCommand(string cmd, string arg)
        {
            switch (cmd)
            {
                case "test-look": ApplyLook(); return "ok " + (Program.Light ? "light" : "dark");
                case "test-setlook": SetLook(arg); return "ok " + (Program.Light ? "light" : "dark");
                case "test-autostart": SetStartWithWindows(arg == "on"); return "ok " + StartsWithWindows();
                case "test-notes": lock (NotifyLog) return "ok " + Family.Json(NotifyLog.Select(n => n[0]).ToList());
                case "test-items": return "ok " + Store.All().Count;
                case "test-account":
                    {
                        // test copies only: "<id> <preset> <imap port> <smtp port> <password> [label...]" - an account on this PC's IMAP / SMTP stand-in
                        var a = arg.Split(new[] { ' ' }, 6); if (a.Length < 5) return "error use: test-account <id> <preset> <imap port> <smtp port> <password> [label]";
                        var acc = new Account { Id = a[0], Email = "alex@example.test", Name = "Alex", Label = a.Length > 5 ? a[5] : "", Preset = a[1], ImapHost = "127.0.0.1", ImapPort = int.Parse(a[2]), ImapSecurity = "none", SmtpHost = "127.0.0.1", SmtpPort = int.Parse(a[3]), SmtpSecurity = "none", SendAs = new List<string>() };
                        AddAccount(acc, a[4], null, null); return "ok";
                    }
            }
            return "error unknown test command";
        }
    }

    // games: no notes while one runs (Delta Force, Discovery, your own list, and what Hallow Hub says you're playing)
    public static class Games
    {
        public static readonly string[] Known = { "DeltaForceClient-Win64-Shipping", "Discovery-d" };
        public static Func<bool> Override;   // tests
        public static bool Running(IEnumerable<string> extra)
        {
            if (Override != null) return Override();
            try
            {
                foreach (var n in Known.Concat((extra ?? new string[0]).Select(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x.Substring(0, x.Length - 4) : x)))
                { var p = Process.GetProcessesByName(n); var any = p.Length > 0; foreach (var x in p) x.Dispose(); if (any) return true; }
                var hub = Path.Combine(Family.DataDir("Hub"), "status.json");
                if (File.Exists(hub))
                {
                    var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(hub)); object v;
                    if (d != null && d.TryGetValue("running", out v) && v is bool && (bool)v && d.TryGetValue("playing", out v) && v is System.Collections.ICollection && ((System.Collections.ICollection)v).Count > 0) return true;
                }
            }
            catch { }
            return false;
        }
    }
}
