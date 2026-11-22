// Hallow Email - "HallowEmail.exe --snap <folder>": every window rendered off-screen to PNGs, in dark and light, with MADE-UP
// mailboxes (example.com / example.net addresses, invented people). Never a real account, never the network, nothing saved.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using MimeKit;

namespace HallowEmail
{
    public static class SnapShots
    {
        public static readonly DateTime Now = new DateTime(2026, 11, 20, 14, 30, 0);

        public static void All(string dir)
        {
            foreach (var light in new[] { false, true }) { Program.ForceLight = light; Program.LoadTheme(Application.Current); Pass(dir, light ? "-light" : ""); }
        }
        static void Pass(string dir, string sfx)
        {
            Func<string, string> F = n => Path.Combine(dir, n + sfx + ".png");
            Trackers.ShieldRulesDir = Path.Combine(dir, "no-shield-lists");
            var store = new MailStore(Path.Combine(dir, "unused")) { Memory = true };
            var p = Profile0();
            var c = new Controller(true, true, p, store);
            Fill(c);
            var w = new MainWindow(c);
            Action<string> shot = n => Snap.Save(w, F(n), 1360, 840);
            w.Show("inbox"); Open(w, "Sam Lee"); shot("inbox");
            w.Tab = "newsletters"; w.Show("inbox"); Open(w, "Trailhead Weekly"); shot("newsletter-trackers");
            w.Tab = "receipts"; w.Show("inbox"); Open(w, "Northwind Supplies"); shot("receipt-attachments");
            w.Tab = "all"; w.Show("inbox"); shot("inbox-all");
            w.Show("spam"); Open(w, "PayPal"); shot("spam-phishing");
            c.S.Screener = true; w.Show("screener"); Open(w, "Jordan Blake"); shot("screener"); c.S.Screener = false;
            w.Tab = "people"; w.Show("inbox"); w.Query = "invoice"; w.Show("inbox"); shot("search"); w.Query = "";
            w.Show("inbox"); w.SelectSender("hello@trailhead-weekly.example"); w.Tab = "newsletters"; w.Show("inbox"); w.SelectSender("hello@trailhead-weekly.example"); shot("bulk-select"); w.Selected.Clear();
            w.Tab = "people"; w.Show("inbox"); var q = new Queued { Id = "snap", Subject = "Re: Saturday hike", To = "sam@example.org", SendAt = Now.AddSeconds(8).ToString("o") }; lock (p) p.Outbox.Add(q); w.ShowUndo(q, 8); shot("undo-send"); lock (p) p.Outbox.Clear(); w.TickUndo();
            foreach (var pg in new[] { "settings", "rules", "unsubscribe", "masked" }) { w.Show(pg); shot(pg); }
            lock (p) p.Outbox.Add(new Queued { Id = "later", Account = "acc1", Subject = "Monthly report", To = "team@northwind.example", SendAt = Now.AddDays(1).Date.AddHours(8).ToString("o") });
            w.Show("outbox"); shot("outbox"); lock (p) p.Outbox.Clear();
            // compose (rich), the account wizard, dialogs
            var st = new ComposeState { Account = "acc1", From = "alex.rivera@example.com", To = "sam@example.org", Subject = "Re: Saturday hike", Html = "<p>Sounds great - I'll bring the <b>map</b> and snacks.</p><ul><li>Meet at 8:30</li><li>Car park by the lake</li></ul><p>-- <br>Alex</p>" };
            st.Files.Add(new[] { "route.gpx", Convert.ToBase64String(new byte[2400]) });
            Snap.Save(new ComposeWindow(c, st), F("compose"), 820, 680);
            Snap.Save(new AccountWizard(c), F("wizard"), 640, 0);
            Snap.Save(new AccountWizard(c, null, "gmail"), F("wizard-gmail"), 640, 0);
            var wz = new AccountWizard(c, null, "outlook"); Snap.Save(wz, F("wizard-outlook"), 640, 0);
            Snap.Save(new AccountWizard(c, null, "custom"), F("wizard-custom"), 640, 0);
            Snap.Save(PageBuilder.UnsubWindow("hello@trailhead-weekly.example", "Trailhead Weekly", Unsubscribe.Plan("<https://trailhead-weekly.example/u/8c1f>", "List-Unsubscribe=One-Click"), null), F("dialog-unsubscribe"), 480, 0);
            Snap.Save(Dialogs.TimeWindow("Send later", "It waits on this PC and goes out at that time - Hallow Email has to be running then.", null, Now), F("dialog-send-later"), 440, 0);
            Snap.Save(PageBuilder.RuleWindow(c, new Rule { Id = "r", Name = "Receipts to their folder", From = "orders@", MoveTo = "Receipts", Label = "receipts", MarkRead = true }, true, null), F("dialog-rule"), 560, 0);
            Snap.Save(Dialogs.ConfirmWindow("Open login-paypal-secure.example?", "It opens in your web browser:\nhttps://login-paypal-secure.example/verify\n\nCareful: the link's text says www.paypal.com, but it really goes to login-paypal-secure.example.", "Open in browser", true, null), F("dialog-link"), 480, 0);
            Snap.Save(CrashNotice.Make(Path.Combine(dir, "example.txt")), F("crash"), 440, 0);
        }
        static void Open(MainWindow w, string sender)
        {
            var cv = w.Conversations().FirstOrDefault(x => x.Items.Any(i => (i.FromName ?? "").StartsWith(sender)));
            if (cv != null) w.Open(cv.Items.First(i => (i.FromName ?? "").StartsWith(sender)), cv);
        }

