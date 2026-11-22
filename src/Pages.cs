// Hallow Email - the full pages: Settings (accounts, look, reading, sending, spam filter, Screener, notifications), Rules, the
// Unsubscribe finder, Your addresses (Cloudflare Email Routing + masked addresses) and the Outbox.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MimeKit;

namespace HallowEmail
{
    public static class PageBuilder
    {
        static ScrollViewer Page(StackPanel sp) { return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; }
        static StackPanel Panel(string title, string sub)
        {
            var sp = new StackPanel { Margin = new Thickness(28, 20, 28, 28), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
            sp.Children.Add(Ui.T(title, 20, true, null, 2)); if (sub != null) sp.Children.Add(Ui.Muted(sub, 12.5, 14));
            return sp;
        }
        static Border Section(string caps, params UIElement[] items)
        {
            var sp = new StackPanel(); sp.Children.Add(Ui.Caps(caps, 10.5)); var gap = new Border { Height = 8 }; sp.Children.Add(gap);
            foreach (var i in items) if (i != null) sp.Children.Add(i);
            var card = Ui.Card(sp); card.Margin = new Thickness(0, 0, 0, 14); return card;
        }
        public static FrameworkElement Build(MainWindow w, string page)
        {
            switch (page)
            {
                case "settings": return Settings(w);
                case "rules": return RulesPage(w);
                case "unsubscribe": return Unsub(w);
                case "masked": return Masked(w);
                case "outbox": return Outbox(w);
            }
            return Ui.Muted("Nothing here.", 12);
        }

        // ---------------- settings
        static FrameworkElement Settings(MainWindow w)
        {
            var c = w.c; var S = c.S;
            var sp = Panel("Settings", "Everything Hallow Email keeps is on this PC: mail cache, accounts, rules and the spam filter are encrypted (Windows DPAPI for this user).");
            // accounts
            var acc = new StackPanel();
            foreach (var a in c.P.Accounts)
            {
                var aa = a; var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var b = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(b, Dock.Right);
                b.Children.Add(Ui.Btn("Edit", () => new AccountWizard(c, aa).Show()));
                b.Children.Add(Ui.Btn("Signature...", () => { var s = Dialogs.Ask(w, "Signature for " + aa.Email, "Added to new mail from this account (plain text; use the Signature button while writing). Leave empty for none.", aa.Signature ?? ""); if (s == null) return; aa.Signature = s.Replace("\\n", "\n"); c.P.Save(); w.Say("Signature saved."); }));
                b.Children.Add(Ui.Btn("Send as...", () => { var s = Dialogs.Ask(w, "Extra From addresses for " + aa.Email, "Addresses you can send as through this account's SMTP (comma separated) - for example your own domain routed by Cloudflare. Your provider has to allow it (Gmail: Settings > Accounts > Send mail as).", string.Join(", ", aa.SendAs)); if (s == null) return; aa.SendAs = s.Split(',', ';').Select(x => x.Trim()).Where(x => x.Contains("@")).Distinct().ToList(); c.P.Save(); w.Say("Saved."); }));
                var sens = new ComboBox { Style = Ui.S("Combo"), Width = 170, ToolTip = "Spam filter sensitivity for this account" };
                foreach (var o in new[] { new[] { "low", "Spam filter: low" }, new[] { "normal", "Spam filter: normal" }, new[] { "high", "Spam filter: high" } }) { var it = new ComboBoxItem { Content = o[1], Tag = o[0], Style = Ui.S("ComboItem") }; sens.Items.Add(it); if (o[0] == aa.Sensitivity) sens.SelectedItem = it; }
                sens.SelectionChanged += (s, e) => { aa.Sensitivity = (string)((ComboBoxItem)sens.SelectedItem).Tag; c.P.Save(); };
                sens.Margin = new Thickness(0, 0, 8, 0); b.Children.Add(sens);
                b.Children.Add(Ui.Danger("Remove", () => { if (!Dialogs.Confirm(w, "Remove " + aa.Email + "?", "Hallow Email forgets this account: its password, its cached mail on this PC and any mail waiting to be sent from it. Your mail on the server isn't touched.", "Remove", true)) return; c.RemoveAccount(aa); w.Show("settings"); }));
                row.Children.Add(b);
                var info = new StackPanel();
                info.Children.Add(Icons.WithText(Preset.Get(a.Preset).Icon, a.Display + "  -  " + a.Email, 13, Ui.R("Text")));
                info.Children.Add(Ui.Muted((a.Auth == "password" ? "App password" : "OAuth (" + (a.Auth == "oauth-google" ? "Google" : "Microsoft") + ")") + "  |  " + MainWindow.StateText(Snap.On ? "idle" : c.E.State(a.Id)), 11.5));
                var wk = c.E.W(a.Id); var att = Snap.On || wk == null ? null : wk.AttentionCode;
                if (att != null) { var at = Ui.Muted("Needs attention: " + Attention.Text(att), 11.5); at.Foreground = Ui.R("Amber"); info.Children.Add(at); }
                row.Children.Add(info); acc.Children.Add(row);
            }
            if (c.P.Accounts.Count == 0) acc.Children.Add(Ui.Muted("No accounts yet.", 12.5, 8));
            var add = Ui.Btn("Add an account", () => new AccountWizard(c).Show(), true); add.HorizontalAlignment = HorizontalAlignment.Left; acc.Children.Add(add);
            sp.Children.Add(Section("Accounts", acc));
            // look
            var look = new WrapPanel();
            foreach (var o in new[] { new[] { "hallow", "Follow Hallow" }, new[] { "light", "Light" }, new[] { "dark", "Dark" } })
            { var v = o[0]; var t = new ToggleButton { Style = Ui.S("Chip"), Content = o[1], IsChecked = S.Look == v, Margin = new Thickness(0, 0, 6, 0) }; t.Click += (s, e) => { c.SetLook(v); if (c.Main != null) c.Main.Show("settings"); }; look.Children.Add(t); }
            sp.Children.Add(Section("Look", look, Ui.Muted("Follow Hallow = the Light / Dark / Same as Windows switch in Hallow Toolkit.", 11.5, 6),
                Ui.Switch("Use the Hallow accent colour", S.UseHallowAccent, on => { S.UseHallowAccent = on; S.Save(); c.ApplyLook(); }),
                Ui.Switch("Use the Hallow icon (Hallow Hub's tray menu) instead of my own", S.UseHallowIcon, on => { S.UseHallowIcon = on; S.Save(); c.CheckHost(); })));
            // reading & privacy
            sp.Children.Add(Section("Reading and privacy",
                Ui.Muted("Remote images and tracking pixels are always blocked until you click \"Load images from this sender\" - trackers stay blocked even then. Tracker list: " + Trackers.Source + " (" + Trackers.Count + " hosts).", 12, 6),
                Ui.Switch("Always show mail as plain text (no WebView2)", S.SafeTextView, on => { S.SafeTextView = on; S.Save(); }),
                SenderList(w, "Senders allowed to show images", c.P.ImageSenders)));
            // sorting & spam
            sp.Children.Add(Section("Spam filter and sorting",
                Ui.Switch("Spam filter (private, learns from your Spam / Not spam clicks)", S.SpamFilter, on => { S.SpamFilter = on; S.Save(); }),
                Ui.Muted("Spam is moved to the account's Junk folder with the reason shown - never deleted. People you've written to are never marked as spam. Learned: " + c.P.Bayes.NS + " spam / " + c.P.Bayes.NH + " good messages.", 11.5, 6),
                Ui.Switch("Screener: first-time senders wait until I let them in", S.Screener, on => { S.Screener = on; if (on) Screener.TurnOn(c.P); S.Save(); c.P.Save(); w.BuildSide(); }),
                Ui.Muted("Turning it on lets everyone who has already written to you through; only new senders wait.", 11.5, 6),
                CategoryList(w)));
            // sending
            var undo = new WrapPanel();
            foreach (var n in new[] { 0, 5, 10, 20, 30 }) { var nn = n; var t = new ToggleButton { Style = Ui.S("Chip"), Content = n == 0 ? "Off" : n + " s", IsChecked = S.UndoSeconds == n, Margin = new Thickness(0, 0, 6, 0) }; t.Click += (s, e) => { S.UndoSeconds = nn; S.Save(); w.Show("settings"); }; undo.Children.Add(t); }
            sp.Children.Add(Section("Sending", Ui.Muted("Undo send: how long mail waits before it really goes.", 12, 6), undo,
                Ui.Muted("Send later and snoozes wait on this PC: Hallow Email must be running at that time (it sends anything overdue as soon as it starts).", 11.5, 0)));
            // notifications
            sp.Children.Add(Section("Notifications",
                Ui.Switch("A quiet note for new mail from People (through Hallow Hub's icon when it hosts it)", S.NotifyPeople, on => { S.NotifyPeople = on; S.Save(); }),
                Ui.Switch("Also while a game is running", S.NotifyDuringGames, on => { S.NotifyDuringGames = on; S.Save(); }),
                Ui.Muted("Newsletters, receipts, notifications and the Screener never notify. Games: Delta Force, Discovery, and whatever Hallow Hub says you're playing.", 11.5, 0)));
            // app
            var app = new WrapPanel();
            app.Children.Add(Ui.Btn("Check for updates", () => c.CheckForUpdate(true)));
            app.Children.Add(Ui.Btn("Open data folder", () => { if (!Snap.On) try { System.Diagnostics.Process.Start("explorer.exe", "\"" + MailSettings.Dir + "\""); } catch { } }));
            sp.Children.Add(Section("App", Ui.Switch("Start with Windows (in the tray)", c.StartsWithWindows(), on => c.SetStartWithWindows(on)), app,
                Ui.Muted("Hallow Email " + Program.Version + ". MailKit and MimeKit (MIT, .NET Foundation and Contributors), Microsoft Edge WebView2 SDK, Font Awesome Free icons (CC BY 4.0). Licences are in the install folder.", 11.5, 0)));
            return Page(sp);
        }
        static FrameworkElement SenderList(MainWindow w, string title, List<string> list)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            sp.Children.Add(Ui.Muted(title + (list.Count == 0 ? ": none" : ":"), 12, 4));
            foreach (var s in list.ToList())
            {
                var ss = s; var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
                var x = Ui.Btn("Remove", () => { lock (w.c.P) list.Remove(ss); w.c.P.Save(); w.Show("settings"); }); x.Padding = new Thickness(8, 2, 8, 2); DockPanel.SetDock(x, Dock.Right); row.Children.Add(x);
                row.Children.Add(new TextBlock { Text = ss, FontFamily = Ui.Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(row);
            }
            return sp;
        }
        static FrameworkElement CategoryList(MainWindow w)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var d = w.c.P.SenderCategory;
            sp.Children.Add(Ui.Muted("Senders you moved to another tab" + (d.Count == 0 ? ": none yet (right-click a message > Always show ... in)" : ":"), 12, 4));
            foreach (var kv in d.ToList())
            {
                var k = kv.Key; var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
                var x = Ui.Btn("Remove", () => { lock (w.c.P) w.c.P.SenderCategory.Remove(k); w.c.P.Save(); w.Show("settings"); }); x.Padding = new Thickness(8, 2, 8, 2); DockPanel.SetDock(x, Dock.Right); row.Children.Add(x);
                row.Children.Add(new TextBlock { Text = k + "  ->  " + Sorter.Title(kv.Value), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(row);
            }
            return sp;
        }

        // ---------------- rules
        static FrameworkElement RulesPage(MainWindow w)
        {
            var c = w.c;
            var sp = Panel("Rules", "Sort new mail as it arrives in the inbox: move it, label it, mark it read, star it, archive it or get a note. Rules run on this PC and act on the server (moves sync to every device).");
            var add = Ui.Btn("New rule", () => EditRule(w, new Rule { Id = Guid.NewGuid().ToString("N").Substring(0, 10), Name = "New rule" }, true), true); add.HorizontalAlignment = HorizontalAlignment.Left; add.Margin = new Thickness(0, 0, 0, 12);
            sp.Children.Add(add);
            foreach (var r in c.P.Rules.ToList())
            {
                var rr = r; var row = new DockPanel();
                var b = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(b, Dock.Right);
                b.Children.Add(Ui.Switch(r.On ? "On" : "Off", r.On, on => { rr.On = on; c.P.Save(); w.Show("rules"); }));
                b.Children.Add(Ui.Btn("Edit", () => EditRule(w, rr, false)));
                b.Children.Add(Ui.Danger("Delete", () => { if (!Dialogs.Confirm(w, "Delete the rule \"" + rr.Name + "\"?", "Mail it already sorted stays where it is.", "Delete", true)) return; lock (c.P) c.P.Rules.Remove(rr); c.P.Save(); w.Show("rules"); }));
                row.Children.Add(b);
                var info = new StackPanel(); info.Children.Add(Ui.T(r.Name, 13.5, true)); info.Children.Add(Ui.Muted(Rules.Describe(r) + (r.Hits > 0 ? "   (used " + r.Hits + " times)" : ""), 12));
                row.Children.Add(info);
                var card = Ui.Card(row, 12); card.Margin = new Thickness(0, 0, 0, 8); sp.Children.Add(card);
            }
            if (c.P.Rules.Count == 0) sp.Children.Add(Ui.Muted("No rules yet. Tip: right-click a message > Create rule from this email.", 12.5));
            var sugg = Suggestions.Make(c.P, c.Store.All());
            if (sugg.Count > 0) { sp.Children.Add(Ui.Caps("Suggestions", 10.5)); foreach (var s in sugg) sp.Children.Add(Ui.Muted(s.Text, 12.5, 4)); }
            return Page(sp);
        }
        public static Window RuleWindow(Controller c, Rule r, bool isNew, Action saved)
        {
            var win = Dialogs.Make("Rule - Hallow Email", 560, null); win.ResizeMode = ResizeMode.CanResize;
            var sp = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            sp.Children.Add(Ui.T(isNew ? "New rule" : "Edit rule", 16, true, null, 8));
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) }); g.ColumnDefinitions.Add(new ColumnDefinition()); int row = 0;
            Action<string, FrameworkElement> line = (l, el) => { g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var t = Ui.Muted(l, 12.5); t.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(t, row); g.Children.Add(t); el.Margin = new Thickness(0, 0, 0, 6); Grid.SetRow(el, row); Grid.SetColumn(el, 1); g.Children.Add(el); row++; };
            var name = Ui.Field(r.Name); name.FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("BodyFont"); line("Name", name);
            var from = Ui.Field(r.From); line("If the sender contains", from);
            var subj = Ui.Field(r.Subject); line("and the subject contains", subj);
            var to = Ui.Field(r.To); line("and To / Cc contains", to);
            var list = Ui.Field(r.ListId); line("and the mailing list is", list);
            var att = new ComboBox { Style = Ui.S("Combo") }; foreach (var o in new[] { "doesn't matter", "has an attachment", "has no attachment" }) att.Items.Add(new ComboBoxItem { Content = o, Style = Ui.S("ComboItem") }); att.SelectedIndex = r.HasAttachment == null ? 0 : r.HasAttachment.Value ? 1 : 2; line("Attachments", att);
            var move = Ui.Field(r.MoveTo); move.ToolTip = "A folder name on the server (e.g. Receipts). It's created when first used."; line("Then move to folder", move);
            var label = Ui.Field(r.Label); line("and add the label", label);
            var cat = new ComboBox { Style = Ui.S("Combo") }; cat.Items.Add(new ComboBoxItem { Content = "(keep the tab)", Tag = null, Style = Ui.S("ComboItem") }); foreach (var k in Sorter.Categories) cat.Items.Add(new ComboBoxItem { Content = "Show in " + Sorter.Title(k), Tag = k, Style = Ui.S("ComboItem") }); cat.SelectedIndex = r.Category == null ? 0 : Array.IndexOf(Sorter.Categories, r.Category) + 1; line("and", cat);
            sp.Children.Add(g);
            var read = Ui.Switch("Mark as read", r.MarkRead, null); var star = Ui.Switch("Star it", r.Star, null); var skip = Ui.Switch("Skip the inbox (archive it)", r.SkipInbox, null); var notify = Ui.Switch("Notify me (even if it's not from People)", r.Notify, null);
            foreach (var x in new[] { read, star, skip, notify }) sp.Children.Add(x);
            var msg = Ui.Muted("", 12); msg.Foreground = Ui.R("Red"); sp.Children.Add(msg);
            var btns = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            btns.Children.Add(Ui.Btn("Cancel", () => win.Close()));
            var ok = Ui.Btn("Save rule", () =>
            {
                Func<TextBox, string> v = t => string.IsNullOrWhiteSpace(t.Text) ? null : t.Text.Trim();
                var n = new Rule { Id = r.Id, Name = v(name) ?? "Rule", From = v(from), Subject = v(subj), To = v(to), ListId = v(list), HasAttachment = att.SelectedIndex == 0 ? (bool?)null : att.SelectedIndex == 1, MoveTo = v(move), Label = v(label), Category = (string)((ComboBoxItem)cat.SelectedItem).Tag, MarkRead = read.IsChecked == true, Star = star.IsChecked == true, SkipInbox = skip.IsChecked == true, Notify = notify.IsChecked == true, On = true, Hits = r.Hits };
                if (n.From == null && n.Subject == null && n.To == null && n.ListId == null && n.HasAttachment == null) { msg.Text = "Give it at least one condition."; return; }
                if (n.MoveTo == null && n.Label == null && n.Category == null && !n.MarkRead && !n.Star && !n.SkipInbox && !n.Notify) { msg.Text = "Give it at least one action."; return; }
                lock (c.P) { c.P.Rules.RemoveAll(x => x.Id == n.Id); c.P.Rules.Add(n); }
                c.P.Save(); win.Close(); if (saved != null) saved();
            }, true); ok.Margin = new Thickness(0); btns.Children.Add(ok);
            sp.Children.Add(btns); win.Content = sp;
            return win;
        }
        public static void EditRule(MainWindow w, Rule r, bool isNew)
        {
            var win = RuleWindow(w.c, r, isNew, () => { w.Say("Rule saved: " + Rules.Describe(w.c.P.Rules.First(x => x.Id == r.Id))); if (w.Page == "rules") w.Show("rules"); });
            win.Owner = w; win.WindowStartupLocation = WindowStartupLocation.CenterOwner; win.ShowDialog();
        }

