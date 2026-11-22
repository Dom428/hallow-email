// Hallow Email - adding an account: Gmail, Outlook / Hotmail, Yahoo, iCloud or any IMAP / SMTP server. App passwords (with
// step-by-step help for each provider), or OAuth with a client ID you register yourself. "Test and save" signs in to IMAP and SMTP
// first; the password is stored DPAPI-encrypted only after that (or when you choose "Save anyway").
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;

namespace HallowEmail
{
    public class Preset
    {
        public string Id, Name, Imap, Smtp, Help, HelpUrl, Icon; public int ImapPort = 993, SmtpPort = 465; public string ImapSec = "ssl", SmtpSec = "ssl"; public bool SentByServer; public string OAuth;
        public static readonly List<Preset> All = new List<Preset> {
            new Preset { Id = "gmail", Name = "Gmail", Icon = "google", Imap = "imap.gmail.com", Smtp = "smtp.gmail.com", SentByServer = true, OAuth = "oauth-google", HelpUrl = "https://myaccount.google.com/apppasswords",
                Help = "Gmail needs an app password (your normal password won't work):\n1. Turn on 2-Step Verification: myaccount.google.com > Security.\n2. Open myaccount.google.com/apppasswords, type a name like \"Hallow Email\" and click Create.\n3. Copy the 16-letter password Google shows and paste it below (spaces don't matter).\nIMAP is on for every Gmail account now. Prefer not to use an app password? Pick \"Sign in with Google (OAuth)\" at the top instead." },
            new Preset { Id = "outlook", Name = "Outlook / Hotmail", Icon = "microsoft", Imap = "outlook.office365.com", Smtp = "smtp-mail.outlook.com", SmtpPort = 587, SmtpSec = "starttls", SentByServer = true, OAuth = "oauth-microsoft", HelpUrl = "https://account.microsoft.com/security",
                Help = "Outlook.com / Hotmail / Live:\n1. Turn on two-step verification: account.microsoft.com > Security > Advanced security options.\n2. On the same page, under App passwords, click \"Create a new app password\" and copy it.\n3. Paste it below.\nMicrosoft is phasing out app passwords for IMAP on personal accounts - if it's refused, pick \"Sign in with Microsoft (OAuth)\" at the top instead. Work / school accounts: your admin decides." },
            new Preset { Id = "yahoo", Name = "Yahoo Mail", Icon = "yahoo", Imap = "imap.mail.yahoo.com", Smtp = "smtp.mail.yahoo.com", HelpUrl = "https://login.yahoo.com/account/security",
                Help = "Yahoo Mail needs an app password:\n1. Open login.yahoo.com/account/security (Account security).\n2. Click \"Generate app password\" (or \"Manage app passwords\"), name it \"Hallow Email\" and click Generate.\n3. Copy the password and paste it below." },
            new Preset { Id = "icloud", Name = "iCloud Mail", Icon = "apple", Imap = "imap.mail.me.com", Smtp = "smtp.mail.me.com", SmtpPort = 587, SmtpSec = "starttls", HelpUrl = "https://account.apple.com",
                Help = "iCloud Mail needs an app-specific password (two-factor authentication must be on):\n1. Sign in at account.apple.com > Sign-In and Security > App-Specific Passwords.\n2. Click +, name it \"Hallow Email\", and copy the password (xxxx-xxxx-xxxx-xxxx).\n3. Use your full iCloud address (name@icloud.com) as the email below." },
            new Preset { Id = "custom", Name = "Other (IMAP / SMTP)", Icon = "at", Imap = "", Smtp = "", SmtpPort = 587, SmtpSec = "starttls",
                Help = "Any provider with IMAP and SMTP: your provider's help pages list the server names and ports (IMAP usually 993 with SSL, SMTP 465 SSL or 587 STARTTLS). If it offers app passwords, use one." } };
        public static Preset Get(string id) { return All.FirstOrDefault(p => p.Id == id) ?? All.Last(); }
    }

