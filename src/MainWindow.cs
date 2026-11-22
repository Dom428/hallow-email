// Hallow Email main window: the folder list on the left (unified Inbox, Screener, Starred, Snoozed, Sent, Drafts, Spam, each account's
// folders, the Unsubscribe finder, masked addresses, Settings), the message list (People / Newsletters / Receipts / Notifications,
// search, conversations, bulk actions, suggestions) and the reader (Reader.cs). Full pages are in Pages.cs.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HallowEmail
{
    public partial class MainWindow : Window
    {
        public readonly Controller c;
        public string Page = "inbox"; public string Tab = "people"; public string Query = "";
        public readonly HashSet<string> Selected = new HashSet<string>(); public string Current;   // thread keys / the open message key
        Border content; TextBlock sayText; Button updateBtn; DispatcherTimer sayTimer, refreshTimer; Border undoBar; TextBlock undoText; string undoId; DateTime undoUntil;
        StackPanel listPanel; ScrollViewer listScroll; public Reader Reader; TextBox search;
        public bool Exiting;
        public static readonly string[][] Pages = {
            new[] { "inbox", "Inbox", "inbox" }, new[] { "screener", "Screener", "user-clock" }, new[] { "starred", "Starred", "star" }, new[] { "snoozed", "Snoozed", "clock" },
            new[] { "sent", "Sent", "paper-plane" }, new[] { "drafts", "Drafts", "file" }, new[] { "spam", "Spam", "ban" }, new[] { "outbox", "Outbox", "hourglass-half" } };
        public static readonly string[][] Tools = { new[] { "unsubscribe", "Unsubscribe finder", "bell-slash" }, new[] { "masked", "Your addresses", "user-secret" }, new[] { "rules", "Rules", "list-check" }, new[] { "settings", "Settings", "gear" } };
        public static string PageId(string s)
        {
            var v = (s ?? "").Trim().ToLowerInvariant();
            if (Sorter.Categories.Contains(v)) return v;
            if (v == "accounts") return "settings"; if (v == "junk") return "spam"; if (v == "unsub") return "unsubscribe"; if (v == "addresses") return "masked";
            return Pages.Any(p => p[0] == v) || Tools.Any(p => p[0] == v) ? v : null;
        }

        public MainWindow(Controller controller)
        {
            c = controller;
            Ui.Dress(this, "Hallow Email", 1360);
            SizeToContent = SizeToContent.Manual; Height = 840; MinWidth = 1000; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Rebuild();
            Closed += (s, e) => { if (Reader != null) Reader.Dispose(); c.MainClosed(); };
            KeyDown += OnKey;
            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) }; refreshTimer.Tick += (s, e) => { refreshTimer.Stop(); RefreshList(); BuildSide(); };
        }
        public new void Show(string page)
        {
            var p = PageId(page) ?? page;
            if (Sorter.Categories.Contains(p)) { Tab = p; p = "inbox"; }
            Page = p; Selected.Clear(); BuildPage();
        }
        void OnKey(object s, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;
            var it = CurrentItem();
            if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { c.Compose(null); e.Handled = true; }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && search != null) { search.Focus(); e.Handled = true; }
            else if (it != null && e.Key == Key.E) { Act(() => c.E.Archive(it)); e.Handled = true; }
            else if (it != null && e.Key == Key.Delete) { Act(() => c.E.Trash(it)); e.Handled = true; }
            else if (it != null && e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control) { ComposeWindow.Reply(c, it, false); e.Handled = true; }
        }
        public void Act(Func<string> f) { var err = f(); if (err != null) Say(err); else { Current = null; MailChanged(); } }

        // ---------------- the frame
        DockPanel side; Grid body;
        public void Rebuild()
        {
            Ui.Restyle(this);
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(226) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            side = new DockPanel { Background = Ui.R("Side"), LastChildFill = true };
            root.Children.Add(side);
            root.Children.Add(new Border { Width = 1, Background = Ui.R("Line"), HorizontalAlignment = HorizontalAlignment.Right });
            var right = new DockPanel(); Grid.SetColumn(right, 1);
            var bar = new DockPanel { Background = Ui.R("Side"), Height = 30 };
            sayText = new TextBlock { FontSize = 12, Foreground = Ui.R("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            bar.Children.Add(sayText);
            DockPanel.SetDock(bar, Dock.Bottom); right.Children.Add(bar);
            right.Children.Add(new Border { Height = 1, Background = Ui.R("Line") }); DockPanel.SetDock(right.Children[1], Dock.Bottom);
            // the Undo send bar
            undoText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
            var undoRow = new DockPanel { Margin = new Thickness(14, 6, 10, 6) };
            var undoBtn = Ui.Btn("Undo", () => DoUndo(), true); DockPanel.SetDock(undoBtn, Dock.Right); undoRow.Children.Add(undoBtn); undoRow.Children.Add(undoText);
            undoBar = new Border { Background = Ui.R("AccentGlow"), BorderBrush = Ui.R("AccentBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = undoRow, Visibility = undoId == null ? Visibility.Collapsed : Visibility.Visible };
            DockPanel.SetDock(undoBar, Dock.Top); right.Children.Add(undoBar);
            content = new Border(); right.Children.Add(content);
            root.Children.Add(right);
            Content = root;
            BuildSide(); BuildPage();
        }

        static int Unread(IEnumerable<MailItem> l) { return l.Count(i => !i.Seen); }
        public void BuildSide()
        {
            side.Children.Clear();
            var head = new StackPanel { Margin = new Thickness(16, 18, 16, 12) };
            head.Children.Add(Ui.Header("EMAIL", 18));
            head.Children.Add(Ui.Muted("Private mail, Hallow style", 11.5, 10));
            var compose = Ui.Btn("New email", () => c.Compose(null), true); compose.HorizontalAlignment = HorizontalAlignment.Stretch; compose.Margin = new Thickness(0); compose.Padding = new Thickness(11, 8, 11, 8);
            head.Children.Add(compose);
            DockPanel.SetDock(head, Dock.Top); side.Children.Add(head);
            var foot = new StackPanel { Margin = new Thickness(16, 6, 16, 12) };
            updateBtn = Ui.Btn("Update", () => c.InstallUpdate(), true); updateBtn.Visibility = c.PendingUpdate == null ? Visibility.Collapsed : Visibility.Visible; updateBtn.HorizontalAlignment = HorizontalAlignment.Left; updateBtn.Margin = new Thickness(0, 0, 0, 8);
            if (c.PendingUpdate != null) updateBtn.Content = "Update to v" + c.PendingUpdate.Version.ToString(2);
            foot.Children.Add(updateBtn);
            foot.Children.Add(Ui.Muted("v" + Program.Version + " - your mail stays on this PC", 11, 0));
            DockPanel.SetDock(foot, Dock.Bottom); side.Children.Add(foot);
            var sp = new StackPanel();
            var all = c.Store.All();
            foreach (var p in Pages)
            {
                if (p[0] == "screener" && !c.S.Screener && !all.Any(i => i.Screened)) continue;
                if (p[0] == "outbox" && c.P.Outbox.Count == 0) continue;
                int n = p[0] == "inbox" ? Unread(View("inbox", "all", all)) : p[0] == "screener" ? View("screener", null, all).Count : p[0] == "spam" ? Unread(View("spam", null, all)) : p[0] == "outbox" ? c.P.Outbox.Count : p[0] == "snoozed" ? c.P.Snoozed.Count : 0;
                sp.Children.Add(NavBtn(p[0], p[1], p[2], n));
            }
            foreach (var a in c.P.Accounts)
            {
                var st = c.E.State(a.Id);
                var cap = new DockPanel { Margin = new Thickness(18, 14, 16, 4) };
                var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Ui.R(st.StartsWith("error") ? "Red" : st == "off" ? "Faint" : st == "idle" || st == "synced" ? "Green" : "Amber"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0), ToolTip = StateText(st) };
                if (Snap.On) dot.Background = Ui.R("Green");
                cap.Children.Add(dot); cap.Children.Add(Ui.Caps(a.Display, 10.5));
                sp.Children.Add(cap);
                var idx = c.Store.Index(a.Id);
                List<FolderInfo> folders; lock (c.Store.Lock) folders = idx.Folders.ToList();
                foreach (var f in folders.OrderBy(f => RoleOrder(f.Role)).ThenBy(f => f.Name))
                {
                    if (f.Role == "all") continue;
                    var key = "folder:" + a.Id + "|" + f.Name; int n; lock (c.Store.Lock) n = idx.Items.Count(i => i.Folder == f.Name && !i.Seen);
                    var b = NavBtn(key, f.Short, RoleIcon(f.Role), f.Role == "sent" || f.Role == "drafts" || f.Role == "trash" ? 0 : n); b.Padding = new Thickness(22, 5, 14, 5);
                    sp.Children.Add(b);
                }
            }
            var tools = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            foreach (var t in Tools) tools.Children.Add(NavBtn(t[0], t[1], t[2], 0));
            sp.Children.Add(tools);
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sp };
            side.Children.Add(sv);
        }
        public static string StateText(string st)
        {
            if (st == null) return "";
            if (st.StartsWith("error")) return "Not connected: " + st.Substring(6).Trim();
            switch (st) { case "idle": return "Connected - new mail arrives at once (IMAP IDLE)"; case "synced": return "Connected"; case "syncing": return "Checking for mail..."; case "connecting": return "Connecting..."; case "off": return "Off"; }
            return st;
        }
        static int RoleOrder(string r) { switch (r) { case "inbox": return 0; case "drafts": return 1; case "sent": return 2; case "archive": return 3; case "junk": return 4; case "trash": return 5; default: return 9; } }
        static string RoleIcon(string r) { switch (r) { case "inbox": return "inbox"; case "sent": return "paper-plane"; case "drafts": return "file"; case "junk": return "ban"; case "trash": return "trash"; case "archive": return "box-archive"; default: return "folder"; } }
        Button NavBtn(string id, string label, string icon, int count)
        {
            var dp = new DockPanel();
            if (count > 0) { var n = new TextBlock { Text = count > 999 ? "999+" : count.ToString(), FontFamily = Ui.Mono, FontSize = 11, Foreground = Ui.R("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold }; DockPanel.SetDock(n, Dock.Right); dp.Children.Add(n); }
            dp.Children.Add(Icons.WithText(icon, label, 13, Ui.R("Text")));
            var b = new Button { Style = Ui.S("Nav"), Content = dp, Margin = new Thickness(8, 0, 8, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 6, 14, 6) };
            b.Tag = id == Page ? "on" : null;
            b.Click += (s, e) => Show(id);
            return b;
        }

        // ---------------- which mail a view shows
        public List<MailItem> View(string page, string tab, List<MailItem> all = null)
        {
            all = all ?? c.Store.All();
            var folders = new Dictionary<string, FolderInfo>();
            foreach (var a in c.P.Accounts) { var idx = c.Store.Index(a.Id); lock (c.Store.Lock) foreach (var f in idx.Folders) folders[a.Id + "|" + f.Name] = f; }
            Func<MailItem, string> role = i => { FolderInfo f; return folders.TryGetValue(i.Account + "|" + i.Folder, out f) ? f.Role : null; };
            IEnumerable<MailItem> r;
            switch (page)
            {
                case "inbox": r = all.Where(i => role(i) == "inbox" && !i.Screened && !c.E.IsSnoozed(i)); if (tab != null && tab != "all") r = r.Where(i => (i.Category ?? "people") == tab); break;
                case "screener": r = all.Where(i => i.Screened && role(i) == "inbox"); break;
                case "starred": r = all.Where(i => i.Flagged && role(i) != "trash"); break;
                case "snoozed": r = all.Where(i => c.E.IsSnoozed(i)); break;
                case "sent": r = all.Where(i => role(i) == "sent"); break;
                case "drafts": r = all.Where(i => role(i) == "drafts"); break;
                case "spam": r = all.Where(i => role(i) == "junk"); break;
                default:
                    if (page != null && page.StartsWith("folder:")) { var k = page.Substring(7); r = all.Where(i => i.Account + "|" + i.Folder == k && !c.E.IsSnoozed(i)); }
                    else r = new MailItem[0];
                    break;
            }
            return r.ToList();
        }
        bool IsMailPage { get { return Pages.Any(p => p[0] == Page && p[0] != "outbox") || Page.StartsWith("folder:") || Page == "search"; } }

        public void BuildPage()
        {
            BuildSide();
            if (Reader != null) { Reader.Dispose(); Reader = null; }
            if (!IsMailPage) { listPanel = null; content.Child = PageBuilder.Build(this, Page); return; }
            body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(430), MinWidth = 300 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 360 });
            var left = new DockPanel();
            var top = new StackPanel { Margin = new Thickness(14, 12, 14, 6) };
            var title = new DockPanel();
            var syncBtn = Ui.Btn("Check now", () => { c.E.SyncNow(); Say("Checking for mail..."); }); syncBtn.Margin = new Thickness(0); DockPanel.SetDock(syncBtn, Dock.Right); title.Children.Add(syncBtn);
            title.Children.Add(Ui.T(PageTitle(), 17, true));
            top.Children.Add(title);
            search = Ui.Field(Query); search.Margin = new Thickness(0, 8, 0, 6); search.ToolTip = "Search every account's cached mail. Try from:anna, subject:invoice, has:attachment, is:unread, label:work";
            search.TextChanged += (s, e) => { Query = search.Text; RefreshList(); };
            top.Children.Add(new Grid { Children = { search, new TextBlock { Text = "Search all mail", Foreground = Ui.R("Faint"), Margin = new Thickness(11, 14, 0, 0), IsHitTestVisible = false, Visibility = string.IsNullOrEmpty(Query) ? Visibility.Visible : Visibility.Collapsed } } });
            search.TextChanged += (s, e) => { ((Grid)search.Parent).Children[1].Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            if (Page == "inbox")
            {
                var tabs = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
                var all = View("inbox", "all");
                foreach (var t in Sorter.Categories.Concat(new[] { "all" }))
                {
                    var tt = t; int n = t == "all" ? Unread(all) : Unread(all.Where(i => (i.Category ?? "people") == t));
                    var chip = new ToggleButton { Style = Ui.S("Chip"), Content = (t == "all" ? "All" : Sorter.Title(t)) + (n > 0 ? "  " + n : ""), IsChecked = Tab == t, Margin = new Thickness(0, 0, 6, 4) };
                    chip.Click += (s, e) => { Tab = tt; Selected.Clear(); BuildPage(); };
                    tabs.Children.Add(chip);
                }
                top.Children.Add(tabs);
            }
            if (Page == "screener") top.Children.Add(Ui.Muted("First-time senders wait here until you say yes. Yes = their mail goes to your inbox from now on; No = it's archived without bothering you. Turn the Screener on or off in Settings.", 11.5, 4));
            if (Page == "spam") top.Children.Add(Ui.Muted("Mail the spam filter or your provider moved to Junk. Nothing here is deleted by Hallow Email. \"Not spam\" moves it back and teaches the filter.", 11.5, 4));
            DockPanel.SetDock(top, Dock.Top); left.Children.Add(top);
            // suggestions and bulk actions
            var extra = new StackPanel { Margin = new Thickness(14, 0, 14, 4) }; DockPanel.SetDock(extra, Dock.Top); left.Children.Add(extra); bulkHost = extra;
            listPanel = new StackPanel();
            listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = listPanel };
            left.Children.Add(listScroll);
            body.Children.Add(left);
            var split = new GridSplitter { Width = 5, Background = Ui.R("Line"), ResizeBehavior = GridResizeBehavior.PreviousAndNext, ResizeDirection = GridResizeDirection.Columns, BorderThickness = new Thickness(2, 0, 2, 0), BorderBrush = Ui.R("Bg") };
            Grid.SetColumn(split, 1); body.Children.Add(split);
            Reader = new Reader(this); Grid.SetColumn(Reader.Root, 2); body.Children.Add(Reader.Root);
            content.Child = body;
            RefreshList();
        }
        string PageTitle()
        {
            if (Page.StartsWith("folder:")) { var k = Page.Substring(7).Split(new[] { '|' }, 2); var a = c.P.Find(k[0]); return (a == null ? "" : a.Display + " - ") + new FolderInfo { Name = k[1] }.Short; }
            var p = Pages.FirstOrDefault(x => x[0] == Page); return p == null ? "Mail" : p[0] == "inbox" ? "Inbox - every account" : p[1];
        }
        StackPanel bulkHost;

        // ---------------- the list (conversations, newest first)
        public void MailChanged() { if (refreshTimer != null) { refreshTimer.Stop(); refreshTimer.Start(); } }
        public class Conv { public string Key; public List<MailItem> Items; public MailItem Newest { get { return Items[0]; } } }
        public List<Conv> Conversations()
        {
            var items = View(Page == "search" ? "inbox" : Page, Tab);
            if (!string.IsNullOrWhiteSpace(Query)) items = MailStore.Search(Page == "inbox" ? c.Store.All().Where(i => !i.Screened).ToList() : items, Query, c.P);
            return items.GroupBy(i => i.Thread ?? i.Key).Select(g => new Conv { Key = g.Key, Items = g.OrderByDescending(x => x.When).ToList() }).OrderByDescending(x => x.Newest.When).Take(400).ToList();
        }
        public void RefreshList()
        {
            if (listPanel == null) { if (Page == "outbox" || Page == "unsubscribe" || Page == "masked") content.Child = PageBuilder.Build(this, Page); return; }
            listPanel.Children.Clear();
            var convs = Conversations();
            BuildBulk(convs);
            if (convs.Count == 0)
            {
                listPanel.Children.Add(Ui.Muted(c.P.Accounts.Count == 0 ? "No accounts yet - add one in Settings." : !string.IsNullOrWhiteSpace(Query) ? "Nothing in the cache matches that." : "Nothing here.", 12.5, 0));
                ((FrameworkElement)listPanel.Children[0]).Margin = new Thickness(16, 20, 16, 0);
            }
            bool many = c.P.Accounts.Count > 1;
            foreach (var cv in convs) listPanel.Children.Add(Row(cv, many));
            if (Current == null && convs.Count > 0 && !Snap.On) { }
        }
        FrameworkElement Row(Conv cv, bool many)
        {
            var m = cv.Newest; bool unread = cv.Items.Any(i => !i.Seen); bool sel = Selected.Contains(cv.Key) || cv.Items.Any(i => i.Key == Current);
            var g = new DockPanel { Margin = new Thickness(14, 8, 14, 8) };
            var line1 = new DockPanel();
            var date = new TextBlock { Text = When(m.When), FontSize = 11.5, Foreground = Ui.R(unread ? "AccentBrush" : "Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(date, Dock.Right); line1.Children.Add(date);
            if (m.Flagged) { var st = Icons.Make("star", 11, Ui.R("Amber")); st.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(st, Dock.Right); line1.Children.Add(st); }
            if (cv.Items.Any(i => i.HasAttachments)) { var pc = Icons.Make("paperclip", 11, Ui.R("Muted")); pc.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(pc, Dock.Right); line1.Children.Add(pc); }
            var who = Page == "sent" || Page == "drafts" ? "To: " + ShortTo(m.To) : string.Join(", ", cv.Items.Select(i => FirstName(i)).Distinct().Take(3));
            var name = new TextBlock { Text = who + (cv.Items.Count > 1 ? "  (" + cv.Items.Count + ")" : ""), FontWeight = unread ? FontWeights.Bold : FontWeights.Normal, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            if (unread) { var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Ui.R("AccentBrush"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(dot, Dock.Left); line1.Children.Add(dot); }
            line1.Children.Add(name);
            var stack = new StackPanel(); stack.Children.Add(line1);
            var subj = new TextBlock { Text = string.IsNullOrEmpty(m.Subject) ? "(no subject)" : m.Subject, FontSize = 12.5, FontWeight = unread ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            stack.Children.Add(subj);
            var pills = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
            if (many) { var a = c.P.Find(m.Account); if (a != null) pills.Children.Add(Small(a.Display, "Lilac", "LilacBg")); }
            if (Page == "inbox" && Tab == "all" && m.Category != null && m.Category != "people") pills.Children.Add(Small(Sorter.Title(m.Category), "Muted", "PanelHi"));
            if (!string.IsNullOrEmpty(m.SpamWhy)) pills.Children.Add(Small("Spam", "Red", "RedBg"));
            foreach (var l in c.E.LabelsOf(m)) pills.Children.Add(Small(l, "AccentBrush", "AccentGlow"));
            if (c.E.IsSnoozed(m)) pills.Children.Add(Small("Snoozed", "Amber", "AmberBg"));
            var prev = new TextBlock { Text = m.Preview ?? "", FontSize = 12, Foreground = Ui.R("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            stack.Children.Add(prev);
            if (pills.Children.Count > 0) stack.Children.Add(pills);
            g.Children.Add(stack);
            var row = new Border { Child = g, Background = sel ? Ui.R("RowSel") : Brushes.Transparent, BorderBrush = Ui.R("LineDim"), BorderThickness = new Thickness(0, 0, 0, 1), Cursor = Cursors.Hand, Tag = cv.Key };
            row.MouseEnter += (s, e) => { if (!(Selected.Contains(cv.Key) || cv.Items.Any(i => i.Key == Current))) row.Background = Ui.R("RowHover"); };
            row.MouseLeave += (s, e) => { row.Background = Selected.Contains(cv.Key) || cv.Items.Any(i => i.Key == Current) ? Ui.R("RowSel") : Brushes.Transparent; };
            row.MouseLeftButtonUp += (s, e) =>
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { if (!Selected.Remove(cv.Key)) Selected.Add(cv.Key); RefreshList(); return; }
                Selected.Clear(); Open(cv.Newest, cv);
            };
            row.ContextMenu = RowMenu(cv);
            return row;
        }
        Border Small(string text, string fg, string bg)
        {
            return new Border { Background = Ui.R(bg), CornerRadius = new CornerRadius(2), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(0, 0, 5, 0), Child = new TextBlock { Text = text, FontSize = 10.5, Foreground = Ui.R(fg), FontWeight = FontWeights.SemiBold } };
        }
        static string FirstName(MailItem i) { var n = i.Sender ?? ""; return n.Length > 28 ? n.Substring(0, 28) : n; }
        static string ShortTo(string to) { if (string.IsNullOrEmpty(to)) return "(nobody)"; MimeKit.InternetAddressList l; return MimeKit.InternetAddressList.TryParse(to, out l) ? string.Join(", ", l.Mailboxes.Select(x => string.IsNullOrEmpty(x.Name) ? x.Address : x.Name).Take(3)) : to; }
        public static string When(DateTime d)
        {
            if (d == DateTime.MinValue) return "";
            var now = Snap.On ? SnapShots.Now : DateTime.Now;
            if (d.Date == now.Date) return d.ToString("HH:mm");
            if (d.Date == now.Date.AddDays(-1)) return "Yesterday";
            if ((now - d).TotalDays < 7) return d.ToString("ddd");
            return d.Year == now.Year ? d.ToString("d MMM") : d.ToString("d MMM yyyy");
        }
        public MailItem CurrentItem() { return Current == null ? null : c.Store.Find(Current); }
        public void Open(MailItem m, Conv cv)
        {
            if (Page == "drafts" && !Snap.On) { OpenDraft(m); return; }
            Current = m.Key; RefreshList();
            if (Reader != null) Reader.Show(m, cv == null ? new List<MailItem> { m } : cv.Items);
            BuildSideLater();
        }
        // a draft opens in the editor; saving or sending replaces the copy on the server
        void OpenDraft(MailItem m)
        {
            string err; var v = c.E.Open(m, out err); if (v == null) { Say(err); return; }
            var st = ComposeState.FromMime(m.Account, v.Message); st.DraftUid = m.Uid;
            new ComposeWindow(c, st).Show();
        }
        void BuildSideLater() { Dispatcher.BeginInvoke(new Action(BuildSide), DispatcherPriority.Background); }

        // ---------------- row menu, bulk actions, suggestions
        ContextMenu RowMenu(Conv cv)
        {
            var m = cv.Newest; var menu = new ContextMenu();
            Action<string, Action> add = (t, a) => { var mi = new MenuItem { Header = t }; mi.Click += (s, e) => a(); menu.Items.Add(mi); };
            add(m.Seen ? "Mark as unread" : "Mark as read", () => { foreach (var i in cv.Items) c.E.SetSeen(i, !m.Seen); });
            add(m.Flagged ? "Remove star" : "Star", () => c.E.SetStar(m, !m.Flagged));
            add("Archive", () => Act(() => All(cv, c.E.Archive)));
            add("Delete (to Trash)", () => Act(() => All(cv, c.E.Trash)));
            add(Page == "spam" ? "Not spam" : "Spam", () => Act(() => All(cv, i => c.E.MarkSpam(i, Page != "spam"))));
            menu.Items.Add(new Separator());
            foreach (var cat in Sorter.Categories) { var cc = cat; if (cc != (m.Category ?? "people")) add("Always show " + (m.FromAddr ?? "this sender") + " in " + Sorter.Title(cc), () => MoveSender(m, cc)); }
            add("Snooze until tomorrow 8:00", () => { c.E.SnoozeIt(m, DateTime.Today.AddDays(1).AddHours(8)); Say("Snoozed until tomorrow 8:00."); MailChanged(); });
            add("Create rule from this email...", () => PageBuilder.EditRule(this, Rules.FromMail(m), true));
            add("Select all from " + (m.FromAddr ?? "this sender"), () => SelectSender(m.FromAddr));
            return menu;
        }
        static string All(Conv cv, Func<MailItem, string> f) { string err = null; foreach (var i in cv.Items) { var e = f(i); if (e != null) err = e; } return err; }
        public void MoveSender(MailItem m, string cat)
        {
            if (string.IsNullOrEmpty(m.FromAddr)) return;
            lock (c.P) c.P.SenderCategory[m.FromAddr.ToLowerInvariant()] = cat;
            foreach (var i in c.Store.All().Where(x => string.Equals(x.FromAddr, m.FromAddr, StringComparison.OrdinalIgnoreCase))) { i.Category = cat; c.Store.Put(i); }
            c.P.Save(); Say(m.FromAddr + " now goes to " + Sorter.Title(cat) + "."); MailChanged();
        }
        public void SelectSender(string addr)
        {
            Selected.Clear(); foreach (var cv in Conversations()) if (cv.Items.Any(i => string.Equals(i.FromAddr, addr, StringComparison.OrdinalIgnoreCase))) Selected.Add(cv.Key);
            RefreshList();
        }
        void BuildBulk(List<Conv> convs)
        {
            if (bulkHost == null) return;
            bulkHost.Children.Clear();
            var sel = convs.Where(x => Selected.Contains(x.Key)).ToList();
            if (sel.Count > 0)
            {
                var row = new WrapPanel();
                row.Children.Add(new TextBlock { Text = sel.Count + " selected", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
                row.Children.Add(Ui.Btn("Archive", () => { Act(() => { string e = null; foreach (var cv in sel) e = All(cv, c.E.Archive) ?? e; return e; }); Selected.Clear(); }));
                row.Children.Add(Ui.Btn("Delete", () => { if (!Dialogs.Confirm(this, "Move " + sel.Count + " conversation" + (sel.Count == 1 ? "" : "s") + " to Trash?", "They go to the account's Trash folder on the server - nothing is deleted for good.", "Delete", true)) return; Act(() => { string e = null; foreach (var cv in sel) e = All(cv, c.E.Trash) ?? e; return e; }); Selected.Clear(); }));
                row.Children.Add(Ui.Btn("Move to...", () => { var f = Dialogs.PickFolder(this, c, sel[0].Newest.Account); if (f == null) return; Act(() => { string e = null; foreach (var cv in sel) foreach (var i in cv.Items.Where(x => x.Account == sel[0].Newest.Account)) e = c.E.Move(i, null, f) ?? e; return e; }); Selected.Clear(); }));
                row.Children.Add(Ui.Btn("Mark read", () => { foreach (var cv in sel) foreach (var i in cv.Items) c.E.SetSeen(i, true); Selected.Clear(); MailChanged(); }));
                row.Children.Add(Ui.Btn("Clear", () => { Selected.Clear(); RefreshList(); }));
                bulkHost.Children.Add(new Border { Child = row, Background = Ui.R("PanelHi"), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(3) });
            }
            if (Page == "inbox" && string.IsNullOrWhiteSpace(Query))
                foreach (var sg in Suggestions.Make(c.P, c.Store.All()).Take(2))
                {
                    var s = sg; var box = new StackPanel();
                    { var bl = new DockPanel(); var ic = Icons.Make("lightbulb", 12, Ui.R("Amber")); ic.Margin = new Thickness(0, 2, 8, 0); ic.VerticalAlignment = VerticalAlignment.Top; bl.Children.Add(ic); bl.Children.Add(Ui.T(s.Text, 12, false, Ui.R("Text"))); box.Children.Add(bl); }
                    var btns = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                    if (s.Kind == "move") btns.Children.Add(Ui.Btn("Make the rule", () => { var r = new Rule { Id = Guid.NewGuid().ToString("N").Substring(0, 10), Name = "Mail from " + s.Sender, From = s.Sender, MoveTo = s.Folder }; lock (c.P) c.P.Rules.Add(r); c.P.Save(); Say("Rule added: " + Rules.Describe(r)); RefreshList(); }, true));
                    else
                    {
                        btns.Children.Add(Ui.Btn("Unsubscribe...", () => Show("unsubscribe"), true));
                        btns.Children.Add(Ui.Btn("Archive automatically", () => { var r = new Rule { Id = Guid.NewGuid().ToString("N").Substring(0, 10), Name = "Archive " + s.Sender, From = s.Sender, SkipInbox = true, MarkRead = true }; lock (c.P) c.P.Rules.Add(r); c.P.Save(); Say("Rule added: " + Rules.Describe(r)); RefreshList(); }));
                    }
                    btns.Children.Add(Ui.Btn("Not now", () => { lock (c.P) c.P.Dismissed.Add(s.Id); c.P.Save(); RefreshList(); }));
                    box.Children.Add(btns);
                    bulkHost.Children.Add(new Border { Child = box, Background = Ui.R("AmberBg"), BorderBrush = Ui.R("Amber"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(3) });
                }
        }

        // ---------------- status line, undo send, update
        public void Say(string text)
        {
            if (sayText == null) return;
            sayText.Text = text; sayText.Foreground = Ui.R("Text");
            if (sayTimer == null) { sayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) }; sayTimer.Tick += (s, e) => { sayTimer.Stop(); sayText.Foreground = Ui.R("Muted"); }; }
            sayTimer.Stop(); sayTimer.Start();
        }
        public void ShowUndo(Queued q, int seconds)
        {
            undoId = q.Id; undoUntil = DateTime.Now.AddSeconds(seconds); undoBar.Visibility = Visibility.Visible; TickUndo();
            if (Snap.On) undoText.Text = "Sending \"" + q.Subject + "\" in " + seconds + " s";
        }
        public void TickUndo()
        {
            if (undoId == null) return;
            var left = (int)Math.Ceiling((undoUntil - DateTime.Now).TotalSeconds);
            bool still; lock (c.P) still = c.P.Outbox.Any(x => x.Id == undoId);
            if (left <= 0 || !still) { undoId = null; undoBar.Visibility = Visibility.Collapsed; return; }
            Queued q; lock (c.P) q = c.P.Outbox.First(x => x.Id == undoId);
            undoText.Text = "Sending \"" + (q.Subject ?? "") + "\" in " + left + " s";
        }
        public Action<Queued> Undone;
        void DoUndo()
        {
            if (undoId == null) return; var id = undoId; Queued q; lock (c.P) q = c.P.Outbox.FirstOrDefault(x => x.Id == id);
            if (q != null && c.E.Undo(id)) { undoId = null; undoBar.Visibility = Visibility.Collapsed; Say("Not sent - it's back in a new window."); ComposeWindow.Reopen(c, q); }
            else Say("Too late - it's already on its way.");
        }
        public void ShowUpdate(string label) { if (updateBtn != null) { updateBtn.Content = label; updateBtn.Visibility = Visibility.Visible; } }
    }
}