        // ---------------- the Screener
        public static void ScreenerAnswer(MainWindow w, MailItem m, bool yes)
        {
            var c = w.c; var a = (m.FromAddr ?? "").ToLowerInvariant(); if (a.Length == 0) return;
            lock (c.P) { c.P.ScreenerOk.Remove(a); c.P.ScreenerNo.Remove(a); if (yes) c.P.ScreenerOk.Add(a); else c.P.ScreenerNo.Add(a); }
            var theirs = c.Store.All().Where(x => x.Screened && string.Equals(x.FromAddr, a, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var x in theirs) { x.Screened = false; c.Store.Put(x); if (!yes) c.E.Archive(x); }
            c.P.Save(); w.Current = null; w.Say(yes ? a + " is in - their mail goes to your inbox." : a + " is screened out - their mail is archived quietly."); w.MailChanged(); w.BuildSide();
        }

        // ---------------- unsubscribe finder
        static FrameworkElement Unsub(MainWindow w)
        {
            var c = w.c;
            var sp = Panel("Unsubscribe finder", "Newsletters and mailing lists in your cached mail, most mail first. Nothing is sent until you confirm: one-click unsubscribe (RFC 8058) is a single web request; otherwise a short email or the sender's page.");
            var rows = Unsubscribe.Finder(c.Store.All());
            if (rows.Count == 0) sp.Children.Add(Ui.Muted("No newsletters found in the cache yet.", 12.5));
            foreach (var r in rows.Take(150))
            {
                var rr = r; var row = new DockPanel();
                var b = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(b, Dock.Right);
                if (r.Header != null) b.Children.Add(Ui.Btn("Unsubscribe", () => UnsubscribeOne(w, rr.Sender, rr.Name, rr.Header, rr.Post, rr.Account), true));
                b.Children.Add(Ui.Btn("Archive automatically", () => { var rule = new Rule { Id = Guid.NewGuid().ToString("N").Substring(0, 10), Name = "Archive " + rr.Sender, From = rr.Sender, SkipInbox = true, MarkRead = true }; lock (c.P) c.P.Rules.Add(rule); c.P.Save(); w.Say("New mail from " + rr.Sender + " will be archived and marked read."); }));
                row.Children.Add(b);
                var info = new StackPanel();
                info.Children.Add(new TextBlock { Text = (r.Name ?? r.Sender), FontWeight = FontWeights.SemiBold, FontSize = 13 });
                var how = r.Kind == "oneclick" ? "one click" : r.Kind == "mailto" ? "by email" : r.Kind == "web" ? "on their web page" : "no unsubscribe link";
                info.Children.Add(Ui.Muted(r.Sender + "   |   " + r.Count + " message" + (r.Count == 1 ? "" : "s") + ", opened " + r.Opened + "   |   " + how, 11.5));
                row.Children.Add(info);
                var card = Ui.Card(row, 10); card.Margin = new Thickness(0, 0, 0, 6); sp.Children.Add(card);
            }
            return Page(sp);
        }
        public static Window UnsubWindow(string sender, string name, UnsubPlan plan, Action<bool> done)
        {
            return Dialogs.ConfirmWindow("Unsubscribe from " + (name ?? sender) + "?", plan.Describe + "\n\nHallow Email also keeps a rule so anything that still comes is archived.", plan.Kind == "web" ? "Open the page" : "Unsubscribe", false, done);
        }
        public static void UnsubscribeOne(MainWindow w, string sender, string name, string header, string post, string account)
        {
            var c = w.c; var plan = Unsubscribe.Plan(header, post);
            if (plan.Kind == "none") { Dialogs.Info(w, "Can't unsubscribe here", plan.Describe); return; }
            bool ok = false; var win = UnsubWindow(sender, name, plan, a => ok = a); win.Owner = w; win.WindowStartupLocation = WindowStartupLocation.CenterOwner; win.ShowDialog();
            if (!ok) return;
            var rule = new Rule { Id = Guid.NewGuid().ToString("N").Substring(0, 10), Name = "Unsubscribed: " + sender, From = sender, SkipInbox = true, MarkRead = true };
            lock (c.P) { c.P.Rules.Add(rule); } c.P.Save();
            w.Say("Unsubscribing from " + sender + "...");
            Task.Factory.StartNew(() => DoUnsubscribe(c.E, plan, account)).ContinueWith(t => w.Say(t.Result ?? "Done - you're unsubscribed from " + sender + "."), TaskScheduler.FromCurrentSynchronizationContext());
        }
        public static string DoUnsubscribe(Engine e, UnsubPlan plan, string account)
        {
            if (plan.Kind == "oneclick") { var err = Unsubscribe.OneClick(plan.Url); return err == null ? null : "Unsubscribe didn't go through: " + err; }
            if (plan.Kind == "mailto")
            {
                var a = e.P.Find(account) ?? e.P.Accounts.FirstOrDefault(); if (a == null) return "No account to send it from.";
                var m = new MimeMessage(); m.From.Add(new MailboxAddress(a.Name ?? "", a.Email)); m.To.Add(MailboxAddress.Parse(plan.MailTo)); m.Subject = plan.MailSubject ?? "unsubscribe";
                m.Body = new TextPart("plain") { Text = plan.MailBody ?? "unsubscribe" };
                e.Queue(a, m, DateTime.Now, false); return null;
            }
            if (plan.Kind == "web") { if (MailSettings.TestDir == null && !Snap.On) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(plan.Url) { UseShellExecute = true }); } catch { } return "Finish on the sender's page in your browser."; }
            return plan.Describe;
        }