    public static class AccountTest
    {
        // signs in to IMAP (opens the inbox) and SMTP; null = fine. The password is never part of an error message.
        public static string Run(Account a, string password, string refreshToken, Engine e)
        {
            string stage = "IMAP";
            try
            {
                using (var c = new ImapClient())
                {
                    c.Timeout = 30000;
                    c.Connect(a.ImapHost, a.ImapPort, Engine.Sec(a.ImapSecurity, a.ImapHost));
                    if (a.Auth != "password") { if (refreshToken == null) return "Sign in with " + (a.Auth == "oauth-google" ? "Google" : "Microsoft") + " first."; c.Authenticate(e.Mechanism(a)); }
                    else c.Authenticate(a.UserName ?? a.Email, password ?? "");
                    c.Inbox.Open(MailKit.FolderAccess.ReadOnly); c.Disconnect(true);
                }
                stage = "SMTP";
                using (var s = new SmtpClient())
                {
                    s.Timeout = 30000;
                    s.Connect(a.SmtpHost, a.SmtpPort, Engine.Sec(a.SmtpSecurity, a.SmtpHost));
                    if (a.Auth != "password") s.Authenticate(e.Mechanism(a)); else s.Authenticate(a.UserName ?? a.Email, password ?? "");
                    s.Disconnect(true);
                }
                return null;
            }
            catch (Exception x)
            {
                var m = x.Message ?? ""; if (!string.IsNullOrEmpty(password) && password.Length >= 4) m = m.Replace(password, "<secret>");
                if (x is MailKit.Security.AuthenticationException) return stage + ": the server didn't accept the sign-in. Check the address and the app password. (" + m + ")";
                return stage + ": " + m;
            }
        }
    }