        static Profile Profile0()
        {
            var p = new Profile(); p.Fix();
            p.Accounts.Add(new Account { Id = "acc1", Email = "alex.rivera@example.com", Name = "Alex Rivera", Label = "Personal", Preset = "gmail", ImapHost = "imap.gmail.com", SmtpHost = "smtp.gmail.com", SendAs = new List<string> { "alex@rivera-family.example" }, Signature = "Alex" });
            p.Accounts.Add(new Account { Id = "acc2", Email = "alex@northwind.example", Name = "Alex Rivera", Label = "Work", Preset = "custom", ImapHost = "mail.northwind.example", SmtpHost = "mail.northwind.example", SendAs = new List<string>() });
            p.Trusted.AddRange(new[] { "sam@example.org", "mum@example.net", "casey@northwind.example" });
            p.Known.AddRange(new[] { "sam@example.org", "mum@example.net", "casey@northwind.example", "hello@trailhead-weekly.example", "orders@northwind-supplies.example", "news@pixelforge.example" });
            p.Rules.Add(new Rule { Id = "r1", Name = "Receipts to their folder", From = "orders@", MoveTo = "Receipts", MarkRead = true, Hits = 14 });
            p.Rules.Add(new Rule { Id = "r2", Name = "Build mail", Subject = "[ci]", Label = "builds", SkipInbox = true, Hits = 52, Account = "acc2" });
            for (int i = 0; i < 4; i++) p.Moves.Add(new MoveNote { Sender = "news@pixelforge.example", Folder = "Games", When = Now.AddDays(-i).ToString("s") });
            p.Opens["digest@citycouncil.example"] = new[] { 9, 0 };
            p.SenderCategory["@pixelforge.example"] = "newsletters";
            p.ImageSenders.Add("hello@trailhead-weekly.example");
            p.CfZone = "rivera-family.example"; p.CfZoneId = "zone"; p.CfForwardTo = "alex.rivera@example.com";
            p.MaskedAddresses.Add(new Masked { Address = "shop.7c2e91a4@rivera-family.example", Note = "online shop", Created = Now.AddDays(-12).ToString("s"), RuleId = "x" });
            p.MaskedAddresses.Add(new Masked { Address = "forum.b14d0e33@rivera-family.example", Note = "forum", Created = Now.AddDays(-40).ToString("s"), RuleId = "y", Off = true });
            p.Bayes = Spam.Seeded();
            return p;
        }
        static void Fill(Controller c)
        {
            foreach (var a in c.P.Accounts)
            {
                var idx = c.Store.Index(a.Id);
                idx.Folders = new List<FolderInfo> { new FolderInfo { Name = "INBOX", Role = "inbox", UidValidity = 1 }, new FolderInfo { Name = a.Id == "acc1" ? "[Gmail]/Sent Mail" : "Sent", Role = "sent", UidValidity = 1 },
                    new FolderInfo { Name = a.Id == "acc1" ? "[Gmail]/Drafts" : "Drafts", Role = "drafts", UidValidity = 1 }, new FolderInfo { Name = a.Id == "acc1" ? "[Gmail]/Spam" : "Junk", Role = "junk", UidValidity = 1 },
                    new FolderInfo { Name = "Archive", Role = "archive", UidValidity = 1 }, new FolderInfo { Name = "Receipts", UidValidity = 1 } };
                if (a.Id == "acc1") idx.Folders.Add(new FolderInfo { Name = "Games", UidValidity = 1 });
            }
            uint uid = 100;
            Func<string, string, string, string, string, string, double, bool, Action<MimeMessage>, MailItem> mk = (acc, folder, fromName, fromAddr, subject, body, hours, seen, extra) =>
            {
                var m = new MimeMessage(); m.From.Add(new MailboxAddress(fromName, fromAddr)); m.To.Add(new MailboxAddress("Alex Rivera", acc == "acc1" ? "alex.rivera@example.com" : "alex@northwind.example"));
                m.Subject = subject; m.Date = new DateTimeOffset(Now.AddHours(-hours)); m.MessageId = "snap" + (uid + 1) + "@example.com";
                var bb = new BodyBuilder(); if (body.StartsWith("<")) { bb.HtmlBody = body; bb.TextBody = Privacy.ToText(body); } else bb.TextBody = body;
                if (extra != null) { var tmp = new MimeMessage(); extra(tmp); foreach (var h in tmp.Headers) m.Headers.Add(h.Field, h.Value); }
                if (extra != null && m.Headers.Contains("X-Snap-Attach")) { foreach (var n in m.Headers["X-Snap-Attach"].Split(',')) bb.Attachments.Add(n.Trim(), n.EndsWith(".zip") ? Zip() : new byte[3000]); m.Headers.Remove("X-Snap-Attach"); }
                m.Body = bb.ToMessageBody();
                byte[] raw; using (var ms = new MemoryStream()) { m.WriteTo(ms); raw = ms.ToArray(); }
                var fi = c.Store.Index(acc).Folders.First(f => f.Name == folder || (folder == "sent" && f.Role == "sent") || (folder == "junk" && f.Role == "junk"));
                var it = Parse.Item(acc, fi, new MailKit.UniqueId(1, ++uid), seen ? MailKit.MessageFlags.Seen : MailKit.MessageFlags.None, m.Headers, raw.Length, null);
                c.Store.PutBody(it, raw); Worker.Fill(it, Parse.Body(raw));
                if (fi.Role == "inbox") c.E.Classify(it, Parse.Body(raw), fi); else it.Category = Sorter.Categorize(it, c.P);
                it.Seen = seen; c.Store.Put(it); return it;
            };
            Action<MimeMessage> news = m => { m.Headers.Add("List-Unsubscribe", "<https://trailhead-weekly.example/u/8c1f>, <mailto:unsub@trailhead-weekly.example?subject=unsubscribe>"); m.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click"); m.Headers.Add("List-Id", "Trailhead Weekly <weekly.trailhead-weekly.example>"); };
            Action<MimeMessage> pass = m => m.Headers.Add("Authentication-Results", "mx.example.com; spf=pass smtp.mailfrom=example.org; dkim=pass header.d=example.org; dmarc=pass");
            var s1 = mk("acc1", "INBOX", "Sam Lee", "sam@example.org", "Saturday hike", "Hey Alex,\n\nAre we still on for the hike on Saturday? The forecast looks dry. I was thinking we start from the lake car park around 8:30.\n\nSam", 30, true, pass);
            var s2 = mk("acc1", "[Gmail]/Sent Mail", "Alex Rivera", "alex.rivera@example.com", "Re: Saturday hike", "Yes! 8:30 works. Should I bring anything?", 28, true, null); s2.To = "Sam Lee <sam@example.org>";
            var s3 = mk("acc1", "INBOX", "Sam Lee", "sam@example.org", "Re: Saturday hike", "Great. Bring snacks and the paper map - my phone dies in the cold.\n\nAlso, Riley might join us. See you Saturday!\n\nSam", 1.5, false, m => { pass(m); m.Headers.Add("In-Reply-To", "<snap102@example.com>"); });
            mk("acc1", "INBOX", "Mum", "mum@example.net", "Sunday lunch", "Lunch at ours on Sunday? Dad is making his famous roast. Let me know if you're coming.\n\nLove, Mum", 5, false, pass);
            mk("acc2", "INBOX", "Casey Morgan", "casey@northwind.example", "Q4 planning notes", "Hi Alex, attached are the notes from this morning. Can you review the budget section before Thursday?\n\nThanks, Casey", 3, false, m => { pass(m); m.Headers.Add("X-Snap-Attach", "Q4-planning.docx"); });
            mk("acc1", "INBOX", "Trailhead Weekly", "hello@trailhead-weekly.example", "10 winter trails worth the drive", "<html><body><h2>Trailhead Weekly</h2><img src=\"https://img.trailhead-weekly.example/hero.jpg\" width=\"600\"><p>This week: ten winter trails, a gear guide and reader photos.</p><p><a href=\"https://trk.trailhead-weekly.example/c/8c1f9a2b7d4e6f1a3c5e?u=7781\">www.trailhead-weekly.example/winter</a></p><img src=\"https://pixel.watch/o/8c1f9a2b7d4e6f1a3c5e7b9d.gif\" width=\"1\" height=\"1\"><img src=\"https://www.google-analytics.com/collect?v=1&tid=UA-1&cid=555\"></body></html>", 4, false, news);
            mk("acc1", "INBOX", "PixelForge", "news@pixelforge.example", "Winter sale: 60% off indie games", "<html><body><p>The winter sale is live!</p><img src=\"https://cdn.pixelforge.example/sale.png\"><img src=\"https://t.pixelforge.example/open/a8f2c9e1d7b34a6f9e2c1d8b7a6f5e4d3c2b1a0f\" width=\"1\" height=\"1\"></body></html>", 20, true, m => m.Headers.Add("List-Unsubscribe", "<mailto:leave@pixelforge.example>"));
            mk("acc1", "INBOX", "City Council", "digest@citycouncil.example", "Your weekly neighbourhood digest", "Bin collection changes, road works on Elm Street, and the winter market dates.", 50, false, m => m.Headers.Add("List-Unsubscribe", "<https://citycouncil.example/unsub?id=4411>"));
            mk("acc1", "INBOX", "Northwind Supplies", "orders@northwind-supplies.example", "Your order 48213 has shipped", "Thanks for your order! It has shipped and should arrive on Monday. Your invoice is attached.", 9, true, m => { pass(m); m.Headers.Add("X-Snap-Attach", "invoice-48213.pdf, tracking-label.pdf.exe, photos.zip"); });
            mk("acc1", "INBOX", "Streamline", "billing@streamline.example", "Receipt for your monthly plan", "Thanks for your payment of 9.99. This receipt is for your records.", 26, true, pass);
            mk("acc1", "INBOX", "Codehub", "notifications@codehub.example", "Riley mentioned you in issue #42", "@alex can you take a look at this? The build fails on the release branch.", 2, false, m => m.Headers.Add("Auto-Submitted", "auto-generated"));
            mk("acc1", "INBOX", "Accounts", "no-reply@accounts.example.com", "New sign-in to your account", "A new sign-in from Windows on Thursday. If this was you, there's nothing to do.", 7, true, null);
            mk("acc2", "INBOX", "CI", "ci@builds.northwind.example", "[ci] nightly build succeeded", "412 tests passed.", 12, true, m => m.Headers.Add("Auto-Submitted", "auto-generated"));
            // spam / phishing (moved to Junk by the filter)
            var ph = mk("acc1", "[Gmail]/Spam", "PayPal", "service@paypa1-secure.example", "URGENT: your account will be suspended", "<html><body><p>Dear customer,</p><p>We detected unusual activity. Verify your account within 24 hours or it will be locked.</p><p><a href=\"https://login-paypal-secure.example/verify\">www.paypal.com/verify</a></p></body></html>", 6, false, m => m.Headers.Add("Authentication-Results", "mx.example.com; spf=softfail smtp.mailfrom=paypa1-secure.example; dkim=none; dmarc=fail"));
            ph.SpamWhy = "The name says PayPal but the address isn't PayPal's. DMARC failed - the sender's domain doesn't vouch for it. The sender's domain is a look-alike of a well-known one. A link's text names www.paypal.com but it goes to login-paypal-secure.example."; ph.Spam = 0.97; c.Store.Put(ph);
            var sp2 = mk("acc1", "[Gmail]/Spam", "Lucky Rewards", "promo@lucky-rewards.example", "You have WON a $1,000 gift card!!", "Congratulations you have been selected. Click below to claim your prize: https://bit.ly/xx", 33, true, null);
            sp2.SpamWhy = "It reads like the spam you've marked before (99% by the learning filter). Typical spam wording (\"you have won\", \"claim your prize\")."; sp2.Spam = 0.93; c.Store.Put(sp2);
            // the Screener
            var j = mk("acc1", "INBOX", "Jordan Blake", "jordan.blake@example.net", "Quick question about the bike for sale", "Hi! Is the bike you listed still available? I could pick it up this weekend.", 0.5, false, null); j.Screened = true; c.Store.Put(j);
            var k = mk("acc1", "INBOX", "Morgan Recruiting", "talent@recruit.example", "An exciting opportunity", "I came across your profile and think you'd be a great fit.", 8, false, null); k.Screened = true; c.Store.Put(k);
            foreach (var a in c.P.Accounts) { var idx = c.Store.Index(a.Id); Threads.Assign(idx.Items); }
        }
        static byte[] Zip()
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true)) { var e = z.CreateEntry("photo_001.jpg.scr"); using (var s = e.Open()) s.Write(new byte[10], 0, 10); }
                return ms.ToArray();
            }
        }
    }
}