        // ---------------- your own domain + masked addresses (Cloudflare Email Routing)
        static FrameworkElement Masked(MainWindow w)
        {
            var c = w.c; var P = c.P;
            var sp = Panel("Your addresses", "Use your own domain for mail, and make throw-away \"masked\" addresses that forward to your inbox - turn one off when it starts getting spam.");
            sp.Children.Add(Section("How it works (Cloudflare Email Routing)",
                Ui.T("1. In the Cloudflare dashboard open your domain (e.g. hallowservices.com) > Email > Email Routing and click Enable. Cloudflare adds the MX and SPF records for you.\n" +
                     "2. Under Destination addresses add the inbox you read here (e.g. your Gmail) and click the verification link Cloudflare emails you.\n" +
                     "3. Add a routing rule such as you@yourdomain -> your inbox, or turn on Catch-all. Mail to your domain now arrives in that inbox - and in Hallow Email.\n" +
                     "4. To REPLY from your domain, add it as a \"Send as\" address of the account that receives it (Settings > Accounts > Send as...). Your provider sends it through its own SMTP: in Gmail add it under Settings > Accounts > Send mail as (\"Treat as an alias\", Gmail's SMTP); Outlook.com and others have similar alias settings. Without that, replies come from the provider address.\n" +
                     "5. Masked addresses (below) need an API token: Cloudflare dashboard > My Profile > API Tokens > Create token > Custom: permissions Zone > Email Routing Rules > Edit, and Zone > Zone > Read, for that one zone. Paste it here - it's stored encrypted (DPAPI) and only used when you create or turn off an address.", 12, false, Ui.R("Text"))));
            var dom = Ui.Field(P.CfZone ?? ""); var fwd = Ui.Field(P.CfForwardTo ?? (P.Accounts.Count > 0 ? P.Accounts[0].Email : "")); var tok = new PasswordBox { Style = Ui.S("Pass") };
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) }); g.ColumnDefinitions.Add(new ColumnDefinition()); int row = 0;
            Action<string, FrameworkElement> line = (l, el) => { g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var t = Ui.Muted(l, 12.5); t.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(t, row); g.Children.Add(t); el.Margin = new Thickness(0, 0, 0, 6); Grid.SetRow(el, row); Grid.SetColumn(el, 1); g.Children.Add(el); row++; };
            line("Your domain", dom); line("Forward masked mail to", fwd); line(Secrets.Has("cf") ? "API token (saved - paste to replace)" : "Cloudflare API token", tok);
            var msg = Ui.Muted("", 12);
            var save = Ui.Btn("Save and check", () =>
            {
                P.CfZone = dom.Text.Trim().ToLowerInvariant(); P.CfForwardTo = fwd.Text.Trim(); if (tok.Password.Length > 0) Secrets.Set("cf", tok.Password.Trim());
                if (!Secrets.Has("cf") || P.CfZone.Length == 0) { msg.Text = "Fill in the domain and the token."; return; }
                msg.Text = "Asking Cloudflare..."; var token = Secrets.Get("cf"); var zone = P.CfZone;
                Task.Factory.StartNew(() => { string err; var id = Cloudflare.ZoneId(token, zone, out err); return Tuple.Create(id, err); }).ContinueWith(t =>
                {
                    if (t.Result.Item1 == null) { msg.Text = t.Result.Item2; msg.Foreground = Ui.R("Red"); return; }
                    P.CfZoneId = t.Result.Item1; P.Save(); msg.Text = "Connected to " + zone + "."; msg.Foreground = Ui.R("Green");
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }, true);
            save.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(Section("Cloudflare (off until a token is set)", g, save, msg));
            // the addresses
            var list = new StackPanel();
            var note = Ui.Field(""); note.ToolTip = "What it's for, e.g. the shop's name - it becomes part of the address";
            var create = Ui.Btn("Create masked address", () => CreateMasked(w, note.Text), true);
            create.IsEnabled = Secrets.Has("cf") && !string.IsNullOrEmpty(P.CfZoneId) || Snap.On;
            list.Children.Add(new DockPanel { Children = { create, note } }); DockPanel.SetDock(create, Dock.Right); create.Margin = new Thickness(8, 0, 0, 0);
            foreach (var m in P.MaskedAddresses.OrderByDescending(x => x.Created).ToList())
            {
                var mm = m; var r = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                var b = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(b, Dock.Right);
                b.Children.Add(Ui.Btn("Copy", () => { try { if (!Snap.On) Clipboard.SetText(mm.Address); } catch { } w.Say("Copied " + mm.Address); }));
                if (!m.Off) b.Children.Add(Ui.Danger("Turn off", () => TurnOffMasked(w, mm)));
                r.Children.Add(b);
                var info = new StackPanel(); info.Children.Add(new TextBlock { Text = m.Address, FontFamily = Ui.Mono, FontSize = 12.5, Foreground = Ui.R(m.Off ? "Faint" : "Text") });
                info.Children.Add(Ui.Muted((m.Note ?? "") + (m.Off ? "  (off - mail to it bounces)" : "") + "  -  made " + (m.Created ?? "").Replace("T", " "), 11.5));
                r.Children.Add(info); list.Children.Add(r);
            }
            sp.Children.Add(Section("Masked addresses", list));
            sp.Children.Add(Section("Hallow Keys (later)", Ui.Muted("Planned hook: Hallow Keys will be able to ask Hallow Email for a new masked address while you sign up for a site, over the family pipe: \"masked-new <site>\" -> ok {\"address\":\"...\"} (only after you confirm in Hallow Email). Not switched on in 1.0, and Hallow Email never reads Keys' data.", 12)));
            return Page(sp);
        }
        public static string CreateMasked(MainWindow w, string note)
        {
            var c = w == null ? null : w.c; return CreateMaskedFor(c.P, note, s => { if (w != null) { w.Say(s); w.Show("masked"); } });
        }
        public static string CreateMaskedFor(Profile P, string note, Action<string> say)
        {
            var token = Secrets.Get("cf");
            if (token == null || string.IsNullOrEmpty(P.CfZoneId) || string.IsNullOrEmpty(P.CfZone)) { say("Set up Cloudflare first (domain + token)."); return "not set up"; }
            if (string.IsNullOrWhiteSpace(P.CfForwardTo)) { say("Say where masked mail should go."); return "no destination"; }
            var alias = Cloudflare.NewAlias(P.CfZone, note); string err;
            var id = Cloudflare.CreateRule(token, P.CfZoneId, alias, P.CfForwardTo, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), out err);
            if (id == null) { say(err); return err; }
            lock (P) P.MaskedAddresses.Add(new Masked { Address = alias, RuleId = id, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), Created = DateTime.Now.ToString("s") });
            P.Save(); say("New masked address: " + alias + " (forwards to " + P.CfForwardTo + ")."); return null;
        }
        static void TurnOffMasked(MainWindow w, Masked m)
        {
            var c = w.c;
            if (!Dialogs.Confirm(w, "Turn off " + m.Address + "?", "Cloudflare deletes its routing rule, so mail sent to it is refused from now on. Mail you already got stays.", "Turn off", true)) return;
            var err = Cloudflare.DeleteRule(Secrets.Get("cf"), c.P.CfZoneId, m.RuleId);
            if (err != null) { w.Say(err); return; }
            m.Off = true; c.P.Save(); w.Say(m.Address + " is off."); w.Show("masked");
        }

        // ---------------- outbox
        static FrameworkElement Outbox(MainWindow w)
        {
            var c = w.c; var sp = Panel("Outbox", "Mail waiting on this PC: Undo send, Send later, or a retry after a failed send. It goes out while Hallow Email is running.");
            List<Queued> l; lock (c.P) l = c.P.Outbox.ToList();
            if (l.Count == 0) sp.Children.Add(Ui.Muted("Nothing waiting.", 12.5));
            foreach (var q in l.OrderBy(x => x.SendAt))
            {
                var qq = q; var row = new DockPanel();
                var b = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(b, Dock.Right);
                b.Children.Add(Ui.Btn("Send now", () => { qq.SendAt = DateTime.Now.ToString("o"); qq.Tries = 0; qq.Error = null; c.P.Save(); w.Say("Sending..."); }));
                b.Children.Add(Ui.Btn("Edit (take it out)", () => { if (c.E.Undo(qq.Id)) ComposeWindow.Reopen(c, qq); w.Show("outbox"); }));
                row.Children.Add(b);
                var info = new StackPanel(); info.Children.Add(Ui.T(qq.Subject ?? "(no subject)", 13, true)); var a = c.P.Find(qq.Account);
                info.Children.Add(Ui.Muted("To " + qq.To + "  |  " + DateTime.Parse(qq.SendAt).ToString("ddd d MMM HH:mm") + (a != null ? "  |  from " + a.Display : "") + (qq.Error != null ? "  |  last try failed: " + qq.Error : ""), 11.5));
                row.Children.Add(info);
                var card = Ui.Card(row, 10); card.Margin = new Thickness(0, 0, 0, 6); sp.Children.Add(card);
            }
            return Page(sp);
        }
    }
}
