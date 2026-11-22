// Hallow Email tests: "EmailTests.exe" runs everything and prints one line per check ("ok ..." / "FAIL ..."), then a summary.
// Only made-up data and local stand-ins (tests\FakeServers.cs): a test data folder under tests\out, a fake IMAP / SMTP server on
// 127.0.0.1, stand-in web endpoints, a stand-in Defender. Never a real account, never real mail, nothing sent off this PC.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MailKit.Security;
using MimeKit;

namespace HallowEmail.Tests
{
    public static class Run
    {
        static int pass, fail; static readonly List<string> fails = new List<string>();
        static void Ok(bool c, string what) { if (c) { pass++; Console.WriteLine("ok   " + what); } else { fail++; fails.Add(what); Console.WriteLine("FAIL " + what); } }
        static void Eq<T>(T want, T got, string what) { Ok(Equals(want, got), what + (Equals(want, got) ? "" : " (want " + want + ", got " + got + ")")); }
        static bool Until(Func<bool> f, int ms) { var end = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < end) { try { if (f()) return true; } catch { } Thread.Sleep(50); } try { return f(); } catch { return false; } }
        static string Out; static readonly List<Process> Children = new List<Process>();

        [STAThread]
        public static int Main(string[] args)
        {
            Program.Resolver();
            // 5-minute kill timeout: a hung server stand-in or pipe can't hang the build
            new Thread(() => { Thread.Sleep(TimeSpan.FromMinutes(5)); Console.WriteLine("FAIL the tests took longer than 5 minutes - killed"); foreach (var pr in Children) try { if (!pr.HasExited) pr.Kill(); } catch { } Environment.Exit(3); }) { IsBackground = true }.Start();
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            Out = Path.Combine(Path.GetDirectoryName(typeof(Run).Assembly.Location), "out");
            try { if (Directory.Exists(Out)) Directory.Delete(Out, true); } catch { }
            Directory.CreateDirectory(Out);
            MailSettings.OverrideDir = Path.Combine(Out, "data"); Directory.CreateDirectory(MailSettings.OverrideDir);
            Trackers.ShieldRulesDir = Path.Combine(Out, "no-lists");
            Dialogs.AutoAnswer = t => true;
            foreach (var t in new Action[] { VaultTests, CacheTests, LookTests, PrivacyTests, ShieldListTests, PhishTests, AttachmentTests, DefenderTests, SortTests, ThreadSearchTests,
                SpamTests, RuleTests, SuggestionTests, ScreenerTests, UnsubTests, CloudflareTests, OAuthTests, NoteTests, ImapTests, AttentionTests, PipeTests, () => WebViewTests.Run(Ok, Out) })
            {
                try { t(); } catch (Exception e) { Ok(false, t.Method.Name + " threw " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace); }
            }
            Console.WriteLine(fail == 0 ? "ALL " + pass + " PASSED" : fail + " FAILED, " + pass + " passed: " + string.Join("; ", fails.Take(8)));
            return fail == 0 ? 0 : 1;
        }

        static byte[] Mail(string fromName, string from, string to, string subject, string text, Action<MimeMessage> extra = null, string html = null, string msgId = null)
        {
            var m = new MimeMessage(); m.From.Add(new MailboxAddress(fromName, from)); m.To.Add(MailboxAddress.Parse(to)); m.Subject = subject; m.Date = DateTimeOffset.Now;
            m.MessageId = msgId ?? MimeKit.Utils.MimeUtils.GenerateMessageId("example.test");
            var b = new BodyBuilder { TextBody = text }; if (html != null) b.HtmlBody = html; m.Body = b.ToMessageBody();
            if (extra != null) extra(m);
            using (var ms = new MemoryStream()) { m.WriteTo(ms); return ms.ToArray(); }
        }
        static MailItem Item(string fromName, string from, string subject, string text = "", Action<MailItem> f = null)
        {
            var it = new MailItem { Account = "a", Folder = "INBOX", UidValidity = 1, Uid = (uint)new Random().Next(1, 1000000), FromName = fromName, FromAddr = from, Subject = subject, Text = text, Date = DateTime.Now.ToString("s") };
            if (f != null) f(it); return it;
        }