    public class AccountWizard : Window
    {
        readonly Controller c; Preset preset; readonly Account editing;
        TextBox email, name, label, imapHost, imapPort, smtpHost, smtpPort, user, clientId, clientSecret; PasswordBox pass; ComboBox imapSec, smtpSec;
        TextBlock msg; string auth = "password"; string refresh; bool busy;
        public AccountWizard(Controller c, Account edit = null, string presetId = null)
        {
            this.c = c; editing = edit;
            Ui.Dress(this, edit == null ? "Add an account - Hallow Email" : "Account - Hallow Email", 640);
            ResizeMode = ResizeMode.CanResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (edit != null) { preset = Preset.Get(edit.Preset); auth = edit.Auth ?? "password"; Details(); }
            else if (presetId != null) { preset = Preset.Get(presetId); Details(); }
            else Pick();
        }
        void Pick()
        {
            var sp = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            sp.Children.Add(Ui.T("Which kind of account?", 17, true, null, 4));
            sp.Children.Add(Ui.Muted("Hallow Email signs in with an app password (or OAuth with your own client ID) - never with your main password if you can avoid it. Everything it keeps is encrypted on this PC.", 12, 14));
            foreach (var p in Preset.All)
            {
                var pp = p; var b = new Button { Style = Ui.S("Btn"), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8), Content = Icons.WithText(p.Icon, p.Name, 15, Ui.R("Text")) };
                b.Click += (s, e) => { preset = pp; Details(); };
                sp.Children.Add(b);
            }
            Content = sp;
        }
        public void Details()
        {
            var sp = new StackPanel { Margin = new Thickness(24, 18, 24, 18) };
            sp.Children.Add(Icons.WithText(preset.Icon, preset.Name, 17, Ui.R("Text")));
            var e = editing;
            // the auth choice
            if (preset.OAuth != null)
            {
                var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) };
                var a1 = new System.Windows.Controls.Primitives.ToggleButton { Style = Ui.S("Chip"), Content = "App password", IsChecked = auth == "password", Margin = new Thickness(0, 0, 6, 0) };
                var a2 = new System.Windows.Controls.Primitives.ToggleButton { Style = Ui.S("Chip"), Content = "Sign in with " + (preset.OAuth == "oauth-google" ? "Google" : "Microsoft") + " (OAuth)", IsChecked = auth != "password" };
                a1.Click += (s, x) => { Keep(); auth = "password"; Details(); }; a2.Click += (s, x) => { Keep(); auth = preset.OAuth; Details(); };
                row.Children.Add(a1); row.Children.Add(a2); sp.Children.Add(row);
            }
            var help = auth == "password" ? preset.Help : OAuth.For(auth).ConsoleHelp + "\n\nWhy your own client ID? Hallow Email has no company server or shared app registration - you register a tiny app for yourself, and only your PC ever sees the tokens. The refresh token is stored DPAPI-encrypted.";
            var hb = new StackPanel(); hb.Children.Add(Ui.T(help, 12, false, Ui.R("Text")));
            if (auth == "password" && preset.HelpUrl != null) { var open = Ui.Btn("Open " + preset.HelpUrl.Replace("https://", ""), () => Browse(preset.HelpUrl)); open.HorizontalAlignment = HorizontalAlignment.Left; open.Margin = new Thickness(0, 8, 0, 0); hb.Children.Add(open); }
            sp.Children.Add(new Border { Child = hb, Background = Ui.R("LilacBg"), BorderBrush = Ui.R("Lilac"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 0, 10) });
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            int r = 0;
            Action<string, FrameworkElement> line = (l, el) => { g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var t = Ui.Muted(l, 12.5); t.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(t, r); g.Children.Add(t); el.Margin = new Thickness(0, 0, 0, 6); Grid.SetRow(el, r); Grid.SetColumn(el, 1); g.Children.Add(el); r++; };
            email = Ui.Field(e != null ? e.Email : V(email)); line("Email address", email);
            name = Ui.Field(e != null ? e.Name : V(name)); name.FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("BodyFont"); line("Your name", name);
            label = Ui.Field(e != null ? e.Label : V(label) ?? (preset.Id == "custom" ? "" : preset.Name.Split(' ')[0])); line("Label in the list", label);
            if (auth == "password") { pass = new PasswordBox { Style = Ui.S("Pass") }; if (passKeep != null) pass.Password = passKeep; line(preset.Id == "custom" ? "Password / app password" : "App password", pass); }
            else
            {
                clientId = Ui.Field(e != null ? e.OAuthClientId : V(clientId)); line("Client ID", clientId);
                if (auth == "oauth-google") { clientSecret = Ui.Field(V(clientSecret)); line("Client secret", clientSecret); }
                var si = Ui.Btn(refresh != null || (e != null && Secrets.Has("rt:" + e.Id)) ? "Signed in - sign in again" : "Sign in in my browser", SignIn, true); si.HorizontalAlignment = HorizontalAlignment.Left; line("", si);
            }
            bool custom = preset.Id == "custom" || e != null;
            if (custom)
            {
                imapHost = Ui.Field(e != null ? e.ImapHost : V(imapHost) ?? preset.Imap); line("IMAP server", imapHost);
                imapPort = Ui.Field((e != null ? e.ImapPort : preset.ImapPort).ToString(), 90); imapSec = Sec(e != null ? e.ImapSecurity : preset.ImapSec);
                line("IMAP port, security", new StackPanel { Orientation = Orientation.Horizontal, Children = { imapPort, imapSec } });
                smtpHost = Ui.Field(e != null ? e.SmtpHost : V(smtpHost) ?? preset.Smtp); line("SMTP server", smtpHost);
                smtpPort = Ui.Field((e != null ? e.SmtpPort : preset.SmtpPort).ToString(), 90); smtpSec = Sec(e != null ? e.SmtpSecurity : preset.SmtpSec);
                line("SMTP port, security", new StackPanel { Orientation = Orientation.Horizontal, Children = { smtpPort, smtpSec } });
                user = Ui.Field(e != null ? e.UserName : V(user)); user.ToolTip = "Leave empty to use the email address"; line("User name (if different)", user);
            }
            sp.Children.Add(g);
            msg = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 12.5 }; sp.Children.Add(msg);
            var btns = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            if (e == null) btns.Children.Add(Ui.Btn("Back", Pick));
            btns.Children.Add(Ui.Btn("Save anyway", () => Save(true)));
            var ok = Ui.Btn("Test and save", () => Save(false), true); ok.Margin = new Thickness(0); btns.Children.Add(ok);
            sp.Children.Add(btns);
            Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        string passKeep;
        void Keep() { if (pass != null) passKeep = pass.Password; }
        static string V(TextBox t) { return t == null ? null : t.Text; }
        ComboBox Sec(string v)
        {
            var cb = new ComboBox { Style = Ui.S("Combo"), Width = 150, Margin = new Thickness(8, 0, 0, 0) };
            foreach (var o in new[] { new[] { "ssl", "SSL/TLS" }, new[] { "starttls", "STARTTLS" } }) { var it = new ComboBoxItem { Content = o[1], Tag = o[0], Style = Ui.S("ComboItem") }; cb.Items.Add(it); if (o[0] == v) cb.SelectedItem = it; }
            if (cb.SelectedItem == null) cb.SelectedIndex = 0; return cb;
        }
        void Browse(string url) { if (Snap.On || MailSettings.TestDir != null) return; try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
        Account Build()
        {
            var a = editing != null ? editing : new Account { Id = Guid.NewGuid().ToString("N").Substring(0, 8) };
            var ad = (email.Text ?? "").Trim();
            a.Email = ad; a.Name = name.Text.Trim(); a.Label = label.Text.Trim(); a.Preset = preset.Id; a.Auth = auth;
            if (imapHost != null)
            {
                a.ImapHost = imapHost.Text.Trim(); int p; a.ImapPort = int.TryParse(imapPort.Text, out p) ? p : 993; a.ImapSecurity = (string)((ComboBoxItem)imapSec.SelectedItem).Tag;
                a.SmtpHost = smtpHost.Text.Trim(); a.SmtpPort = int.TryParse(smtpPort.Text, out p) ? p : 587; a.SmtpSecurity = (string)((ComboBoxItem)smtpSec.SelectedItem).Tag;
                a.UserName = string.IsNullOrWhiteSpace(user.Text) ? null : user.Text.Trim();
            }
            else { a.ImapHost = preset.Imap; a.ImapPort = preset.ImapPort; a.ImapSecurity = preset.ImapSec; a.SmtpHost = preset.Smtp; a.SmtpPort = preset.SmtpPort; a.SmtpSecurity = preset.SmtpSec; }
            a.SentSavedByServer = preset.SentByServer;
            if (clientId != null) a.OAuthClientId = clientId.Text.Trim();
            if (a.SendAs == null) a.SendAs = new List<string>();
            return a;
        }
        void Say(string s, string key) { msg.Text = s; msg.Foreground = Ui.R(key); }
        void SignIn()
        {
            var a = Build(); var p = OAuth.For(auth);
            if (string.IsNullOrWhiteSpace(a.OAuthClientId)) { Say("Paste your client ID first (see the steps above).", "Red"); return; }
            var secret = clientSecret == null ? null : clientSecret.Text.Trim();
            Say("Finish signing in in your browser... (Hallow Email never sees your password)", "Muted");
            Task.Factory.StartNew(() => OAuth.SignIn(p, a.OAuthClientId, secret, a.Email, url => Browse(url), CancellationToken.None)).ContinueWith(t =>
            {
                var tk = t.Exception != null ? new OAuth.Tokens { Error = t.Exception.InnerException.Message } : t.Result;
                if (tk.Refresh == null) { Say("Sign-in didn't work: " + (tk.Error ?? "no refresh token came back - check offline access in your app registration."), "Red"); return; }
                refresh = tk.Refresh; Say("Signed in. Click \"Test and save\".", "Green");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        void Save(bool anyway)
        {
            if (busy) return;
            var a = Build();
            if (!System.Text.RegularExpressions.Regex.IsMatch(a.Email ?? "", @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) { Say("That email address doesn't look right.", "Red"); return; }
            if (string.IsNullOrEmpty(a.ImapHost) || string.IsNullOrEmpty(a.SmtpHost)) { Say("Fill in the IMAP and SMTP servers.", "Red"); return; }
            var pw = pass != null ? pass.Password.Replace(" ", "") : null; if (preset.Id == "custom" && pass != null) pw = pass.Password;
            if (auth == "password" && string.IsNullOrEmpty(pw) && (editing == null || !Secrets.Has("pw:" + editing.Id))) { Say("Paste the app password.", "Red"); return; }
            if (auth == "password" && string.IsNullOrEmpty(pw)) pw = Secrets.Get("pw:" + a.Id);
            var secret = clientSecret == null ? null : clientSecret.Text.Trim();
            if (auth != "password") { if (refresh == null && editing != null) refresh = Secrets.Get("rt:" + a.Id); if (refresh == null) { Say("Sign in with " + OAuth.For(auth).Name + " first.", "Red"); return; } Secrets.Set("rt:" + a.Id, refresh); if (!string.IsNullOrEmpty(secret)) Secrets.Set("cs:" + a.Id, secret); }
            if (anyway) { c.AddAccount(a, pw, refresh, secret); Close(); return; }
            busy = true; Say("Signing in to " + a.ImapHost + " and " + a.SmtpHost + "...", "Muted");
            var rt = refresh;
            Task.Factory.StartNew(() => AccountTest.Run(a, pw, rt, c.E)).ContinueWith(t =>
            {
                busy = false; var err = t.Result;
                if (err != null) { Say(err + "  (\"Save anyway\" keeps it and keeps trying.)", "Red"); return; }
                c.AddAccount(a, pw, rt, secret); c.Say("Account added - your mail is on its way."); Close();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
