// Hallow Email - writing mail: rich text or plain, attachments, a signature per account, "Send as" addresses, drafts saved to the
// server's Drafts folder, Send later (queued on this PC - the app must be running at that time) and Undo send (held for a few seconds).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MimeKit;

namespace HallowEmail
{
    public class ComposeState
    {
        public string Account, From, To, Cc, Bcc, Subject, Html, Text, InReplyTo; public List<string> References = new List<string>();
        public bool Rich = true; public uint DraftUid; public List<string[]> Files = new List<string[]>();   // name, base64 (in memory only)
        public static ComposeState New(Controller c, string to)
        {
            var a = c.P.Accounts.FirstOrDefault(); var s = new ComposeState { Account = a == null ? null : a.Id, From = a == null ? null : a.Email, To = to ?? "", Subject = "" };
            if (!string.IsNullOrEmpty(to) && to.Contains("?")) { var q = to.Split(new[] { '?' }, 2); s.To = q[0]; foreach (var kv in q[1].Split('&').Select(x => x.Split(new[] { '=' }, 2)).Where(x => x.Length == 2)) { if (kv[0].Equals("subject", StringComparison.OrdinalIgnoreCase)) s.Subject = Uri.UnescapeDataString(kv[1]); if (kv[0].Equals("body", StringComparison.OrdinalIgnoreCase)) s.Text = Uri.UnescapeDataString(kv[1]); if (kv[0].Equals("cc", StringComparison.OrdinalIgnoreCase)) s.Cc = Uri.UnescapeDataString(kv[1]); } }
            return s;
        }
        // the MIME message (HTML + text alternative when rich)
        public MimeMessage Build(Account a)
        {
            var m = new MimeMessage();
            var from = string.IsNullOrEmpty(From) ? a.Email : From;
            m.From.Add(new MailboxAddress(a.Name ?? "", from));
            Add(m.To, To); Add(m.Cc, Cc); Add(m.Bcc, Bcc);
            m.Subject = Subject ?? "";
            if (!string.IsNullOrEmpty(InReplyTo)) m.InReplyTo = InReplyTo;
            foreach (var r in References) m.References.Add(r);
            var b = new BodyBuilder();
            if (Rich && !string.IsNullOrEmpty(Html)) { b.HtmlBody = "<!DOCTYPE html><html><body style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px\">" + Html + "</body></html>"; b.TextBody = Text ?? Privacy.ToText(Html); }
            else b.TextBody = Text ?? "";
            foreach (var f in Files) b.Attachments.Add(f[0], Convert.FromBase64String(f[1]));
            m.Body = b.ToMessageBody();
            m.Headers.Add("X-Mailer", "Hallow Email " + Program.Version);
            return m;
        }
        static void Add(InternetAddressList l, string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            InternetAddressList p; if (InternetAddressList.TryParse(s.Replace(';', ','), out p)) l.AddRange(p.Mailboxes);
            else throw new FormatException("\"" + s + "\" isn't a list of email addresses.");
        }
        public static ComposeState FromMime(string account, MimeMessage m)
        {
            var s = new ComposeState { Account = account, To = m.To.ToString(), Cc = m.Cc.ToString(), Bcc = m.Bcc.ToString(), Subject = m.Subject, InReplyTo = m.InReplyTo, Html = HtmlBody(m.HtmlBody), Text = m.TextBody, Rich = m.HtmlBody != null };
            var f = m.From.Mailboxes.FirstOrDefault(); if (f != null) s.From = f.Address;
            s.References.AddRange(m.References);
            foreach (var a in m.Attachments.OfType<MimePart>()) using (var ms = new MemoryStream()) { a.Content.DecodeTo(ms); s.Files.Add(new[] { a.FileName ?? "attachment", Convert.ToBase64String(ms.ToArray()) }); }
            return s;
        }
        static string HtmlBody(string h) { if (h == null) return null; var m = Regex.Match(h, "<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase); return m.Success ? m.Groups[1].Value : h; }
    }

    public class ComposeWindow : Window
    {
        readonly Controller c; ComposeState st;
        ComboBox fromBox; TextBox to, cc, bcc, subject, plain; RichTextBox rich; WrapPanel files; TextBlock msg; ToggleButton richToggle;
        bool sentOrDiscarded, dirty;
        public ComposeWindow(Controller c, ComposeState s)
        {
            this.c = c; st = s;
            Ui.Dress(this, string.IsNullOrEmpty(s.Subject) ? "New email - Hallow Email" : s.Subject + " - Hallow Email", 820);
            SizeToContent = SizeToContent.Manual; Height = 680; MinWidth = 600; MinHeight = 460;
            if (c.Main != null && c.Main.IsVisible) { Owner = null; WindowStartupLocation = WindowStartupLocation.CenterScreen; }
            Rebuild();
            Closing += (o, e) =>
            {
                if (sentOrDiscarded || Snap.On || !dirty) return;
                var r = MessageBoxResult.None;
                var keep = Dialogs.Three(this, "Keep this email?", "Save it to Drafts on the server, or throw it away?", "Save draft", "Discard", out r);
                if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
                if (keep) SaveDraft(false);
            };
        }
        public void Rebuild()
        {
            if (fromBox != null) Collect();
            Ui.Restyle(this);
            var root = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };
            // buttons
            var bar = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            msg = new TextBlock { Foreground = Ui.R("Muted"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            var right = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(right, Dock.Right);
            right.Children.Add(Ui.Btn("Discard", () => { sentOrDiscarded = true; Close(); }));
            right.Children.Add(Ui.Btn("Save draft", () => SaveDraft(true)));
            right.Children.Add(Ui.Btn("Send later...", SendLater));
            var send = Ui.Btn("Send", () => Send(null), true); send.Margin = new Thickness(0); send.MinWidth = 90; right.Children.Add(send);
            bar.Children.Add(right); bar.Children.Add(msg);
            DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
            // fields
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            int row = 0;
            Action<string, FrameworkElement> line = (label, el) =>
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var t = Ui.Muted(label, 12.5); t.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(t, row); grid.Children.Add(t);
                el.Margin = new Thickness(0, 0, 0, 6); Grid.SetRow(el, row); Grid.SetColumn(el, 1); grid.Children.Add(el); row++;
            };
            fromBox = new ComboBox { Style = Ui.S("Combo") };
            foreach (var a in c.P.Accounts) { foreach (var addr in new[] { a.Email }.Concat(a.SendAs ?? new List<string>())) fromBox.Items.Add(new ComboBoxItem { Content = (a.Name ?? "") + " <" + addr + ">" + (c.P.Accounts.Count > 1 ? "   (" + a.Display + ")" : ""), Tag = a.Id + "|" + addr, Style = Ui.S("ComboItem") }); }
            foreach (ComboBoxItem it in fromBox.Items) if ((string)it.Tag == st.Account + "|" + (st.From ?? "")) fromBox.SelectedItem = it;
            if (fromBox.SelectedItem == null) foreach (ComboBoxItem it in fromBox.Items) if (((string)it.Tag).StartsWith(st.Account + "|")) { fromBox.SelectedItem = it; break; }
            if (fromBox.SelectedItem == null && fromBox.Items.Count > 0) fromBox.SelectedIndex = 0;
            line("From", fromBox);
            to = Ui.Field(st.To); line("To", to);
            cc = Ui.Field(st.Cc); line("Cc", cc);
            bcc = Ui.Field(st.Bcc); line("Bcc", bcc);
            subject = Ui.Field(st.Subject); subject.FontFamily = (FontFamily)Application.Current.FindResource("BodyFont"); line("Subject", subject);
            DockPanel.SetDock(grid, Dock.Top); root.Children.Add(grid);
            // toolbar
            var tools = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
            richToggle = new ToggleButton { Style = Ui.S("Chip"), Content = "Rich text", IsChecked = st.Rich, Margin = new Thickness(0, 0, 10, 0), ToolTip = "Off = plain text only" };
            richToggle.Click += (o, e) => { Collect(); st.Rich = richToggle.IsChecked == true; if (!st.Rich) { st.Text = st.Text ?? Privacy.ToText(st.Html ?? ""); } else if (string.IsNullOrEmpty(st.Html)) st.Html = TextToHtml(st.Text); Rebuild(); };
            tools.Children.Add(richToggle);
            if (st.Rich)
            {
                foreach (var f in new[] { new object[] { "B", EditingCommands.ToggleBold, FontWeights.Bold }, new object[] { "I", EditingCommands.ToggleItalic, FontWeights.Normal }, new object[] { "U", EditingCommands.ToggleUnderline, FontWeights.Normal }, new object[] { "List", EditingCommands.ToggleBullets, FontWeights.Normal } })
                {
                    var cmd = (RoutedUICommand)f[1];
                    var b = new Button { Style = Ui.S("Btn"), Content = f[0], FontWeight = (FontWeight)f[2], MinWidth = 30, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(8, 3, 8, 3) };
                    if ((string)f[0] == "I") b.FontStyle = FontStyles.Italic;
                    b.Click += (o, e) => { cmd.Execute(null, rich); rich.Focus(); };
                    tools.Children.Add(b);
                }
            }
            var attach = Ui.Btn("Attach...", AddFile); attach.Margin = new Thickness(10, 0, 4, 0); tools.Children.Add(attach);
            var sig = Ui.Btn("Signature", InsertSignature); tools.Children.Add(sig);
            DockPanel.SetDock(tools, Dock.Top); root.Children.Add(tools);
            files = new WrapPanel(); DockPanel.SetDock(files, Dock.Bottom); root.Children.Add(files); ShowFiles();
            // the editor
            if (st.Rich)
            {
                rich = new RichTextBox { Background = Ui.R("Well"), Foreground = Ui.R("Text"), BorderBrush = Ui.R("Line"), Padding = new Thickness(10), FontSize = 14, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsTab = true, CaretBrush = Ui.R("AccentBrush"), FontFamily = (FontFamily)Application.Current.FindResource("BodyFont") };
                rich.Document = HtmlToDoc(st.Html ?? TextToHtml(st.Text ?? Sig(true)));
                rich.Document.Foreground = Ui.R("Text"); rich.TextChanged += (o, e) => dirty = true;
                root.Children.Add(rich); plain = null;
            }
            else
            {
                plain = new TextBox { Text = st.Text ?? Sig(false), AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, Background = Ui.R("Well"), Foreground = Ui.R("Text"), BorderBrush = Ui.R("Line"), Padding = new Thickness(10), FontSize = 14, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CaretBrush = Ui.R("AccentBrush") };
                plain.TextChanged += (o, e) => dirty = true;
                root.Children.Add(plain); rich = null;
            }
            foreach (var t in new[] { to, cc, bcc, subject }) t.TextChanged += (o, e) => dirty = true;
            Content = root;
        }
        string Sig(bool forHtml)
        {
            var a = c.P.Find(st.Account); if (a == null || string.IsNullOrEmpty(a.Signature)) return "";
            return "\n\n-- \n" + a.Signature;
        }
        void InsertSignature()
        {
            Collect(); var a = Acc(); if (a == null || string.IsNullOrEmpty(a.Signature)) { Say("This account has no signature yet - add one in Settings > Accounts."); return; }
            if (st.Rich) st.Html = (st.Html ?? "") + "<p>-- <br>" + System.Net.WebUtility.HtmlEncode(a.Signature).Replace("\n", "<br>") + "</p>"; else st.Text = (st.Text ?? "") + "\n\n-- \n" + a.Signature;
            Rebuild();
        }
        Account Acc() { var t = fromBox.SelectedItem as ComboBoxItem; var id = t == null ? st.Account : ((string)t.Tag).Split('|')[0]; return c.P.Find(id); }
        void Say(string s) { msg.Text = s; }
        void Collect()
        {
            var t = fromBox.SelectedItem as ComboBoxItem;
            if (t != null) { var p = ((string)t.Tag).Split(new[] { '|' }, 2); st.Account = p[0]; st.From = p[1]; }
            st.To = to.Text; st.Cc = cc.Text; st.Bcc = bcc.Text; st.Subject = subject.Text;
            if (rich != null) { st.Html = DocToHtml(rich.Document); st.Text = new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text; }
            if (plain != null) st.Text = plain.Text;
        }
        void AddFile()
        {
            var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Attach files" };
            if (d.ShowDialog(this) != true) return;
            foreach (var f in d.FileNames)
            {
                var fi = new FileInfo(f); if (fi.Length > 25 * 1024 * 1024) { Say(fi.Name + " is over 25 MB - most providers refuse that."); continue; }
                st.Files.Add(new[] { fi.Name, Convert.ToBase64String(File.ReadAllBytes(f)) }); dirty = true;
            }
            ShowFiles();
        }
        void ShowFiles()
        {
            files.Children.Clear();
            foreach (var f in st.Files.ToList())
            {
                var ff = f; var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(Icons.WithText("paperclip", f[0] + "  (" + (f[1].Length * 3 / 4 / 1024) + " KB)", 11, Ui.R("Text")));
                var x = new Button { Style = Ui.S("Btn"), Content = "x", Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Remove" };
                x.Click += (o, e) => { st.Files.Remove(ff); ShowFiles(); }; sp.Children.Add(x);
                files.Children.Add(new Border { Child = sp, Background = Ui.R("PanelHi"), Padding = new Thickness(8, 4, 6, 4), Margin = new Thickness(0, 6, 6, 0), CornerRadius = new CornerRadius(3) });
            }
        }
        string Check(Account a)
        {
            if (a == null) return "Pick the account to send from.";
            if (string.IsNullOrWhiteSpace(st.To) && string.IsNullOrWhiteSpace(st.Cc) && string.IsNullOrWhiteSpace(st.Bcc)) return "Who is it to?";
            try { st.Build(a); } catch (Exception e) { return e.Message; }
            return null;
        }
        public void Send(DateTime? at)
        {
            Collect(); var a = Acc(); var err = Check(a); if (err != null) { Say(err); return; }
            if (string.IsNullOrWhiteSpace(st.Subject) && !Dialogs.Confirm(this, "Send without a subject?", "", "Send", false)) return;
            var msgObj = st.Build(a);
            bool undo = at == null && c.S.UndoSeconds > 0;
            var q = c.E.Queue(a, msgObj, at ?? DateTime.Now.AddSeconds(c.S.UndoSeconds), undo);
            if (st.DraftUid != 0) { var uid = st.DraftUid; c.E.Do(a.Id, cl => { try { c.E.W(a.Id).DeleteFrom(cl, "drafts", uid); } catch { } }); }
            sentOrDiscarded = true;
            if (c.Main != null) { if (undo) c.Main.ShowUndo(q, c.S.UndoSeconds); c.Main.Say(at != null ? "Will be sent " + at.Value.ToString("ddd d MMM HH:mm") + " (Hallow Email needs to be running then)." : undo ? "Sending in " + c.S.UndoSeconds + " s - Undo is at the top." : "Sending..."); }
            Close();
        }
        void SendLater()
        {
            Collect(); var err = Check(Acc()); if (err != null) { Say(err); return; }
            var at = Dialogs.PickTime(this, "Send later", "It waits on this PC and goes out at that time - Hallow Email has to be running then (it sends anything overdue when it next starts).");
            if (at != null) Send(at);
        }
        void SaveDraft(bool stay)
        {
            Collect(); var a = Acc(); if (a == null) { Say("Pick an account first."); return; }
            MimeMessage m; try { m = st.Build(a); } catch (Exception e) { Say(e.Message); return; }
            Say("Saving to Drafts...");
            var old = st.DraftUid;
            var e2 = c.E.SaveDraft(a.Id, m, old);
            if (e2 != null) { Say("Couldn't save the draft: " + e2); return; }
            dirty = false; Say("Saved to Drafts on the server.");
            if (!stay) sentOrDiscarded = true;
        }

        // ---- reply / forward / reopen after Undo
        public static void Reply(Controller c, MailItem m, bool all)
        {
            var a = c.P.Find(m.Account); if (a == null) return;
            var s = new ComposeState { Account = a.Id, From = a.Email };
            s.To = string.IsNullOrEmpty(m.ReplyTo) ? (string.IsNullOrEmpty(m.FromName) ? m.FromAddr : "\"" + m.FromName.Replace("\"", "") + "\" <" + m.FromAddr + ">") : m.ReplyTo;
            if (all)
            {
                var me = new HashSet<string>(new[] { a.Email }.Concat(a.SendAs ?? new List<string>()), StringComparer.OrdinalIgnoreCase);
                var others = new List<string>(); InternetAddressList l;
                foreach (var h in new[] { m.To, m.Cc }) if (h != null && InternetAddressList.TryParse(h, out l)) foreach (var x in l.Mailboxes) if (!me.Contains(x.Address) && !string.Equals(x.Address, m.FromAddr, StringComparison.OrdinalIgnoreCase)) others.Add(x.ToString());
                s.Cc = string.Join(", ", others.Distinct());
            }
            s.Subject = Regex.IsMatch(m.Subject ?? "", @"^\s*re:", RegexOptions.IgnoreCase) ? m.Subject : "Re: " + m.Subject;
            s.InReplyTo = m.MessageId; if (m.References != null) s.References.AddRange(m.References); if (m.MessageId != null) s.References.Add(m.MessageId);
            var quote = (m.Text ?? m.Preview ?? "").Trim();
            s.Html = "<p><br></p><p>" + System.Net.WebUtility.HtmlEncode("On " + m.When.ToString("ddd d MMM yyyy HH:mm") + ", " + m.Sender + " wrote:") + "</p><blockquote>" + System.Net.WebUtility.HtmlEncode(quote).Replace("\n", "<br>") + "</blockquote>";
            s.Text = "\n\nOn " + m.When.ToString("ddd d MMM yyyy HH:mm") + ", " + m.Sender + " wrote:\n" + string.Join("\n", quote.Split('\n').Select(x => "> " + x));
            new ComposeWindow(c, s).Show();
        }
        public static void Forward(Controller c, MailItem m, BodyView v)
        {
            var a = c.P.Find(m.Account); if (a == null) return;
            var s = new ComposeState { Account = a.Id, From = a.Email, Subject = Regex.IsMatch(m.Subject ?? "", @"^\s*(fwd?|fw):", RegexOptions.IgnoreCase) ? m.Subject : "Fwd: " + m.Subject };
            var head = "---------- Forwarded message ----------\nFrom: " + m.Sender + " <" + m.FromAddr + ">\nDate: " + m.When.ToString("ddd d MMM yyyy HH:mm") + "\nSubject: " + m.Subject + "\nTo: " + m.To + "\n\n";
            s.Text = "\n\n" + head + (v != null ? v.Text : m.Text);
            s.Html = "<p><br></p><p>" + System.Net.WebUtility.HtmlEncode(head).Replace("\n", "<br>") + "</p>" + System.Net.WebUtility.HtmlEncode(v != null ? v.Text ?? "" : m.Text ?? "").Replace("\n", "<br>");
            if (v != null) foreach (var at in v.Attachments) s.Files.Add(new[] { at.Name, Convert.ToBase64String(at.Data) });
            new ComposeWindow(c, s).Show();
        }
        public static void Reopen(Controller c, Queued q)
        {
            using (var ms = new MemoryStream(Convert.FromBase64String(q.Mime))) { var m = MimeMessage.Load(ms); new ComposeWindow(c, ComposeState.FromMime(q.Account, m)).Show(); }
        }

        // ---- rich text <-> simple HTML (paragraphs, bold, italic, underline, bullet lists, links)
        public static string DocToHtml(FlowDocument d)
        {
            var sb = new StringBuilder(); foreach (var b in d.Blocks) Block(b, sb); return sb.ToString();
        }
        static void Block(Block b, StringBuilder sb)
        {
            var p = b as Paragraph; var l = b as List; var s = b as Section;
            if (p != null) { sb.Append("<p style=\"margin:0 0 8px 0\">"); foreach (var i in p.Inlines) Inline(i, sb); sb.Append("</p>"); }
            else if (l != null) { sb.Append(l.MarkerStyle == TextMarkerStyle.Decimal ? "<ol>" : "<ul>"); foreach (var li in l.ListItems) { sb.Append("<li>"); foreach (var bb in li.Blocks) { var pp = bb as Paragraph; if (pp != null) foreach (var i in pp.Inlines) Inline(i, sb); else Block(bb, sb); } sb.Append("</li>"); } sb.Append(l.MarkerStyle == TextMarkerStyle.Decimal ? "</ol>" : "</ul>"); }
            else if (s != null) { sb.Append("<blockquote>"); foreach (var bb in s.Blocks) Block(bb, sb); sb.Append("</blockquote>"); }
        }
        static void Inline(Inline i, StringBuilder sb)
        {
            var r = i as Run; var span = i as Span; var lb = i as LineBreak;
            if (lb != null) { sb.Append("<br>"); return; }
            string open = "", close = "";
            if (i.FontWeight == FontWeights.Bold || i.FontWeight == FontWeights.SemiBold) { open += "<b>"; close = "</b>" + close; }
            if (i.FontStyle == FontStyles.Italic) { open += "<i>"; close = "</i>" + close; }
            if (i.TextDecorations != null && i.TextDecorations.Count > 0) { open += "<u>"; close = "</u>" + close; }
            var h = i as Hyperlink; if (h != null && h.NavigateUri != null) { open += "<a href=\"" + System.Net.WebUtility.HtmlEncode(h.NavigateUri.ToString()) + "\">"; close = "</a>" + close; }
            sb.Append(open);
            if (r != null) sb.Append(System.Net.WebUtility.HtmlEncode(r.Text).Replace("\n", "<br>"));
            else if (span != null) foreach (var x in span.Inlines) Inline(x, sb);
            sb.Append(close);
        }
        public static string TextToHtml(string t) { return string.Join("", (t ?? "").Replace("\r", "").Split('\n').Select(l => "<p style=\"margin:0 0 8px 0\">" + System.Net.WebUtility.HtmlEncode(l) + "</p>")); }
        // our own simple HTML back into the editor (anything fancier becomes text)
        public static FlowDocument HtmlToDoc(string html)
        {
            var d = new FlowDocument { PagePadding = new Thickness(0) };
            var tok = new MimeKit.Text.HtmlTokenizer(new StringReader(html ?? "")) { DecodeCharacterReferences = true };
            MimeKit.Text.HtmlToken t; Paragraph p = null; List list = null; bool b = false, it = false, u = false, inQuote = false;
            Func<Paragraph> P = () => { if (p == null) { p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) }; if (inQuote) { p.BorderBrush = Brushes.Gray; p.BorderThickness = new Thickness(2, 0, 0, 0); p.Padding = new Thickness(8, 0, 0, 0); } if (list != null) list.ListItems.Add(new ListItem(p)); else d.Blocks.Add(p); } return p; };
            while (tok.ReadNextToken(out t))
            {
                if (t.Kind == MimeKit.Text.HtmlTokenKind.Tag)
                {
                    var tag = (MimeKit.Text.HtmlTagToken)t; var n = tag.Name.ToLowerInvariant();
                    if (n == "p" || n == "div" || n == "li") { p = null; if (!tag.IsEndTag) P(); }
                    else if (n == "br") { P().Inlines.Add(new LineBreak()); }
                    else if (n == "b" || n == "strong") b = !tag.IsEndTag;
                    else if (n == "i" || n == "em") it = !tag.IsEndTag;
                    else if (n == "u") u = !tag.IsEndTag;
                    else if (n == "ul" || n == "ol") { p = null; if (tag.IsEndTag) list = null; else { list = new List { MarkerStyle = n == "ol" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 8) }; d.Blocks.Add(list); } }
                    else if (n == "blockquote") { p = null; inQuote = !tag.IsEndTag; }
                }
                else if (t.Kind == MimeKit.Text.HtmlTokenKind.Data)
                {
                    var text = ((MimeKit.Text.HtmlDataToken)t).Data; if (text.Trim().Length == 0 && text.Contains("\n")) continue;
                    var r = new Run(text); if (b) r.FontWeight = FontWeights.Bold; if (it) r.FontStyle = FontStyles.Italic; if (u) r.TextDecorations = TextDecorations.Underline;
                    P().Inlines.Add(r);
                }
            }
            if (d.Blocks.Count == 0) d.Blocks.Add(new Paragraph());
            return d;
        }
    }
}
