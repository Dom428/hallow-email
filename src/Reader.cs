// Hallow Email - the reading pane. HTML mail is cleaned first (Privacy.cs) and then shown in a locked-down Microsoft Edge WebView2:
// scripts off, no context-menu "inspect", no autofill, every network request refused except https images you allowed for that sender
// (and never a tracker), links never navigate inside it - a click asks first and opens your browser. Without WebView2 (or with
// Settings > "Always show mail as text") a plain-text view is used. Phishing hints, the privacy bar and attachment flags sit above it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HallowEmail
{
    public class Reader : IDisposable
    {
        readonly MainWindow w; readonly Controller c;
        public readonly DockPanel Root = new DockPanel();
        MailItem item; BodyView body; CleanHtml clean; bool remoteOk;
        WebView2 web; bool webReady; static bool? webAvailable; static CoreWebView2Environment env;
        public string LastHtml;   // tests / snap
        public Reader(MainWindow w)
        {
            this.w = w; c = w.c;
            Root.Children.Add(Empty());
        }
        FrameworkElement Empty()
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var ic = Icons.Make("envelope-open", 42, Ui.R("Faint")); ic.Margin = new Thickness(0, 0, 0, 12); sp.Children.Add(ic);
            sp.Children.Add(new TextBlock { Text = "Pick a message to read it", Foreground = Ui.R("Muted"), HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = "Remote images and trackers stay blocked until you allow them.", Foreground = Ui.R("Faint"), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
            return sp;
        }
        public static bool WebView2Available()
        {
            if (webAvailable.HasValue) return webAvailable.Value;
            try { webAvailable = !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); } catch { webAvailable = false; }
            return webAvailable.Value;
        }

        public void Show(MailItem m, List<MailItem> conv)
        {
            item = m; body = null; clean = null;
            string err; body = c.E.Open(m, out err);
            if (body == null) { Root.Children.Clear(); Root.Children.Add(Ui.Muted(err ?? "Couldn't open it.", 12.5, 0)); ((FrameworkElement)Root.Children[0]).Margin = new Thickness(24); return; }
            m = c.Store.Find(m.Key) ?? m; item = m;
            bool first; lock (c.P) first = c.P.Known.Count(k => k == (m.FromAddr ?? "").ToLowerInvariant()) == 0 || c.Store.All().Count(i => string.Equals(i.FromAddr, m.FromAddr, StringComparison.OrdinalIgnoreCase)) <= 1;
            lock (c.P) remoteOk = m.FromAddr != null && c.P.ImageSenders.Contains(m.FromAddr.ToLowerInvariant());
            Build(m, conv, first);
        }

        void Build(MailItem m, List<MailItem> conv, bool firstTime)
        {
            Root.Children.Clear();
            var top = new StackPanel { Margin = new Thickness(20, 14, 20, 8) };
            // actions
            var acts = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            acts.Children.Add(Ui.Btn("Reply", () => ComposeWindow.Reply(c, m, false)));
            acts.Children.Add(Ui.Btn("Reply all", () => ComposeWindow.Reply(c, m, true)));
            acts.Children.Add(Ui.Btn("Forward", () => ComposeWindow.Forward(c, m, body)));
            acts.Children.Add(Ui.Btn("Archive", () => w.Act(() => c.E.Archive(m))));
            acts.Children.Add(Ui.Btn("Delete", () => w.Act(() => c.E.Trash(m))));
            bool inJunk = w.Page == "spam" || !string.IsNullOrEmpty(m.SpamWhy) && m.Folder.ToLowerInvariant().Contains("junk") || m.Folder.ToLowerInvariant().Contains("spam");
            acts.Children.Add(inJunk ? Ui.Btn("Not spam", () => w.Act(() => c.E.MarkSpam(m, false))) : Ui.Btn("Spam", () => w.Act(() => c.E.MarkSpam(m, true))));
            acts.Children.Add(Ui.Btn(m.Flagged ? "Unstar" : "Star", () => { c.E.SetStar(m, !m.Flagged); Build(m, conv, firstTime); }));
            var snooze = Ui.Btn("Snooze", null); snooze.Click += (s, e) => SnoozeMenu(snooze, m); acts.Children.Add(snooze);
            var more = Ui.Btn("More", null); more.Click += (s, e) => MoreMenu(more, m); acts.Children.Add(more);
            top.Children.Add(acts);
            if (m.Screened)
            {
                var scr = new WrapPanel();
                scr.Children.Add(new TextBlock { Text = "First mail from " + m.FromAddr + ". Let them in?", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0) });
                scr.Children.Add(Ui.Btn("Yes, let them in", () => PageBuilder.ScreenerAnswer(w, m, true), true));
                scr.Children.Add(Ui.Btn("No - archive their mail", () => PageBuilder.ScreenerAnswer(w, m, false)));
                top.Children.Add(Bar(scr, "Lilac", "LilacBg"));
            }
            top.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(m.Subject) ? "(no subject)" : m.Subject, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            var from = new WrapPanel();
            from.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(m.FromName) ? m.FromAddr : m.FromName, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0) });
            from.Children.Add(new TextBlock { Text = "<" + m.FromAddr + ">", Foreground = Ui.R("Muted"), FontFamily = Ui.Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            top.Children.Add(from);
            var acc = c.P.Find(m.Account);
            top.Children.Add(Ui.Muted("To " + (m.To ?? "") + (string.IsNullOrEmpty(m.Cc) ? "" : "  Cc " + m.Cc) + "   " + m.When.ToString("ddd d MMM yyyy, HH:mm") + (acc != null && c.P.Accounts.Count > 1 ? "   in " + acc.Display : ""), 11.5, 6));
            var labels = c.E.LabelsOf(m);
            if (labels.Count > 0) { var lp = new WrapPanel(); foreach (var l in labels) lp.Children.Add(Ui.Pill(l, "AccentBrush", "AccentGlow")); top.Children.Add(lp); }
            // the conversation
            if (conv != null && conv.Count > 1)
            {
                var cp = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
                cp.Children.Add(Ui.Caps(conv.Count + " messages in this conversation", 10));
                foreach (var x in conv.Take(12))
                {
                    var xx = x; var b = new Button { Style = Ui.S("Head"), HorizontalContentAlignment = HorizontalAlignment.Left, Content = (x.Key == m.Key ? "> " : "   ") + x.Sender + " - " + MainWindow.When(x.When) + (x.Seen ? "" : "  (new)"), FontWeight = x.Key == m.Key ? FontWeights.SemiBold : FontWeights.Normal };
                    b.Click += (s, e) => { w.Current = xx.Key; Show(xx, conv); };
                    cp.Children.Add(b);
                }
                top.Children.Add(cp);
            }
            // spam reason, phishing hints
            if (!string.IsNullOrEmpty(m.SpamWhy)) top.Children.Add(Bar(Ui.T("Why is this spam? " + m.SpamWhy, 12, false, Ui.R("Text")), "Red", "RedBg"));
            foreach (var h in Phish.Check(m, firstTime))
            {
                var key = h.Level == "red" ? "Red" : h.Level == "amber" ? "Amber" : h.Level == "green" ? "Green" : "Lilac";
                var icon = h.Level == "red" || h.Level == "amber" ? "triangle-exclamation" : h.Level == "green" ? "circle-check" : "circle-info";
                var dp = new DockPanel(); var ic = Icons.Make(icon, 13, Ui.R(key)); ic.Margin = new Thickness(0, 1, 8, 0); ic.VerticalAlignment = VerticalAlignment.Top; dp.Children.Add(ic);
                dp.Children.Add(Ui.T(h.Text, 12, false, Ui.R("Text")));
                top.Children.Add(Bar(dp, key, key + "Bg"));
            }
            // privacy
            if (body.Html != null)
            {
                Func<string, string> cid = id => { string d; return body.Cid.TryGetValue(id, out d) ? d : null; };
                clean = Privacy.Sanitize(body.Html, remoteOk, cid, !Program.Light);
                var pv = new WrapPanel();
                var msg = clean.Trackers > 0 ? clean.Trackers + " tracker" + (clean.Trackers == 1 ? "" : "s") + " blocked" + (clean.TrackerHosts.Count > 0 ? " (" + string.Join(", ", clean.TrackerHosts.Take(3)) + ")" : "") : "No trackers found";
                var imgs = clean.RemoteImages - clean.Trackers;
                if (!remoteOk && imgs > 0) msg += " - " + imgs + " remote image" + (imgs == 1 ? "" : "s") + " blocked";
                else if (remoteOk) msg += " - images from this sender are on";
                var pdp = new DockPanel(); var sh = Icons.Make("shield-halved", 13, Ui.R("Green")); sh.Margin = new Thickness(0, 0, 8, 0); pdp.Children.Add(sh); pdp.Children.Add(Ui.T(msg, 12, false, Ui.R("Text")));
                pv.Children.Add(pdp);
                if (!remoteOk && imgs > 0) { var lb = Ui.Btn("Load images from this sender", () => SetImages(m, true, conv, firstTime)); lb.Margin = new Thickness(12, 0, 0, 0); pv.Children.Add(lb); }
                else if (remoteOk) { var lb = Ui.Btn("Block images again", () => SetImages(m, false, conv, firstTime)); lb.Margin = new Thickness(12, 0, 0, 0); pv.Children.Add(lb); }
                top.Children.Add(Bar(pv, "Green", "GreenBg"));
                var mm = clean.Mismatches;
                if (mm.Count > 0) top.Children.Add(Bar(Ui.T(mm.Count + " link" + (mm.Count == 1 ? "" : "s") + " say one site but go to another - e.g. \"" + Trim(mm[0].Text, 40) + "\" really goes to " + mm[0].Host + ". Hover a link to see where it goes.", 12, false, Ui.R("Text")), "Amber", "AmberBg"));
            }
            // attachments
            if (body.Attachments.Count > 0)
            {
                var ap = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
                foreach (var a in body.Attachments)
                {
                    var aa = a; var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                    var btns = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(btns, Dock.Right);
                    btns.Children.Add(Ui.Btn("Open", () => OpenAttachment(aa)));
                    btns.Children.Add(Ui.Btn("Save as...", () => SaveAttachment(aa)));
                    row.Children.Add(btns);
                    var info = new StackPanel();
                    info.Children.Add(Icons.WithText(a.Info.Dangerous ? "triangle-exclamation" : "paperclip", a.Name + "  (" + Size(a.Data.Length) + ")", 12, Ui.R(a.Info.Dangerous ? "Red" : "Text")));
                    foreach (var f in a.Info.Flags) info.Children.Add(new TextBlock { Text = f.Text, FontSize = 11.5, Foreground = Ui.R(f.Level == "red" ? "Red" : "Amber"), Margin = new Thickness(19, 0, 0, 0), TextWrapping = TextWrapping.Wrap });
                    row.Children.Add(info); ap.Children.Add(row);
                }
                top.Children.Add(new Border { Child = ap, BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 6, 0, 0), Margin = new Thickness(0, 4, 0, 0) });
            }
            var topScroll = new ScrollViewer { Content = top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360 };
            DockPanel.SetDock(topScroll, Dock.Top); Root.Children.Add(topScroll);
            Root.Children.Add(BodyHost());
        }
        static string Trim(string s, int n) { s = s ?? ""; return s.Length > n ? s.Substring(0, n) + "..." : s; }
        static string Size(long b) { return b < 1024 ? b + " B" : b < 1048576 ? (b / 1024.0).ToString("0") + " KB" : (b / 1048576.0).ToString("0.0") + " MB"; }
        Border Bar(UIElement child, string fg, string bg) { return new Border { Child = child, Background = Ui.R(bg), BorderBrush = Ui.R(fg), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 4, 0, 2) }; }
        void SetImages(MailItem m, bool on, List<MailItem> conv, bool first)
        {
            var a = (m.FromAddr ?? "").ToLowerInvariant(); if (a.Length == 0) return;
            lock (c.P) { c.P.ImageSenders.Remove(a); if (on) c.P.ImageSenders.Add(a); }
            c.P.Save(); remoteOk = on; Build(m, conv, first);
        }

        // ---------------- the body
        FrameworkElement BodyHost()
        {
            var html = body.Html != null ? clean.Html : Privacy.TextToHtml(body.Text ?? "", !Program.Light);
            LastHtml = html;
            if (Snap.On || c.S.SafeTextView || !WebView2Available()) return TextView();
            var host = new Grid { Background = Ui.R("Bg") };
            if (web == null)
            {
                web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(Program.Light ? 0xF6 : 0x1C, Program.Light ? 0xF4 : 0x17, Program.Light ? 0xFA : 0x26) };
                Init(html);
            }
            else { var p = web.Parent as Panel; if (p != null) p.Children.Remove(web); if (webReady) web.CoreWebView2.NavigateToString(html); }
            host.Children.Add(web);
            return host;
        }
        FrameworkElement TextView()
        {
            var text = body.Text ?? Privacy.ToText(body.Html ?? "");
            var tb = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Ui.R("Panel"), Foreground = Ui.R("Text"), FontSize = 13.5, Padding = new Thickness(18), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = (FontFamily)Application.Current.FindResource("BodyFont") };
            return new Border { Child = tb, Margin = new Thickness(20, 4, 20, 16), BorderBrush = Ui.R("Line"), BorderThickness = new Thickness(1) };
        }
        async void Init(string html)
        {
            try
            {
                if (env == null)
                {
                    var data = Path.Combine(MailSettings.TestDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HallowEmail"), "WebView2");
                    var opt = new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-sync --no-pings");
                    env = await CoreWebView2Environment.CreateAsync(null, data, opt);
                }
                await web.EnsureCoreWebView2Async(env);
                Lockdown(web.CoreWebView2, env, () => remoteOk, u => w.Dispatcher.BeginInvoke(new Action(() => Link(u))));
                webReady = true;
                web.CoreWebView2.NavigateToString(LastHtml ?? html);
            }
            catch (Exception e) { c.Log("WebView2: " + e.Message); webAvailable = false; if (item != null) { Root.Children.RemoveAt(Root.Children.Count - 1); Root.Children.Add(TextView()); } }
        }
        // the reader's WebView2 rules (also used by the tests): scripts off, nothing remote unless allowed, links never navigate here
        public static void Lockdown(CoreWebView2 cw, CoreWebView2Environment env, Func<bool> remoteOk, Action<string> link)
        {
            var st = cw.Settings;
            st.IsScriptEnabled = false; st.IsWebMessageEnabled = false; st.AreHostObjectsAllowed = false; st.AreDevToolsEnabled = false;
            st.IsStatusBarEnabled = true;   // a link's real address shows at the bottom on hover
            st.IsGeneralAutofillEnabled = false; st.IsPasswordAutosaveEnabled = false; st.AreBrowserAcceleratorKeysEnabled = false; st.IsBuiltInErrorPageEnabled = false;
            try { cw.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict; } catch { }
            cw.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            cw.WebResourceRequested += (s, e) =>
            {
                var u = e.Request.Uri; Uri uri;
                bool ok = remoteOk() && Uri.TryCreate(u, UriKind.Absolute, out uri) && uri.Scheme == "https" && e.ResourceContext == CoreWebView2WebResourceContext.Image && !Privacy.IsTracker(u, -1, -1, null);
                if (!ok) e.Response = env.CreateWebResourceResponse(null, 403, "Blocked by Hallow Email", "");
            };
            cw.NavigationStarting += (s, e) =>
            {
                if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || e.Uri == "about:blank") return;
                e.Cancel = true; link(e.Uri);
            };
            cw.NewWindowRequested += (s, e) => { e.Handled = true; link(e.Uri); };
            cw.DownloadStarting += (s, e) => { e.Cancel = true; };
            cw.PermissionRequested += (s, e) => { e.State = CoreWebView2PermissionState.Deny; };
        }
        // a click on a link: mailto opens a new email; web links ask first (and say so when the text named another site)
        void Link(string url)
        {
            if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) { c.Compose(Uri.UnescapeDataString(url.Substring(7))); return; }
            if (!Privacy.SafeLink(url)) { w.Say("That link was blocked (it isn't a web or mail address)."); return; }
            var host = Privacy.HostOf(url);
            var lc = clean == null ? null : clean.Links.FirstOrDefault(l => l.Href == url && l.Mismatch);
            var text = "It opens in your web browser:\n" + url + (lc != null ? "\n\nCareful: the link's text says " + lc.TextHost + ", but it really goes to " + host + "." : "");
            if (!Dialogs.Confirm(w, "Open " + host + "?", text, "Open in browser", lc != null)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception e) { w.Say("Couldn't open it: " + e.Message); }
        }

        // ---------------- attachments: flagged ones ask first; everything is scanned with Microsoft Defender before it opens
        string TempDir { get { return Path.Combine(MailSettings.Dir, "open"); } }
        void OpenAttachment(AttachPart a)
        {
            if (a.Info.Dangerous && !Dialogs.Confirm(w, "Open " + a.Name + "?", string.Join("\n", a.Info.Flags.Select(f => "- " + f.Text)) + "\n\nOnly open it if you were expecting it from this sender. It's scanned with Microsoft Defender first.", "Scan and open", true)) return;
            string path; try { path = Write(a); } catch (Exception e) { w.Say("Couldn't save it: " + e.Message); return; }
            w.Say("Scanning " + a.Name + " with Microsoft Defender...");
            Task.Factory.StartNew(() => { bool clean; var msg = Defender.Scan(path, out clean); return Tuple.Create(clean, msg); }).ContinueWith(t =>
            {
                var r = t.Result; w.Say(r.Item2);
                if (!r.Item1) { try { File.Delete(path); } catch { } Dialogs.Info(w, "Not opened", r.Item2); return; }
                if (MailSettings.TestDir != null) return;
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception e) { w.Say("Windows couldn't open it: " + e.Message); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        string Write(AttachPart a)
        {
            Directory.CreateDirectory(TempDir);
            foreach (var old in Directory.GetFiles(TempDir)) try { if (File.GetCreationTime(old) < DateTime.Now.AddDays(-1)) File.Delete(old); } catch { }
            var name = string.Join("_", (a.Name ?? "attachment").Split(Path.GetInvalidFileNameChars())).Trim(); if (name.Length == 0) name = "attachment";
            var p = Path.Combine(TempDir, name); for (int i = 2; File.Exists(p); i++) p = Path.Combine(TempDir, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
            File.WriteAllBytes(p, a.Data); return p;
        }
        void SaveAttachment(AttachPart a)
        {
            var d = new Microsoft.Win32.SaveFileDialog { FileName = a.Name, Title = "Save attachment" };
            if (d.ShowDialog(w) != true) return;
            try { File.WriteAllBytes(d.FileName, a.Data); w.Say("Saved. " + (a.Info.Flags.Count > 0 ? "Careful with it: " + a.Info.Flags[0].Text : "")); } catch (Exception e) { w.Say("Couldn't save it: " + e.Message); }
        }

        void SnoozeMenu(Button b, MailItem m)
        {
            var menu = new ContextMenu { PlacementTarget = b };
            Action<string, DateTime> add = (t, d) => { var mi = new MenuItem { Header = t + "  (" + d.ToString("ddd HH:mm") + ")" }; mi.Click += (s, e) => { c.E.SnoozeIt(m, d); w.Say("Snoozed until " + d.ToString("ddd d MMM HH:mm") + ". It comes back as a reminder."); w.Current = null; w.MailChanged(); }; menu.Items.Add(mi); };
            var now = DateTime.Now;
            add("Later today", now.AddHours(3)); add("This evening", now.Date.AddHours(now.Hour >= 18 ? 42 : 18)); add("Tomorrow morning", now.Date.AddDays(1).AddHours(8));
            int toMon = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7; if (toMon == 0) toMon = 7; add("Next week", now.Date.AddDays(toMon).AddHours(8));
            menu.IsOpen = true;
        }
        void MoreMenu(Button b, MailItem m)
        {
            var menu = new ContextMenu { PlacementTarget = b };
            Action<string, Action> add = (t, a) => { var mi = new MenuItem { Header = t }; mi.Click += (s, e) => a(); menu.Items.Add(mi); };
            add("Mark as unread", () => { c.E.SetSeen(m, false); w.Current = null; w.MailChanged(); });
            add("Move to folder...", () => { var f = Dialogs.PickFolder(w, c, m.Account); if (f != null) w.Act(() => c.E.Move(m, null, f)); });
            add("Add a label...", () => { var l = Dialogs.Ask(w, "Add a label", "A label is kept on this PC (and on the server too when it supports IMAP keywords).", ""); if (string.IsNullOrWhiteSpace(l)) return; c.E.AddLabel(m, l.Trim()); c.P.Save(); c.E.Do(m.Account, cl => c.E.W(m.Account).Keywords(cl, m, new List<string> { l.Trim() })); Show(m, null); });
            add("Create rule from this email...", () => PageBuilder.EditRule(w, Rules.FromMail(m), true));
            foreach (var cat in Sorter.Categories) { var cc = cat; if (cc != (m.Category ?? "people")) add("Always show this sender in " + Sorter.Title(cc), () => w.MoveSender(m, cc)); }
            add("Select all from this sender", () => w.SelectSender(m.FromAddr));
            if (!string.IsNullOrEmpty(m.ListUnsubscribe)) add("Unsubscribe...", () => PageBuilder.UnsubscribeOne(w, m.FromAddr, m.Sender, m.ListUnsubscribe, m.ListUnsubscribePost, m.Account));
            add("Show the headers", () => Dialogs.Info(w, "Headers", HeadersText(m)));
            menu.IsOpen = true;
        }
        string HeadersText(MailItem m)
        {
            if (body == null || body.Message == null) return "";
            return string.Join("\n", body.Message.Headers.Take(60).Select(h => h.Field + ": " + (h.Value.Length > 300 ? h.Value.Substring(0, 300) + "..." : h.Value)));
        }
        public void Dispose() { try { if (web != null) web.Dispose(); } catch { } web = null; }
    }
}