        // ---------------- encryption at rest
        static void VaultTests()
        {
            Vault.ResetKeyForTests();
            var plain = Encoding.UTF8.GetBytes("hello secret mail body 123");
            var enc = Vault.Encrypt(plain);
            Ok(!Encoding.UTF8.GetString(enc).Contains("secret"), "vault: ciphertext doesn't contain the text");
            Eq("hello secret mail body 123", Encoding.UTF8.GetString(Vault.Decrypt(enc)), "vault: round trip");
            Ok(File.Exists(Path.Combine(MailSettings.Dir, "cache.key")), "vault: key file created");
            Ok(!File.ReadAllBytes(Path.Combine(MailSettings.Dir, "cache.key")).SequenceEqual(new byte[64]), "vault: key file isn't the raw key length");
            var bad = (byte[])enc.Clone(); bad[30] ^= 1; bool threw = false; try { Vault.Decrypt(bad); } catch { threw = true; }
            Ok(threw, "vault: a changed file is refused (HMAC)");
            Vault.ResetKeyForTests(); Eq("hello secret mail body 123", Encoding.UTF8.GetString(Vault.Decrypt(enc)), "vault: the DPAPI-protected key opens again after a restart");
            Secrets.Reset(); Secrets.Set("pw:t1", "app-pass-SECRET-99"); Secrets.Reset();
            Eq("app-pass-SECRET-99", Secrets.Get("pw:t1"), "secrets: DPAPI round trip");
            Ok(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(MailSettings.Dir, "secrets.dat"))).Contains("SECRET"), "secrets: secrets.dat has no plain text");
            Ok(Crash.Secrets.Contains("app-pass-SECRET-99"), "secrets: crash reports scrub the password");
            Secrets.RemoveAccount("t1"); Ok(!Secrets.Has("pw:t1"), "secrets: removing an account forgets its password");
            var p = new Profile(); p.Fix(); p.Accounts.Add(new Account { Id = "x", Email = "someone@example.test" }); p.Trusted.Add("friend@example.test"); p.Save();
            Ok(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(MailSettings.Dir, "profile.bin"))).Contains("example.test"), "profile: profile.bin is encrypted");
            Eq("someone@example.test", Profile.Load().Accounts[0].Email, "profile: round trip");
            File.Delete(Path.Combine(MailSettings.Dir, "profile.bin"));
        }
        static void CacheTests()
        {
            var dir = Path.Combine(Out, "cache-test"); var s = new MailStore(dir);
            var it = new MailItem { Account = "acc", Folder = "INBOX", UidValidity = 3, Uid = 9, Subject = "Quarterly zebra report", FromAddr = "a@example.test", Text = "the zebra numbers", Date = DateTime.Now.ToString("s") };
            s.Put(it); s.PutBody(it, Encoding.UTF8.GetBytes("Subject: Quarterly zebra report\r\n\r\nzebra body")); s.Save("acc");
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            Ok(files.Length == 2, "cache: index + one message file");
            Ok(files.All(f => !Encoding.UTF8.GetString(File.ReadAllBytes(f)).Contains("zebra")), "cache: nothing readable on disk");
            var s2 = new MailStore(dir);
            Eq(1, s2.Items("acc").Count, "cache: index reloads"); Eq("Quarterly zebra report", s2.Items("acc")[0].Subject, "cache: subject survives");
            Ok(Encoding.UTF8.GetString(s2.Body(s2.Items("acc")[0])).Contains("zebra body"), "cache: message reloads");
            File.WriteAllBytes(Path.Combine(dir, "acc", "index.bin"), new byte[] { 1, 2, 3 });
            Eq(0, new MailStore(dir).Items("acc").Count, "cache: a damaged index starts empty instead of crashing");
            s2.Forget("acc"); Ok(!Directory.Exists(Path.Combine(dir, "acc")), "cache: forgetting an account deletes its cache");
        }
        static void LookTests()
        {
            Eq("dark", Look.ModeOf(null), "look: missing theme.json mode = dark");
            Eq("dark", Look.ModeOf("{\"accent\":\"#112233\"}"), "look: no mode = dark");
            Eq("light", Look.ModeOf("{\"mode\":\"light\"}"), "look: light");
            Ok(Look.Resolve("system", "hallow", () => true), "look: system follows Windows (light)");
            Ok(!Look.Resolve("system", "hallow", () => false), "look: system follows Windows (dark)");
            Ok(Look.Resolve("dark", "light", () => false), "look: the app's own Look wins (light)");
            Ok(!Look.Resolve("light", "dark", () => true), "look: the app's own Look wins (dark)");
            foreach (var accent in new[] { "#F07820", "#8B5CF6", "#22C55E", "#FACC15" })
            {
                var bad = Look.LightPairs(accent).Where(p => Look.Contrast((string)p[1], (string)p[2]) < (double)p[3]).Select(p => (string)p[0]).ToList();
                Ok(bad.Count == 0, "look: light palette contrast with accent " + accent + (bad.Count > 0 ? " - " + string.Join(", ", bad) : ""));
            }
        }

        // ---------------- privacy
        static void PrivacyTests()
        {
            var html = "<html><head><meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\"><base href=\"https://evil.example/\"><link rel=stylesheet href=\"https://cdn.example/x.css\"><script>alert(1)</script></head><body onload=\"steal()\">" +
                "<p style=\"background:url(https://track.example/bg.png);color:red\">Hi there</p>" +
                "<img src=\"https://images.shop.example/hero.jpg\" width=\"600\">" +
                "<img src=\"https://images.shop.example/p.gif\" width=\"1\" height=\"1\">" +
                "<img src=\"https://www.google-analytics.com/collect?v=1&cid=5\">" +
                "<img src=\"https://mail.shop.example/open?id=8f3a9c2b7d4e6f1a0b9c8d7e\">" +
                "<img src=\"cid:logo@x\" onerror=\"x()\">" +
                "<a href=\"javascript:alert(1)\">bad link</a> <a href=\"https://login-bank.example/x\">www.mybank.example</a> <a href=\"https://www.shop.example/sale\">shop.example/sale</a>" +
                "<iframe src=\"https://evil.example/frame\"></iframe><form action=\"https://evil.example/post\"><input name=pw></form><svg><script>bad()</script></svg></body></html>";
            var r = Privacy.Sanitize(html, false, id => id == "logo@x" ? "data:image/png;base64,AAAA" : null);
            var h = r.Html;
            Ok(!h.Contains("<script") && !h.Contains("alert(1)") && !h.Contains("bad()"), "privacy: scripts removed");
            Ok(!h.Contains("onload") && !h.Contains("onerror"), "privacy: event handlers removed");
            Ok(!h.Contains("javascript:"), "privacy: javascript: links removed");
            Ok(!h.Contains("<iframe") && !h.Contains("<form") && !h.Contains("<input") && !h.Contains("<base") && !h.Contains("refresh") && !h.Contains("x.css"), "privacy: frames, forms, base, meta refresh, stylesheets removed");
            Ok(!h.Contains("track.example") && h.Contains("color:red"), "privacy: CSS url() removed, the rest of the style kept");
            Ok(!h.Contains("images.shop.example/hero.jpg"), "privacy: remote images blocked by default");
            Eq(4, r.RemoteImages, "privacy: four remote images seen");
            Eq(3, r.Trackers, "privacy: 1x1 pixel + known tracker domain + unique-ID URL = 3 trackers");
            Ok(h.Contains("data:image/png;base64,AAAA"), "privacy: cid: images shown inline");
            Ok(h.Contains("Content-Security-Policy") && h.Contains("default-src 'none'"), "privacy: CSP on top");
            Ok(h.Contains("title=\"Goes to: login-bank.example\"") && h.Contains("href=\"https://login-bank.example/x\""), "privacy: links show their real destination on hover");
            var mm = r.Mismatches; Eq(1, mm.Count, "privacy: one link text / destination mismatch");
            Ok(mm.Count == 1 && mm[0].TextHost == "www.mybank.example" && mm[0].Host == "login-bank.example", "privacy: the mismatch names both sites");
            Ok(h.Contains("really goes to login-bank.example"), "privacy: a visible warning after the link");
            Ok(!r.Links.Any(l => l.Text == "shop.example/sale" && l.Mismatch), "privacy: www.shop.example vs shop.example isn't a mismatch");
            var r2 = Privacy.Sanitize(html, true, null);
            Ok(r2.Html.Contains("https://images.shop.example/hero.jpg"), "privacy: 'Load images from this sender' shows normal images");
            Ok(!r2.Html.Contains("p.gif") && !r2.Html.Contains("google-analytics") && !r2.Html.Contains("open?id="), "privacy: trackers stay blocked even then");
            Ok(!Privacy.Sanitize("<img src=\"http://plain.example/a.jpg\">", true, null).Html.Contains("http://plain.example"), "privacy: plain-http images never load");
            Ok(Trackers.HasUniqueId("https://x.example/o.gif?u=ab12cd34ef56ab12cd34ef56"), "privacy: unique-ID query detected");
            Ok(!Trackers.HasUniqueId("https://x.example/logo.png?v=2"), "privacy: a short version query isn't an ID");
            Ok(Privacy.TextToHtml("see https://a.example/x <b>", false).Contains("&lt;b&gt;") , "privacy: plain text is escaped");
        }
        static void ShieldListTests()
        {
            var d = Path.Combine(Out, "shield-rules"); Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "easyprivacy.json"), "[{\"id\":1,\"priority\":20,\"action\":{\"type\":\"allow\"},\"condition\":{\"urlFilter\":\"||allowed.example^\"}},{\"id\":2,\"action\":{\"type\":\"block\"},\"condition\":{\"urlFilter\":\"||pixels.fixture-tracker.example^\"}},{\"id\":3,\"action\":{\"type\":\"block\"},\"condition\":{\"requestDomains\":[\"metrics.fixture.example\",\"beacon.fixture.example\"]}}]");
            File.WriteAllText(Path.Combine(d, "ublock-badware.json"), "[{\"id\":1,\"action\":{\"type\":\"block\"},\"condition\":{\"requestDomains\":[\"scam-shop.fixture.example\"]}}]");
            Trackers.ShieldRulesDir = d; Trackers.Reset();
            Ok(Trackers.IsTracker("pixels.fixture-tracker.example") && Trackers.IsTracker("eu.metrics.fixture.example"), "shield lists: block rules (urlFilter + requestDomains, subdomains) are trackers");
            Ok(!Trackers.IsTracker("allowed.example"), "shield lists: allow rules are not");
            Ok(Trackers.Source.Contains("Shield"), "shield lists: the source says Hallow Shield");
            Ok(Trackers.IsBadware("www.scam-shop.fixture.example") && !Trackers.IsBadware("shop.example"), "shield lists: badware hosts");
            var r = Privacy.Sanitize("<img src=\"https://beacon.fixture.example/i.png\" width=300 height=200>", true, null); Eq(1, r.Trackers, "shield lists: used for images");
            Trackers.ShieldRulesDir = Path.Combine(Out, "no-lists"); Trackers.Reset();
        }

        // ---------------- safety
        static void PhishTests()
        {
            var auth = "mx.example.test; spf=pass smtp.mailfrom=paypal.com; dkim=pass header.d=paypal.com; dmarc=pass";
            var fake = Phish.Check(Item("PayPal", "service@paypa1-secure.example", "Account"), false);
            Ok(fake.Any(h => h.Level == "red" && h.Text.Contains("PayPal")), "phish: 'PayPal' from a random domain");
            Ok(fake.Any(h => h.Text.Contains("looks like paypal.com")), "phish: paypa1 is a look-alike of paypal.com");
            var real = Phish.Check(Item("PayPal", "service@paypal.com", "Receipt", "", i => i.AuthResults = auth), false);
            Ok(!real.Any(h => h.Level == "red") && real.Any(h => h.Level == "green"), "phish: real PayPal with DMARC pass is fine");
            Ok(Phish.Check(Item("PayPal Service", "service@intl.paypal.com", "x"), false).All(h => h.Level != "red"), "phish: a brand subdomain is fine");
            Ok(Phish.Check(Item("support@bank.example", "x@other.example", "x"), false).Any(h => h.Text.Contains("The name shows support@bank.example")), "phish: an address in the name that isn't the sender");
            Ok(Phish.Lookalike("xn--pypal-4ve.com") != null, "phish: punycode domain");
            Ok(Phish.Lookalike("rnicrosoft.com") != null && Phish.Lookalike("arnazon.com") != null && Phish.Lookalike("paypa1.com") != null, "phish: character swaps (rn/m, 1/l)");
            Ok(Phish.Lookalike("amazno.com") != null, "phish: transposed letters");
            Ok(Phish.Lookalike("paypal-secure-login.com") != null, "phish: brand + 'secure' word");
            Ok(Phish.Lookalike("example.org") == null && Phish.Lookalike("amazon.co.uk") == null && Phish.Lookalike("mail.google.com") == null, "phish: normal domains aren't flagged");
            var a = Phish.Auth("mx.example.test; spf=softfail smtp.mailfrom=x; dkim=fail; dmarc=fail (p=reject)");
            Ok(a["spf"] == "softfail" && a["dkim"] == "fail" && a["dmarc"] == "fail", "phish: Authentication-Results parsed");
            Ok(Phish.Check(Item("Shop", "a@shop.example", "x", "", i => i.AuthResults = "mx; spf=fail; dmarc=fail"), false).Any(h => h.Level == "red" && h.Text.Contains("DMARC fail")), "phish: DMARC fail is red");
            Ok(Phish.Check(Item("Shop", "a@shop.example", "x", "", i => i.ReplyTo = "x@elsewhere.example"), false).Any(h => h.Text.Contains("Replies go to elsewhere.example")), "phish: Reply-To to another domain");
            Ok(Phish.Check(Item("New", "n@new.example", "x"), true).Any(h => h.Text.StartsWith("First mail")), "phish: first-time sender note");
        }
        static byte[] Zip(params string[] names)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true)) foreach (var n in names) { var e = z.CreateEntry(n); using (var s = e.Open()) s.WriteByte(1); }
                return ms.ToArray();
            }
        }
        static void AttachmentTests()
        {
            Ok(Attachments.Inspect("setup.exe", new byte[3]).Dangerous, "attach: .exe flagged");
            Ok(Attachments.Inspect("report.docm", new byte[3]).Dangerous, "attach: macro document flagged");
            var d = Attachments.Inspect("invoice.pdf.exe", new byte[3]); Ok(d.Flags.Any(f => f.Text.StartsWith("Double extension")), "attach: double extension");
            Ok(Attachments.Inspect("photos.zip", Zip("a.jpg", "b.jpg.scr")).Flags.Any(f => f.Text.Contains("b.jpg.scr")), "attach: archive containing a program");
            Ok(!Attachments.Inspect("photos.zip", Zip("a.jpg", "b.png")).Dangerous, "attach: archive of pictures is fine");
            Ok(Attachments.Inspect("doc‮fdp.exe", new byte[3]).Flags.Any(f => f.Text.Contains("right-to-left")), "attach: right-to-left override");
            Ok(Attachments.Inspect("disk.iso", new byte[3]).Dangerous, "attach: disk image");
            Ok(Attachments.Inspect("statement.pdf", new byte[3]).Flags.Count == 0, "attach: a PDF has no flags");
            Ok(Attachments.Inspect("old.doc", new byte[3]).Flags.Any(f => f.Level == "amber"), "attach: old Office format is amber");
        }
        static void DefenderTests()
        {
            var stand = Path.Combine(Path.GetDirectoryName(typeof(Run).Assembly.Location), "FakeMpCmdRun.exe");
            Defender.Override = stand;
            var f1 = Path.Combine(Out, "clean.txt"); File.WriteAllText(f1, "hello");
            var f2 = Path.Combine(Out, "bad.txt"); File.WriteAllText(f2, "xx HALLOW-TEST-THREAT xx");
            bool clean; var m1 = Defender.Scan(f1, out clean); Ok(clean && m1.Contains("found nothing"), "defender: clean file opens (stand-in MpCmdRun)");
            var prio = Path.Combine(Path.GetDirectoryName(stand), "priority.txt"); Ok(File.Exists(prio) && File.ReadAllText(prio) == "Idle", "defender: the scan runs at Idle priority" + (File.Exists(prio) ? " (" + File.ReadAllText(prio) + ")" : ""));
            var m2 = Defender.Scan(f2, out clean); Ok(!clean && m2.Contains("Hallow_Test_Standin"), "defender: a threat is named and the file isn't opened");
            bool c2; Defender.Verdict(5, "", out c2); Ok(!c2, "defender: an unknown exit code never counts as clean");
            Defender.Override = Path.Combine(Out, "missing.exe"); var m3 = Defender.Scan(f1, out clean); Ok(!clean, "defender: no scanner = not opened");
            Defender.Override = null;
        }

        // ---------------- sorting
        static void SortTests()
        {
            var p = new Profile(); p.Fix();
            Eq("newsletters", Sorter.Categorize(Item("Weekly", "news@letters.example", "This week", "", i => i.ListUnsubscribe = "<https://x.example/u>"), p), "sort: List-Unsubscribe = newsletter");
            Eq("newsletters", Sorter.Categorize(Item("Club", "club@list.example", "Meeting", "", i => i.Precedence = "bulk"), p), "sort: Precedence bulk = newsletter");
            Eq("receipts", Sorter.Categorize(Item("Shop", "orders@shop.example", "Your order 10443 has shipped", "", i => i.ListUnsubscribe = "<mailto:x@y.example>"), p), "sort: order shipped = receipt (even with List-Unsubscribe)");
            Eq("receipts", Sorter.Categorize(Item("Utility", "bills@power.example", "Your bill is ready - invoice 2211"), p), "sort: invoice = receipt");
            Eq("notifications", Sorter.Categorize(Item("Accounts", "no-reply@accounts.example", "New sign-in to your account"), p), "sort: no-reply security alert = notification");
            Eq("notifications", Sorter.Categorize(Item("Hub", "bot@code.example", "Build failed", "", i => i.AutoSubmitted = "auto-generated"), p), "sort: Auto-Submitted = notification");
            Eq("people", Sorter.Categorize(Item("Sam", "sam@example.org", "Dinner on Friday?"), p), "sort: a person = People");
            p.SenderCategory["sam@example.org"] = "notifications";
            Eq("notifications", Sorter.Categorize(Item("Sam", "sam@example.org", "Dinner"), p), "sort: the user moved this sender");
            p.SenderCategory["@letters.example"] = "people";
            Eq("people", Sorter.Categorize(Item("Weekly", "news@letters.example", "x", "", i => i.ListUnsubscribe = "<https://x.example/u>"), p), "sort: the user moved the whole domain");
        }
        static void ThreadSearchTests()
        {
            var a = Item("Sam", "sam@example.org", "Saturday hike", "", i => { i.MessageId = "m1@x"; i.Date = DateTime.Now.AddHours(-5).ToString("s"); });
            var b = Item("Alex", "alex@example.test", "Re: Saturday hike", "", i => { i.MessageId = "m2@x"; i.InReplyTo = "m1@x"; i.Date = DateTime.Now.AddHours(-4).ToString("s"); });
            var c = Item("Sam", "sam@example.org", "RE: Re: saturday hike", "", i => { i.MessageId = "m3@x"; i.Date = DateTime.Now.AddHours(-3).ToString("s"); });
            var d = Item("Sam", "sam@example.org", "Saturday hike", "", i => { i.MessageId = "m4@x"; i.Date = DateTime.Now.AddHours(-2).ToString("s"); });
            var e = Item("Pat", "pat@example.org", "Lunch", "", i => { i.MessageId = "m5@x"; i.References = new List<string> { "m1@x" }; });
            var l = new List<MailItem> { a, b, c, d, e }; Threads.Assign(l);
            Ok(a.Thread == b.Thread, "threads: In-Reply-To joins"); Ok(c.Thread == a.Thread, "threads: 'RE: Re:' with the same subject joins");
            Ok(d.Thread != a.Thread, "threads: a new mail with the same subject (no Re:) starts its own"); Ok(e.Thread == a.Thread, "threads: References join");
            Eq("saturday hike", Threads.Norm("Fwd: RE: Saturday   Hike"), "threads: subject normalised");
            var s = new List<MailItem> { Item("Sam", "sam@example.org", "Hike plans", "bring the map"), Item("Bank", "no-reply@bank.example", "Statement", "your statement", i => { i.HasAttachments = true; i.Seen = true; }), Item("Anna", "anna@example.net", "Invoice 42", "pay by friday", i => i.Flagged = true) };
            Eq(1, MailStore.Search(s, "map", null).Count, "search: body text"); Eq(1, MailStore.Search(s, "from:anna", null).Count, "search: from:");
            Eq(1, MailStore.Search(s, "has:attachment", null).Count, "search: has:attachment"); Eq(2, MailStore.Search(s, "is:unread", null).Count, "search: is:unread");
            Eq(1, MailStore.Search(s, "is:starred invoice", null).Count, "search: two terms"); Eq(0, MailStore.Search(s, "subject:map", null).Count, "search: subject: only looks at the subject");
            Eq(1, MailStore.Search(s, "\"bring the map\"", null).Count, "search: quoted phrase");
        }

        // ---------------- the spam filter
        static readonly string[][] TestSpam = {
            new[] { "Claim your FREE prize now!!", "You have been selected as a winner. Claim your prize today, limited time offer. Click the link to claim.", "win@prize-hub.test" },
            new[] { "Verify your account immediately", "Your account will be suspended. Verify your account and confirm your identity within 24 hours using the secure link.", "alert@account-verify.test" },
            new[] { "Make $3000 weekly from home", "Guaranteed income working from home, no experience. Earn money fast with our bitcoin investment system.", "cash@easy-money.test" },
            new[] { "Unclaimed inheritance funds", "Dear beneficiary, unclaimed funds of 7 million dollars are waiting. Send your bank details for the wire transfer.", "lawyer@estate-claims.test" },
            new[] { "Cheap pills, no prescription", "Cheap meds and viagra, discreet delivery, risk free, order now.", "rx@pill-store.test" },
            new[] { "Your parcel is on hold", "Pay the small redelivery fee now or your package will be returned. Act now.", "delivery@parcel-fee.test" },
            new[] { "Exclusive casino bonus inside", "Free spins and a casino bonus waiting for you. Deposit today and double your money.", "vip@lucky-casino.test" },
            new[] { "Congratulations, you've won an iPhone", "Congratulations you are our winner, pay only shipping to claim your prize iPhone.", "promo@phone-giveaway.test" },
            new[] { "Lose 20 pounds in 10 days", "Miracle diet pill, lose weight fast, doctors are shocked, limited time offer.", "slim@diet-miracle.test" },
            new[] { "Crypto opportunity of a lifetime", "Join our crypto opportunity, guaranteed returns, risk free, act now before it ends.", "invest@moon-coin.test" },
            new[] { "Gift card waiting for you", "Claim the gift card reward now, complete the survey, expires today.", "reward@survey-gifts.test" },
            new[] { "Final notice: account locked", "Unusual activity detected. Your account is locked, update your payment details immediately.", "billing@secure-billing.test" },
        };
        static readonly string[][] TestHam = {
            new[] { "Are you coming on Sunday?", "We're having a barbecue on Sunday afternoon, bring whoever you like. Let me know so I get enough food.", "riley@example.org" },
            new[] { "Re: the budget spreadsheet", "I updated the travel line and added the hotel costs. Can you check the totals before the meeting?", "casey@example.com" },
            new[] { "Your order has been delivered", "Your parcel was delivered today at 14:02 and left with a neighbour. Thanks for shopping with us.", "orders@shop.example.com" },
            new[] { "Receipt for your donation", "Thank you for your donation of 25.00. This receipt can be used for tax purposes.", "receipts@charity.example.org" },
            new[] { "This week in the garden club", "Planting tips for winter, the seed swap on Saturday, and photos from members. Unsubscribe at any time.", "club@garden.example.org" },
            new[] { "Limited time offer: 20% off our winter range", "Our winter sale starts today with 20% off coats and boots for newsletter subscribers. Free returns as always.", "news@outdoor-shop.example.com" },
            new[] { "Your security code", "Use code 551203 to finish signing in. It expires in 10 minutes. Didn't ask for it? Change your password.", "no-reply@accounts.example.com" },
            new[] { "Minutes from the residents meeting", "Attached are the minutes. Next meeting is on the 3rd. Please send agenda items by Friday.", "secretary@residents.example.org" },
            new[] { "Re: borrowing the drill", "Sure, come by after six and grab it from the shed. Bring it back whenever.", "neighbour@example.net" },
            new[] { "Interview confirmation", "Thanks for applying. Your interview is confirmed for Tuesday at 10:00. Bring a photo ID.", "hr@company.example.com" },
            new[] { "Happy new year from all of us", "Wishing you a happy new year! The office reopens on the 3rd of January.", "team@company.example.com" },
            new[] { "Urgent: server disk almost full", "Disk usage on build-01 is at 95%. Please clean up old artifacts today.", "alerts@builds.example.com" },
            new[] { "Re: urgent - can you call me?", "Sorry for the rush, can you call me when you get this? It's about the car.", "dad@example.net" },
            new[] { "You won the office raffle!", "Congratulations, you won the hamper in the office raffle. Pick it up from reception.", "events@company.example.com" },
        };
        static void SpamTests()
        {
            var b = Spam.Seeded();
            Ok(b.NS == Seed.SpamMail.Length && b.NH == Seed.HamMail.Length, "spam: seeded from the built-in made-up corpus");
            Func<string[], bool, MailItem> mk = (x, spam) => Item(x[2].Split('@')[0], x[2], x[0], x[1], i => { if (!spam) i.AuthResults = "mx.example.test; spf=pass; dkim=pass; dmarc=pass"; });
            Func<MailItem, bool, bool, SpamVerdict> judge = (m, first, trusted) => Spam.Judge(b, new Spam.Input { Mail = m, FirstTime = first, Trusted = trusted }, "normal");
            // ham: never flagged - also with spammy words, as a first-time sender, and on High sensitivity
            var hamFlagged = TestHam.Where(h => judge(mk(h, false), true, false).IsSpam).Select(h => h[0]).ToList();
            Ok(hamFlagged.Count == 0, "spam: no fixture ham flagged (" + TestHam.Length + " messages)" + (hamFlagged.Count > 0 ? ": " + string.Join(" | ", hamFlagged) : ""));
            var hamHigh = TestHam.Where(h => Spam.Judge(b, new Spam.Input { Mail = mk(h, false), FirstTime = false }, "high").IsSpam).ToList();
            Ok(hamHigh.Count == 0, "spam: no ham flagged on High sensitivity either");
            var caught = TestSpam.Count(s => judge(mk(s, true), true, false).IsSpam);
            Ok(caught >= TestSpam.Length * 0.6, "spam: caught " + caught + "/" + TestSpam.Length + " fixture spam (want >= 60%)");
            // trusted senders are never spam, whatever they write
            foreach (var s in TestSpam) { var m = mk(s, true); m.FromAddr = "friend@example.org"; Ok(!judge(m, false, true).IsSpam && judge(m, false, true).Score == 0, "spam: trusted sender never flagged: " + s[0]); }
            var ns = mk(TestSpam[0], true); ns.NotSpam = true; Ok(!judge(ns, true, false).IsSpam, "spam: 'Not spam' is respected");
            // reasons
            var v = judge(Item("PayPal", "service@paypa1-billing.test", "Your account will be suspended", "Verify your account within 24 hours", i => i.AuthResults = "mx; spf=fail; dmarc=fail"), true, false);
            Ok(v.IsSpam && v.Why.Any(w => w.Contains("DMARC")) && v.Why.Any(w => w.Contains("PayPal")), "spam: a faked PayPal is spam, with reasons");
            var hdr = judge(Item("X", "x@x.test", "Hello", "hi", i => i.SpamHeaders = "X-Spam-Flag: YES\nX-Spam-Status: Yes, score=9"), false, false);
            Ok(hdr.Why.Any(w => w.Contains("provider's filter")), "spam: provider X-Spam headers count");
            var shortv = Spam.Judge(b, new Spam.Input { Mail = Item("Y", "y@y.test", "Look", "click"), LinkHosts = new List<string> { "bit.ly" } }, "normal");
            Ok(shortv.Why.Any(w => w.Contains("shortener")), "spam: URL shorteners noticed");
            // learning: train on ham that looks like spam, it stops looking like spam
            var tricky = Item("Club", "club@example.org", "Win free spins at our casino night!", "Our charity casino night: free spins, cash prize raffle, all proceeds to the school.");
            var before = Spam.Score(b, Spam.Tokens(tricky.Subject, tricky.Text, tricky.FromAddr, null, null));
            for (int i = 0; i < 3; i++) Spam.Train(b, Spam.Tokens(tricky.Subject, tricky.Text, tricky.FromAddr, null, null), false);
            var after = Spam.Score(b, Spam.Tokens(tricky.Subject, tricky.Text, tricky.FromAddr, null, null));
            Ok(after < before, "spam: 'Not spam' training lowers the score (" + before.ToString("0.00") + " -> " + after.ToString("0.00") + ")");
            Ok(Spam.Threshold("low") > Spam.Threshold("normal") && Spam.Threshold("normal") > Spam.Threshold("high"), "spam: sensitivity Low / Normal / High");
        }

        // ---------------- rules, suggestions, the Screener
        static void RuleTests()
        {
            var r = new Rule { From = "orders@", MoveTo = "Receipts", On = true };
            Ok(Rules.Matches(r, Item("Shop", "orders@shop.example", "x")), "rules: from contains");
            Ok(!Rules.Matches(r, Item("Shop", "news@shop.example", "x")), "rules: no match");
            Ok(!Rules.Matches(new Rule { MoveTo = "X", On = true }, Item("a", "a@b.example", "x")), "rules: a rule without conditions never matches");
            Ok(Rules.Matches(new Rule { Subject = "[ci]", HasAttachment = false, On = true }, Item("CI", "ci@b.example", "[ci] build ok")), "rules: subject + attachment");
            Ok(!Rules.Matches(new Rule { From = "orders@", On = false }, Item("Shop", "orders@shop.example", "x")), "rules: off = no match");
            Ok(!Rules.Matches(new Rule { From = "orders@", Account = "other", On = true }, Item("Shop", "orders@shop.example", "x")), "rules: limited to one account");
            Ok(Rules.Describe(new Rule { From = "orders@", MoveTo = "Receipts", MarkRead = true }).Contains("move to Receipts"), "rules: describe");
        }
        static void SuggestionTests()
        {
            var p = new Profile(); p.Fix();
            for (int i = 0; i < 4; i++) Suggestions.NoteMove(p, "News@Games.example", "Games");
            Suggestions.NoteMove(p, "once@x.example", "Other");
            var s = Suggestions.Make(p, new MailItem[0]);
            Ok(s.Any(x => x.Kind == "move" && x.Sender == "news@games.example" && x.Folder == "Games"), "suggest: 'you always move X to Y'");
            Ok(!s.Any(x => x.Sender == "once@x.example"), "suggest: one move isn't a habit");
            for (int i = 0; i < 6; i++) Suggestions.NoteReceived(p, Item("Digest", "digest@city.example", "Digest", "", m => m.Category = "newsletters"));
            Ok(Suggestions.Make(p, new MailItem[0]).Any(x => x.Kind == "never" && x.Sender == "digest@city.example"), "suggest: 'you never open these newsletters'");
            Suggestions.NoteOpened(p, Item("Digest", "digest@city.example", "Digest", "", m => m.Category = "newsletters"));
            Ok(!Suggestions.Make(p, new MailItem[0]).Any(x => x.Kind == "never"), "suggest: opened once = no nagging");
            p.Dismissed.Add("move:news@games.example>Games"); Ok(!Suggestions.Make(p, new MailItem[0]).Any(x => x.Kind == "move"), "suggest: 'Not now' hides it");
            p.Dismissed.Clear(); p.Rules.Add(new Rule { From = "news@games.example", MoveTo = "Games" }); Ok(!Suggestions.Make(p, new MailItem[0]).Any(x => x.Kind == "move"), "suggest: not again once the rule exists");
        }
        static void ScreenerTests()
        {
            var p = new Profile(); p.Fix(); p.Known.Add("old@friend.example"); p.Trusted.Add("pal@example.org");
            Ok(!Screener.Waits(Item("New", "new@x.example", "Hi"), p, false), "screener: off = nobody waits");
            Ok(Screener.Waits(Item("New", "new@x.example", "Hi"), p, true), "screener: a first-time sender waits");
            Ok(!Screener.Waits(Item("Pal", "pal@example.org", "Hi"), p, true), "screener: people you've written to never wait");
            Screener.TurnOn(p); Ok(!Screener.Waits(Item("Old", "old@friend.example", "Hi"), p, true), "screener: turning it on lets earlier senders through");
            p.ScreenerOk.Add("new@x.example"); Ok(!Screener.Waits(Item("New", "new@x.example", "Hi"), p, true), "screener: approved senders go to the inbox");
        }

        // ---------------- unsubscribe, Cloudflare, OAuth (stand-in endpoints)
        static void UnsubTests()
        {
            var one = Unsubscribe.Plan("<mailto:u@list.example?subject=bye>, <https://list.example/u/123>", "List-Unsubscribe=One-Click");
            Eq("oneclick", one.Kind, "unsub: RFC 8058 one-click preferred"); Eq("https://list.example/u/123", one.Url, "unsub: one-click URL");
            var mt = Unsubscribe.Plan("<mailto:u@list.example?subject=bye%20now&body=please>, <https://list.example/u/123>", null);
            Ok(mt.Kind == "mailto" && mt.MailTo == "u@list.example" && mt.MailSubject == "bye now" && mt.MailBody == "please", "unsub: mailto with subject/body (no one-click header)");
            Eq("web", Unsubscribe.Plan("<https://list.example/u/1>", null).Kind, "unsub: web page only"); Eq("none", Unsubscribe.Plan(null, null).Kind, "unsub: nothing offered");
            Eq("oneclick", Unsubscribe.Plan("<http://list.example/u>, <https://list.example/u>", "List-Unsubscribe=One-Click").Kind, "unsub: one-click only over https");
            var http = new FakeHttp(); http.Answer = (m, p, a, b) => Tuple.Create(p.StartsWith("/fail") ? 500 : 200, "{}");
            try
            {
                Ok(Unsubscribe.OneClick(http.Url + "u/abc?x=1") == null, "unsub: one-click POST answered");
                var r = http.Requests.Last(); Ok(r[0] == "POST" && r[1] == "/u/abc?x=1" && r[3] == "List-Unsubscribe=One-Click", "unsub: POST with List-Unsubscribe=One-Click body");
                Ok(Unsubscribe.OneClick(http.Url + "fail") != null, "unsub: a failure is reported");
            }
            finally { http.Stop(); }
            var items = new List<MailItem>();
            for (int i = 0; i < 5; i++) items.Add(Item("Weekly", "w@news.example", "Issue " + i, "", m => { m.ListUnsubscribe = "<https://news.example/u>"; m.ListUnsubscribePost = "List-Unsubscribe=One-Click"; m.Seen = i == 0; }));
            items.Add(Item("Daily", "d@daily.example", "Today", "", m => m.ListUnsubscribe = "<mailto:off@daily.example>"));
            items.Add(Item("Sam", "sam@example.org", "Hi"));
            var f = Unsubscribe.Finder(items);
            Ok(f.Count == 2 && f[0].Sender == "w@news.example" && f[0].Count == 5 && f[0].Opened == 1 && f[0].Kind == "oneclick" && f[1].Kind == "mailto", "unsub: finder lists newsletters by volume");
        }
        static void CloudflareTests()
        {
            var http = new FakeHttp();
            http.Answer = (m, p, a, b) =>
            {
                if (a != "Bearer cf-test-token") return Tuple.Create(403, "{\"success\":false,\"errors\":[{\"code\":10000,\"message\":\"Authentication error\"}]}");
                if (m == "GET" && p.StartsWith("/zones?name=mydomain.example")) return Tuple.Create(200, "{\"success\":true,\"result\":[{\"id\":\"zone123\",\"name\":\"mydomain.example\"}]}");
                if (m == "GET" && p.StartsWith("/zones?name=")) return Tuple.Create(200, "{\"success\":true,\"result\":[]}");
                if (m == "POST" && p == "/zones/zone123/email/routing/rules") return Tuple.Create(200, "{\"success\":true,\"result\":{\"id\":\"rule789\",\"enabled\":true}}");
                if (m == "DELETE" && p == "/zones/zone123/email/routing/rules/rule789") return Tuple.Create(200, "{\"success\":true,\"result\":{\"id\":\"rule789\"}}");
                return Tuple.Create(404, "{\"success\":false,\"errors\":[{\"message\":\"not found\"}]}");
            };
            Cloudflare.ApiOverride = http.Url.TrimEnd('/');
            try
            {
                string err; Eq("zone123", Cloudflare.ZoneId("cf-test-token", "mydomain.example", out err), "cloudflare: zone id (stand-in API)");
                Ok(Cloudflare.ZoneId("wrong", "mydomain.example", out err) == null && err.Contains("Authentication error"), "cloudflare: a bad token says why");
                Ok(Cloudflare.ZoneId("cf-test-token", "other.example", out err) == null && err.Contains("doesn't have a zone"), "cloudflare: unknown domain");
                var alias = Cloudflare.NewAlias("mydomain.example", "Shop & Co!");
                Ok(System.Text.RegularExpressions.Regex.IsMatch(alias, @"^shopco\.[0-9a-f]{8}@mydomain\.example$"), "cloudflare: masked address shape " + alias);
                // through the app's own code path, with the token from secrets.dat
                var p = new Profile(); p.Fix(); p.CfZone = "mydomain.example"; p.CfZoneId = "zone123"; p.CfForwardTo = "me@example.test";
                Secrets.Set("cf", null); string said = null;
                Ok(PageBuilder.CreateMaskedFor(p, "shop", s => said = s) != null && said.Contains("Set up Cloudflare"), "cloudflare: off until the token is set");
                Secrets.Set("cf", "cf-test-token");
                Ok(PageBuilder.CreateMaskedFor(p, "shop", s => said = s) == null && p.MaskedAddresses.Count == 1 && p.MaskedAddresses[0].RuleId == "rule789", "cloudflare: masked address created");
                var post = http.Requests.Last(r => r[0] == "POST");
                Ok(post[3].Contains("\"type\":\"literal\"") && post[3].Contains("\"field\":\"to\"") && post[3].Contains(p.MaskedAddresses[0].Address) && post[3].Contains("\"type\":\"forward\"") && post[3].Contains("me@example.test"), "cloudflare: the routing rule matches the alias and forwards to the inbox");
                Ok(Cloudflare.DeleteRule("cf-test-token", "zone123", "rule789") == null, "cloudflare: turning an address off deletes its rule");
                Ok(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(MailSettings.Dir, "secrets.dat"))).Contains("cf-test-token"), "cloudflare: the token is stored encrypted");
                Secrets.Set("cf", null);
            }
            finally { Cloudflare.ApiOverride = null; http.Stop(); }
        }
        static void OAuthTests()
        {
            var v = OAuth.Verifier(); Ok(v.Length >= 43 && !v.Contains("=") && !v.Contains("+"), "oauth: PKCE verifier is base64url");
            using (var sha = System.Security.Cryptography.SHA256.Create())
                Eq(Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(v))).TrimEnd('=').Replace('+', '-').Replace('/', '_'), OAuth.Challenge(v), "oauth: S256 challenge");
            var url = OAuth.AuthorizeUrl(OAuth.Google, "my-client.apps.example", "http://127.0.0.1:5555/", "st", "ch", "me@example.test");
            Ok(url.StartsWith("https://accounts.google.com/") && url.Contains("client_id=my-client.apps.example") && url.Contains("code_challenge_method=S256") && url.Contains("scope=https%3A%2F%2Fmail.google.com%2F") && url.Contains("access_type=offline"), "oauth: Google authorize URL");
            Ok(OAuth.AuthorizeUrl(OAuth.Microsoft, "c", "http://127.0.0.1:1/", "s", "c", null).Contains("offline_access"), "oauth: Microsoft asks for offline_access");
            var http = new FakeHttp();
            http.Answer = (m, p, a, b) =>
            {
                if (b.Contains("grant_type=refresh_token") && b.Contains("refresh_token=rt-good")) return Tuple.Create(200, "{\"access_token\":\"at-fresh-1\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");
                if (b.Contains("grant_type=authorization_code") && b.Contains("code=c0de") && b.Contains("code_verifier=")) return Tuple.Create(200, "{\"access_token\":\"at-2\",\"refresh_token\":\"rt-new\",\"expires_in\":3600}");
                return Tuple.Create(400, "{\"error\":\"invalid_grant\",\"error_description\":\"Token has been expired or revoked.\"}");
            };
            OAuth.TokenOverride = http.Url + "token";
            try
            {
                var t = OAuth.Refresh(OAuth.Google, "cid", "csecret", "rt-good"); Ok(t.Access == "at-fresh-1" && t.Refresh == "rt-good", "oauth: refresh (stand-in token endpoint)");
                Ok(http.Requests.Last()[3].Contains("client_secret=csecret") && http.Requests.Last()[3].Contains("client_id=cid"), "oauth: client id / secret sent to the token endpoint");
                var bad = OAuth.Refresh(OAuth.Google, "cid", null, "rt-bad"); Ok(bad.Access == null && bad.Error.Contains("expired or revoked"), "oauth: a revoked token says why");
                var ex = OAuth.Exchange(OAuth.Microsoft, "cid", null, "c0de", "verif", "http://127.0.0.1:1/"); Ok(ex.Refresh == "rt-new", "oauth: code exchange with PKCE verifier");
            }
            finally { OAuth.TokenOverride = null; http.Stop(); }
        }
        static void NoteTests()
        {
            var s = new MailSettings(); string why;
            var people = new List<MailItem> { Item("Sam", "sam@example.org", "Dinner?", "", m => m.Category = "people") };
            Ok(Controller.NotePlan(people, s, () => false, out why) != null, "notify: new mail from People");
            Ok(Controller.NotePlan(people, s, () => true, out why) == null && why.Contains("game"), "notify: held while a game runs");
            s.NotifyDuringGames = true; Ok(Controller.NotePlan(people, s, () => true, out why) != null, "notify: unless switched on for games");
            s.NotifyPeople = false; Ok(Controller.NotePlan(people, s, () => false, out why) == null, "notify: switched off");
            s.NotifyPeople = true; Ok(Controller.NotePlan(new List<MailItem> { Item("X", "x@y.example", "s", "", m => m.Screened = true) }, s, () => false, out why) == null, "notify: never for the Screener");
        }

        // ---------------- the mail engine against the local IMAP / SMTP stand-in
        static void ImapTests()
        {
            var imap = new FakeImap(); var smtp = new FakeSmtp();
            var dir = Path.Combine(Out, "imap"); Directory.CreateDirectory(dir);
            var me = "alex@example.test";
            imap.Add("INBOX", Mail("Sam Lee", "sam@example.org", me, "Saturday hike", "Bring the zebra map please.", null, null, "hike1@example.org"), "\\Seen");
            imap.Add("INBOX", Mail("Sam Lee", "sam@example.org", me, "Re: Saturday hike", "And snacks.", m => m.InReplyTo = "hike1@example.org"));
            imap.Add("INBOX", Mail("Weekly", "news@weekly.example", me, "This week", "News", m => { m.Headers.Add("List-Unsubscribe", "<https://weekly.example/u>"); }, "<p>News <img src=\"https://t.weekly.example/p.gif\" width=1 height=1></p>"));
            imap.Add("Sent", Mail("Alex", me, "pat@example.org", "Hello Pat", "Hi"), "\\Seen");
            var p = new Profile(); p.Fix(); var S = new MailSettings();
            var acc = new Account { Id = "t1", Email = me, Name = "Alex", Label = "Test", Preset = "custom", ImapHost = "127.0.0.1", ImapPort = imap.Port, ImapSecurity = "none", SmtpHost = "127.0.0.1", SmtpPort = smtp.Port, SmtpSecurity = "none", SendAs = new List<string>() };
            p.Accounts.Add(acc); Secrets.Set("pw:t1", imap.Pass);
            var store = new MailStore(Path.Combine(dir, "cache"));
            var E = new Engine(p, S, store); var newMail = new List<MailItem>(); E.NewMail += l => { lock (newMail) newMail.AddRange(l); };
            try
            {
                E.Start(); var w = E.W("t1");
                Ok(Until(() => w.State == "idle", 15000), "imap: signed in, synced and idling (state " + w.State + ")");
                Ok(!imap.Log.Any(x => x.Contains(imap.Pass)), "imap: the password never shows in the protocol log copy");
                var items = store.Items("t1");
                Eq(4, items.Count, "imap: 4 messages cached (inbox + sent)");
                var idx = store.Index("t1");
                Ok(idx.Folders.Any(f => f.Role == "sent" && f.Name == "Sent") && idx.Folders.Any(f => f.Role == "junk") && idx.Folders.Any(f => f.Role == "drafts") && idx.Folders.Any(f => f.Role == "archive") && idx.Folders.Any(f => f.Role == "trash"), "imap: special folders found (SPECIAL-USE)");
                var hike = items.Where(i => i.Subject.Contains("hike")).ToList();
                Ok(hike.Count == 2 && hike[0].Thread == hike[1].Thread, "imap: the reply is in the same conversation" + string.Concat(hike.Select(x => " [" + x.Subject + " id=" + x.MessageId + " irt=" + x.InReplyTo + " t=" + x.Thread + " d=" + x.Date + "]")));
                Ok(items.First(i => i.Subject == "This week").Category == "newsletters", "imap: newsletter sorted");
                Eq(1, MailStore.Search(store.All(), "zebra", p).Count, "imap: search finds body text (pre-fetched bodies)");
                Ok(items.All(i => i.BodyCached), "imap: bodies cached");
                Ok(p.Trusted.Contains("pat@example.org"), "imap: people in Sent are trusted");
                Ok(newMail.Count == 0, "imap: the first sync doesn't notify");
                // IDLE push
                int idles = imap.Idles;
                imap.Add("INBOX", Mail("Riley", "riley@example.org", me, "Coffee tomorrow?", "Want to grab a coffee?"));
                Ok(Until(() => store.Items("t1").Any(i => i.Subject == "Coffee tomorrow?"), 8000), "imap: IDLE pushed the new mail");
                Ok(Until(() => { lock (newMail) return newMail.Any(i => i.Subject == "Coffee tomorrow?" && i.Category == "people"); }, 3000), "imap: new mail from People is announced");
                Ok(Until(() => w.State == "idle", 5000), "imap: back to idling");
                // the spam filter moves spam to Junk on the server, with a reason
                imap.Add("INBOX", Mail("PayPal", "service@paypa1-secure.test", me, "URGENT: your account will be suspended", "Verify your account within 24 hours or it will be locked. Claim your refund, act now.", m => m.Headers.Add("Authentication-Results", "mx.example.test; spf=fail smtp.mailfrom=paypa1-secure.test; dmarc=fail")));
                Ok(Until(() => imap.Count("Junk") == 1, 8000), "imap: spam moved to the server's Junk folder");
                Ok(Until(() => store.Items("t1").Any(i => i.Folder == "Junk" && i.Subject.StartsWith("URGENT")), 8000), "imap: and synced back into the cache");
                Ok(!newMail.Any(i => i.Subject.StartsWith("URGENT")), "imap: spam isn't announced");
                // rules: move + label (IMAP keyword) and skip-inbox
                p.Rules.Add(new Rule { Id = "r1", Name = "receipts", From = "orders@", MoveTo = "Receipts", Label = "receipts", On = true });
                p.Rules.Add(new Rule { Id = "r2", Name = "ci", Subject = "[ci]", SkipInbox = true, MarkRead = true, On = true });
                imap.Add("INBOX", Mail("Shop", "orders@shop.example", me, "Your order 777 has shipped", "Arrives Monday."));
                imap.Add("INBOX", Mail("CI", "ci@builds.example", me, "[ci] nightly ok", "fine"));
                Ok(Until(() => imap.Count("Receipts") == 1, 8000), "rules: moved to Receipts on the server");
                Ok(Until(() => imap.Find("Receipts", "Your order 777 has shipped") != null && imap.Find("Receipts", "Your order 777 has shipped").Flags.Contains("receipts"), 3000), "rules: label kept as an IMAP keyword");
                Ok(Until(() => imap.Find("Archive", "[ci] nightly ok") != null && imap.Find("Archive", "[ci] nightly ok").Flags.Contains("\\Seen"), 8000), "rules: skip inbox = archived and marked read on the server");
                Ok(p.Rules.First(r => r.Id == "r1").Hits == 1, "rules: hit counted");
                // the Screener
                S.Screener = true; Screener.TurnOn(p);
                imap.Add("INBOX", Mail("Stranger", "stranger@new.example", me, "Hello there", "First time writing."));
                Ok(Until(() => store.Items("t1").Any(i => i.Subject == "Hello there" && i.Screened), 8000), "screener: a first-time sender waits in the Screener");
                Ok(imap.Find("INBOX", "Hello there") != null, "screener: it stays in the inbox on the server");
                p.ScreenerOk.Add("stranger@new.example");
                imap.Add("INBOX", Mail("Stranger", "stranger@new.example", me, "Second note", "Again."));
                Ok(Until(() => store.Items("t1").Any(i => i.Subject == "Second note" && !i.Screened), 8000), "screener: once approved, mail goes to the inbox");
                p.ScreenerNo.Add("pest@nag.example");
                imap.Add("INBOX", Mail("Pest", "pest@nag.example", me, "Buy now", "Please buy."));
                Ok(Until(() => imap.Find("Archive", "Buy now") != null, 8000), "screener: screened-out senders are archived");
                S.Screener = false;
                // actions: read flag, star, archive, delete (to Trash), bulk
                var coffee = store.Items("t1").First(i => i.Subject == "Coffee tomorrow?");
                E.SetSeen(coffee, true); E.SetStar(coffee, true);
                Ok(Until(() => imap.Find("INBOX", "Coffee tomorrow?").Flags.Contains("\\Seen") && imap.Find("INBOX", "Coffee tomorrow?").Flags.Contains("\\Flagged"), 5000), "actions: read + star synced to the server");
                Ok(E.Archive(coffee) == null && Until(() => imap.Find("Archive", "Coffee tomorrow?") != null && imap.Find("INBOX", "Coffee tomorrow?") == null, 5000), "actions: archive moves it on the server");
                Ok(p.Moves.Count == 0 || p.Moves.All(m => m.Folder != "Archive" || m.Sender != "riley@example.org"), "actions: archive isn't counted as a folder habit");
                foreach (var it in store.Items("t1").Where(i => i.FromAddr == "sam@example.org").ToList()) E.Trash(it);
                Ok(Until(() => imap.Count("Trash") == 2 && imap.Find("INBOX", "Saturday hike") == null, 5000), "actions: bulk delete = moved to Trash (not deleted)");
                var weekly = store.Items("t1").First(i => i.Subject == "This week");
                Ok(E.Move(weekly, null, "Receipts", true) == null && imap.Find("Receipts", "This week") != null, "actions: move to a folder");
                Ok(p.Moves.Any(m => m.Sender == "news@weekly.example" && m.Folder == "Receipts"), "actions: user moves are remembered for suggestions");
                // Spam / Not spam train the filter and move the mail
                int ns = p.Bayes.NS;
                var shop = store.Items("t1").FirstOrDefault(i => i.Subject == "Second note");
                Ok(shop != null && E.MarkSpam(shop, true) == null && Until(() => imap.Find("Junk", "Second note") != null, 5000) && p.Bayes.NS == ns + 1, "spam: 'Spam' moves to Junk and trains");
                Ok(Until(() => store.Items("t1").Any(i => i.Folder == "Junk" && i.Subject == "Second note"), 8000), "spam: synced");
                var back = store.Items("t1").First(i => i.Folder == "Junk" && i.Subject == "Second note");
                Ok(E.MarkSpam(back, false) == null && Until(() => imap.Find("INBOX", "Second note") != null, 5000), "spam: 'Not spam' moves it back to the inbox");
                // drafts saved to IMAP, replaced on the next save
                var d1 = new ComposeState { Account = "t1", To = "pat@example.org", Subject = "Draft one", Text = "v1", Rich = false }.Build(acc);
                Ok(E.SaveDraft("t1", d1, 0) == null && imap.Find("Drafts", "Draft one") != null && imap.Find("Drafts", "Draft one").Flags.Contains("\\Draft"), "drafts: saved to the server's Drafts with \\Draft");
                var uid1 = imap.Find("Drafts", "Draft one").Uid;
                var d2 = new ComposeState { Account = "t1", To = "pat@example.org", Subject = "Draft two", Text = "v2", Rich = false }.Build(acc);
                Ok(E.SaveDraft("t1", d2, uid1) == null && imap.Find("Drafts", "Draft two") != null && imap.Find("Drafts", "Draft one") == null, "drafts: saving again replaces the old draft");
                // sending: Undo send, send now, send later (captured by the SMTP stand-in)
                var st = new ComposeState { Account = "t1", To = "Pat <pat@example.org>", Cc = "newfriend@example.org", Subject = "Hike photos", Html = "<p>Here they are <b>now</b></p>", Rich = true };
                st.Files.Add(new[] { "photo.jpg", Convert.ToBase64String(new byte[] { 1, 2, 3 }) });
                var q1 = E.Queue(acc, st.Build(acc), DateTime.Now.AddSeconds(10), true);
                E.Tick(); Ok(E.Undo(q1.Id) && p.Outbox.Count == 0, "send: Undo within the delay takes it back");
                for (int i = 0; i < 3; i++) { E.Tick(); Thread.Sleep(100); }
                Eq(0, smtp.Count, "send: nothing reached the SMTP stand-in after Undo");
                var q2 = E.Queue(acc, st.Build(acc), DateTime.Now.AddSeconds(1.2), true);
                E.Tick(); Thread.Sleep(300); Eq(0, smtp.Count, "send: held during the undo delay");
                Ok(Until(() => { E.Tick(); return smtp.Count == 1; }, 8000), "send: sent after the delay");
                var sent = smtp.Mail[0];
                Ok(sent[0] == me && sent[1].Contains("pat@example.org") && sent[1].Contains("newfriend@example.org") && sent[2].Contains("Subject: Hike photos") && sent[2].Contains("photo.jpg") && sent[2].Contains("text/html"), "send: from, recipients, HTML body and attachment");
                Ok(Until(() => p.Outbox.Count == 0, 3000) && p.Trusted.Contains("newfriend@example.org"), "send: outbox empty, recipients now trusted");
                Ok(Until(() => imap.Find("Sent", "Hike photos") != null, 5000), "send: a copy appended to the server's Sent folder");
                var q3 = E.Queue(acc, new ComposeState { Account = "t1", To = "pat@example.org", Subject = "Later one", Text = "x", Rich = false }.Build(acc), DateTime.Now.AddSeconds(2), false);
                E.Tick(); Thread.Sleep(300); Eq(1, smtp.Count, "send later: not before its time");
                Ok(Until(() => { E.Tick(); return smtp.Count == 2; }, 8000), "send later: goes out at its time");
                // a failed send is kept and retried (wrong password), never lost
                smtp.Pass = "changed";
                var q4 = E.Queue(acc, new ComposeState { Account = "t1", To = "pat@example.org", Subject = "Retry me", Text = "x", Rich = false }.Build(acc), DateTime.Now, false);
                Ok(Until(() => { E.Tick(); lock (p) return p.Outbox.Any(x => x.Id == q4.Id && x.Error != null); }, 8000), "send: a failure stays in the Outbox with the error");
                Ok(!p.Outbox.First(x => x.Id == q4.Id).Error.Contains(imap.Pass), "send: the error never contains the password");
                lock (p) p.Outbox.Clear(); smtp.Pass = imap.Pass;
                // unsubscribe by email (asks first in the app; here the "yes" is the test)
                var plan = Unsubscribe.Plan("<mailto:leave@weekly.example?subject=unsubscribe>", null);
                Ok(PageBuilder.DoUnsubscribe(E, plan, "t1") == null && Until(() => { E.Tick(); return smtp.Mail.Any(m => m[1] == "leave@weekly.example"); }, 8000), "unsub: the mailto unsubscribe goes through the account's SMTP");
                // snooze
                var snz = store.Items("t1").First(i => i.Folder == "INBOX");
                var due = new List<Snooze>(); E.SnoozeDue += l => due.AddRange(l);
                E.SnoozeIt(snz, DateTime.Now.AddSeconds(1)); Ok(E.IsSnoozed(snz), "snooze: hidden until then");
                Ok(Until(() => { E.Tick(); return due.Count == 1; }, 5000) && !E.IsSnoozed(snz), "snooze: comes back with a reminder");
                // status for the family link: counts only
                var stat = Family.Json(E.UnreadPerAccount());
                Ok(stat.Contains("\"id\":\"t1\"") && stat.Contains("\"label\":\"Test\"") && stat.Contains("\"labelUserChosen\":true") && stat.Contains("\"labelHasAddress\":false") && stat.Contains("\"needsAttention\":false") && stat.Contains("\"attention\":null"), "status 1.1: opaque id, the user's label, no attention: " + stat);
                Ok(stat.Contains("\"account\":\"Test\"") && !stat.Contains("@") && !stat.Contains("hike") && !stat.Contains("Coffee"), "status: unread per account, no addresses or subjects: " + stat);
                // the cache on disk is encrypted and reloads
                store.Save("t1");
                var files = Directory.GetFiles(Path.Combine(dir, "cache"), "*", SearchOption.AllDirectories);
                Ok(files.Length > 3 && files.All(f => { var t = Encoding.UTF8.GetString(File.ReadAllBytes(f)); return !t.Contains("Saturday") && !t.Contains("zebra") && !t.Contains("example.org"); }), "cache: the synced mail is encrypted on disk (" + files.Length + " files)");
                Eq(store.Items("t1").Count, new MailStore(Path.Combine(dir, "cache")).Items("t1").Count, "cache: reloads from disk");
                // OAuth (XOAUTH2) against the stand-ins: refresh token -> access token -> IMAP + SMTP sign-in
                var http = new FakeHttp(); http.Answer = (m, pth, a, b) => b.Contains("refresh_token=rt-test") ? Tuple.Create(200, "{\"access_token\":\"at-xyz\",\"expires_in\":3600}") : Tuple.Create(400, "{\"error\":\"invalid_grant\"}");
                OAuth.TokenOverride = http.Url + "token"; imap.Token = "at-xyz"; smtp.Token = "at-xyz";
                var oa = new Account { Id = "t2", Email = me, Label = "OAuth", Preset = "gmail", Auth = "oauth-google", OAuthClientId = "cid", ImapHost = "127.0.0.1", ImapPort = imap.Port, ImapSecurity = "none", SmtpHost = "127.0.0.1", SmtpPort = smtp.Port, SmtpSecurity = "none", SendAs = new List<string>() };
                Secrets.Set("rt:t2", "rt-test");
                Ok(AccountTest.Run(oa, null, "rt-test", E) == null, "oauth: IMAP + SMTP sign-in with XOAUTH2 (stand-in token endpoint)");
                Ok(AccountTest.Run(acc, "wrong-password", null, E).Contains("didn't accept"), "account test: a wrong app password is explained");
                Ok(AccountTest.Run(acc, imap.Pass, null, E) == null, "account test: IMAP + SMTP sign-in with an app password");
                bool refused = false; try { Engine.Sec("none", "mail.example.com"); } catch { refused = true; }
                Ok(refused, "security: unencrypted connections only to this PC");
                OAuth.TokenOverride = null; http.Stop(); Secrets.RemoveAccount("t2");
            }
            finally { E.StopAll(); imap.Stop(); smtp.Stop(); Secrets.RemoveAccount("t1"); }
        }

        // ---------------- 1.1: why an account needs attention (code + plain words), from exceptions and from the IMAP stand-in's answers
        static void AttentionTests()
        {
            Eq("app-password-needed", Attention.Classify(new AuthenticationException("[ALERT] Application-specific password required: https://support.google.com/accounts/answer/185833")), "attention: Gmail's app-password answer");
            Eq("app-password-needed", Attention.Classify(new AuthenticationException("[ALERT] Please log in via your web browser")), "attention: \"log in via your web browser\"");
            Eq("sign-in-failed", Attention.Classify(new AuthenticationException("[AUTHENTICATIONFAILED] Invalid credentials (Failure)")), "attention: wrong password");
            Eq("quota", Attention.Classify(new AuthenticationException("[ALERT] Account exceeded command or bandwidth limits")), "attention: provider limits (even when the sign-in is refused)");
            Eq("quota", Attention.Classify(new Exception("[OVERQUOTA] Mailbox is full")), "attention: mailbox full");
            Eq("server-unreachable", Attention.Classify(new System.Net.Sockets.SocketException(10061)), "attention: connection refused");
            Eq("server-unreachable", Attention.Classify(new IOException("x", new TimeoutException())), "attention: time-out");
            Eq("error", Attention.Classify(new InvalidOperationException("something odd")), "attention: anything else");
            Ok(Attention.Classify(null) == null && Attention.Text(null) == null, "attention: none");
            Ok(Attention.Codes.All(cd => { var tx = Attention.Text(cd); return tx != null && tx.Length > 20 && !tx.Contains("@"); }), "attention: every code has plain words");
            Ok(Attention.LooksLikeAddress("me@home.example") && !Attention.LooksLikeAddress("Personal") && !Attention.LooksLikeAddress("@work"), "labels that look like an address");
            Ok(!Engine.LabelUserChosen(new Account { Label = "Gmail", Preset = "gmail" }) && Engine.LabelUserChosen(new Account { Label = "Personal", Preset = "gmail" }) && !Engine.LabelUserChosen(new Account { Label = " ", Preset = "custom" }) && Engine.LabelUserChosen(new Account { Label = "Work", Preset = "custom" }), "label: user-chosen vs the wizard's default");
            Ok(Engine.GenericLabel(new Account { Preset = "outlook" }, 0) == "Outlook" && Engine.GenericLabel(new Account { Preset = "custom", Email = "x@y.example" }, 2) == "Account 3", "label: made up without the address");
            // the engine against the stand-in: each server answer becomes the right code, and a good sign-in clears it
            var imap = new FakeImap(); var smtp = new FakeSmtp(); var dir = Path.Combine(Out, "attention"); Directory.CreateDirectory(dir);
            var p = new Profile(); p.Fix(); var S = new MailSettings();
            var acc = new Account { Id = "at1", Email = "alex@example.test", Name = "Alex", Label = "", Preset = "gmail", ImapHost = "127.0.0.1", ImapPort = imap.Port, ImapSecurity = "none", SmtpHost = "127.0.0.1", SmtpPort = smtp.Port, SmtpSecurity = "none", SendAs = new List<string>() };
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); int closed = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            var gone = new Account { Id = "at2", Email = "alex@example.test", Label = "Old server", Preset = "custom", ImapHost = "127.0.0.1", ImapPort = closed, ImapSecurity = "none", SmtpHost = "127.0.0.1", SmtpPort = closed, SmtpSecurity = "none", SendAs = new List<string>() };
            p.Accounts.Add(acc); p.Accounts.Add(gone); Secrets.Set("pw:at1", imap.Pass); Secrets.Set("pw:at2", "x");
            imap.LoginReply = "NO [ALERT] Application-specific password required: https://support.google.com/accounts/answer/185833";
            var E = new Engine(p, S, new MailStore(Path.Combine(dir, "cache")));
            try
            {
                E.Start(); var w = E.W("at1"); var w2 = E.W("at2");
                Ok(Until(() => w.AttentionCode == "app-password-needed", 15000), "engine: Gmail-style answer -> app-password-needed (" + w.AttentionCode + ")");
                Ok(Until(() => w2.AttentionCode == "server-unreachable", 15000), "engine: nothing listening -> server-unreachable (" + w2.AttentionCode + ")");
                var stat = Family.Json(E.UnreadPerAccount());
                Ok(stat.Contains("\"label\":\"Gmail\"") && stat.Contains("\"labelUserChosen\":false") && stat.Contains("\"label\":\"Old server\"") && stat.Contains("\"needsAttention\":true") && !stat.Contains("support.google") && !stat.Contains("@"), "status: codes and plain words only, no server text or address: " + stat);
                imap.LoginReply = "NO [ALERT] Account exceeded command or bandwidth limits"; E.W("at1").Kick(true);
                Ok(Until(() => w.AttentionCode == "quota", 20000), "engine: limits answer -> quota (" + w.AttentionCode + ")");
                imap.LoginReply = null; w.Kick(true);
                Ok(Until(() => w.AttentionCode == null && (w.State == "idle" || w.State == "synced"), 30000), "engine: a good sign-in clears it (" + w.AttentionCode + ", " + w.State + ")");
            }
            finally { E.StopAll(); imap.Stop(); smtp.Stop(); Secrets.RemoveAccount("at1"); Secrets.RemoveAccount("at2"); }
        }

        // ---------------- the family pipe of a headless test copy (its own test folder and pipe name; no window, no tray)
        static string Cmd(string exe, string env, string line)
        {
            var psi = new ProcessStartInfo(exe, "--cmd \"" + line + "\"") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            psi.EnvironmentVariables["HALLOW_EMAIL_TESTDIR"] = env;
            using (var pr = Process.Start(psi)) { var o = pr.StandardOutput.ReadToEnd().Trim(); pr.WaitForExit(); return pr.ExitCode + " " + o; }
        }
        static void PipeTests()
        {
            var exe = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Run).Assembly.Location), "..", "HallowEmail.exe"));
            var env = Path.Combine(Out, "pipe-copy"); Directory.CreateDirectory(env);
            var psi = new ProcessStartInfo(exe, "--tray") { UseShellExecute = false, CreateNoWindow = true };
            psi.EnvironmentVariables["HALLOW_EMAIL_TESTDIR"] = env; psi.EnvironmentVariables["HALLOW_EMAIL_HEADLESS"] = "1";
            var app = Process.Start(psi); Children.Add(app);
            var imap = new FakeImap(); var smtp = new FakeSmtp();
            try
            {
                try { app.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                string ping = null; Ok(Until(() => (ping = Cmd(exe, env, "ping")).StartsWith("0 ok"), 15000), "pipe: ping answers: " + ping);
                Ok(ping.Contains("\"app\":\"Email\"") && ping.Contains("\"version\":\"" + Program.Version + "\""), "pipe: app and version");
                var st = Cmd(exe, env, "status");
                Ok(st.StartsWith("0 ok {") && st.Contains("\"accounts\":[]") && st.Contains("\"unread\":0") && st.Contains("\"lastCrash\""), "pipe: status");
                Ok(File.Exists(Path.Combine(env, "status.json")) && File.ReadAllText(Path.Combine(env, "status.json")).Contains("\"app\":\"Email\""), "pipe: status.json written in the test folder");
                Ok(Cmd(exe, env, "menu").Contains("\"compose\""), "pipe: menu");
                // 1.1: per-account unread + attention over the pipe, against the local IMAP / SMTP stand-in (never a real account)
                imap.Add("INBOX", Mail("Sam Lee", "sam@example.org", "alex@example.test", "Lunch?", "Noodles at noon."));
                imap.Add("INBOX", Mail("Riley", "riley@example.org", "alex@example.test", "Photos", "Here."));
                Ok(Cmd(exe, env, "test-account pa1 gmail " + imap.Port + " " + smtp.Port + " " + imap.Pass).StartsWith("0 ok"), "pipe: a test account (the wizard's default label left empty)");
                Ok(Cmd(exe, env, "test-account pa2 custom " + imap.Port + " " + smtp.Port + " wrong-pass me@home.example").StartsWith("0 ok"), "pipe: a second one, wrong password, labelled with an address by the user");
                string accs = null;
                Ok(Until(() => { accs = Cmd(exe, env, "accounts"); return accs.Contains("\"unread\":2,") && accs.Contains("\"attention\":\"sign-in-failed\""); }, 20000), "pipe: accounts: " + accs);
                Ok(accs.Contains("\"id\":\"pa1\"") && accs.Contains("\"label\":\"Gmail\"") && accs.Contains("\"labelUserChosen\":false") && accs.Contains("\"needsAttention\":false"), "pipe: account 1 - opaque id, a made-up label (not chosen by the user), fine");
                Ok(accs.Contains("\"id\":\"pa2\"") && accs.Contains("\"label\":\"me@home.example\"") && accs.Contains("\"labelUserChosen\":true") && accs.Contains("\"labelHasAddress\":true") && accs.Contains("\"needsAttention\":true") && accs.Contains("\"attentionText\":\"The mail server didn"), "pipe: account 2 - the user's own label is marked as theirs (and as an address), sign-in failed with plain words");
                Ok(!accs.Contains("alex@example.test") && !accs.Contains("Noodles") && !accs.Contains("Lunch") && !accs.Contains("sam@") && !accs.Contains("Invalid credentials"), "pipe: no account address, no mail content, no server text");
                Ok(accs.Contains("\"needsAttention\":1"), "pipe: one account needs attention");
                st = Cmd(exe, env, "status"); var sj = File.ReadAllText(Path.Combine(env, "status.json"));
                Ok(st.Contains("\"id\":\"pa2\"") && st.Contains("\"attention\":\"sign-in-failed\"") && sj.Contains("\"labelUserChosen\":false") && sj.Contains("\"needsAttention\":1") && !sj.Contains("alex@example.test"), "pipe: status and status.json carry the same");
                Ok(Cmd(exe, env, "compose sam@example.org").StartsWith("0 ok"), "pipe: compose <to>");
                Ok(Cmd(exe, env, "compose not-an-address").StartsWith("1 error"), "pipe: compose refuses a non-address");
                Ok(Cmd(exe, env, "open inbox").StartsWith("0 ok") && Cmd(exe, env, "open nowhere").StartsWith("1 error"), "pipe: open <page>");
                Ok(Cmd(exe, env, "frobnicate").StartsWith("1 error unknown command"), "pipe: unknown command");
                Ok(Cmd(exe, env, "test-setlook light").Contains("ok light") && Cmd(exe, env, "test-setlook dark").Contains("ok dark"), "pipe: Look override switches light / dark");
                Ok(Cmd(exe, env, "test-autostart on").Contains("True") && Cmd(exe, env, "test-autostart off").Contains("False"), "pipe: start with Windows (test file, not the registry)");
                Ok(Cmd(exe, env, "quit").StartsWith("0 ok") && app.WaitForExit(8000), "pipe: quit");
                Ok(Cmd(exe, env, "ping").StartsWith("2 "), "pipe: exit code 2 when not running");
            }
            finally { try { if (!app.HasExited) app.Kill(); } catch { } imap.Stop(); smtp.Stop(); }
        }
    }
}
