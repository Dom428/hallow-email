// Hallow Email - the local spam filter. Private: nothing leaves this PC.
//  - a trainable naive Bayes classifier (Robinson / Fisher combining, like SpamBayes and Thunderbird's junk filter) on tokens from the
//    subject, body, sender domain, link hosts and a few headers. It starts from a small made-up seed corpus (below) and learns from your
//    "Spam" / "Not spam" clicks. The counts live in profile.bin (encrypted).
//  - rule signals with reasons: your provider's spam headers, SPF/DKIM/DMARC failures, brand-name / lookalike senders, link text that
//    names another site, URL shorteners, image-only mail, spam phrases, first-time sender + urgency, Hallow Shield's badware hosts.
//  - trusted senders (people you've written to) and mail you marked "Not spam" are never spam. Spam is MOVED to the account's Junk
//    folder (so it syncs) - never deleted.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace HallowEmail
{
    [DataContract]
    public class BayesData
    {
        [DataMember] public Dictionary<string, int> S = new Dictionary<string, int>();
        [DataMember] public Dictionary<string, int> H = new Dictionary<string, int>();
        [DataMember] public int NS; [DataMember] public int NH; [DataMember] public bool Seeded;
    }

    public class SpamVerdict { public double Score; public double Bayes; public bool IsSpam; public List<string> Why = new List<string>(); public string Summary { get { return string.Join(" ", Why); } } }

    public static class Spam
    {
        public static double Threshold(string sensitivity) { switch ((sensitivity ?? "").ToLowerInvariant()) { case "low": return 0.92; case "high": return 0.72; default: return 0.84; } }

        // ---- tokens
        public static List<string> Tokens(string subject, string text, string fromAddr, IEnumerable<string> linkHosts, string extra)
        {
            var t = new HashSet<string>();
            foreach (Match m in Regex.Matches((subject ?? "").ToLowerInvariant(), @"[\p{L}\p{N}$€£!'-]{2,24}")) t.Add("s:" + m.Value.Trim('\'', '-'));
            int n = 0;
            foreach (Match m in Regex.Matches((text ?? "").ToLowerInvariant(), @"[\p{L}\p{N}$€£'-]{3,20}")) { t.Add(m.Value.Trim('\'', '-')); if (++n > 1500) break; }
            var dom = Domains.Of(fromAddr); if (dom.Length > 0) { t.Add("d:" + Domains.Registrable(dom)); t.Add("tld:" + dom.Substring(dom.LastIndexOf('.') + 1)); }
            if (linkHosts != null) foreach (var h in linkHosts.Take(40)) t.Add("u:" + Domains.Registrable(h));
            if (!string.IsNullOrEmpty(extra)) foreach (var x in extra.Split(' ')) if (x.Length > 0) t.Add("h:" + x);
            if (Regex.IsMatch(subject ?? "", @"[A-Z]{6,}")) t.Add("s:*caps*");
            if ((subject ?? "").Contains("!!")) t.Add("s:*bangs*");
            return t.ToList();
        }

        public static void Train(BayesData b, IEnumerable<string> tokens, bool spam, int sign = 1)
        {
            lock (b)
            {
                var d = spam ? b.S : b.H;
                foreach (var tk in tokens) { int c; d.TryGetValue(tk, out c); c += sign; if (c <= 0) d.Remove(tk); else d[tk] = c; }
                if (spam) b.NS = Math.Max(0, b.NS + sign); else b.NH = Math.Max(0, b.NH + sign);
            }
        }
        // 0 = ham, 1 = spam, 0.5 = no idea
        public static double Score(BayesData b, IEnumerable<string> tokens)
        {
            lock (b)
            {
                if (b.NS == 0 || b.NH == 0) return 0.5;
                var fs = new List<double>();
                foreach (var tk in tokens)
                {
                    int s, h; b.S.TryGetValue(tk, out s); b.H.TryGetValue(tk, out h);
                    int n = s + h; if (n == 0) continue;
                    double ps = (double)s / b.NS, ph = (double)h / b.NH, p = ps / (ps + ph);
                    double f = (0.45 * 0.5 + n * p) / (0.45 + n);   // Robinson's f(w), x = 0.5, s = 0.45
                    if (Math.Abs(f - 0.5) >= 0.1) fs.Add(f);
                }
                if (fs.Count == 0) return 0.5;
                fs = fs.OrderByDescending(f => Math.Abs(f - 0.5)).Take(60).ToList();
                double lnH = 0, lnS = 0; foreach (var f in fs) { lnH += Math.Log(Math.Max(1e-9, 1 - f)); lnS += Math.Log(Math.Max(1e-9, f)); }
                double S = 1 - ChiInv(-2 * lnH, 2 * fs.Count), H = 1 - ChiInv(-2 * lnS, 2 * fs.Count);   // SpamBayes: S from ln(1-f), H from ln f
                return (1 + S - H) / 2;
            }
        }
        // the chi-squared survival function for an even number of degrees of freedom
        static double ChiInv(double chi, int df)
        {
            double m = chi / 2, sum, term; sum = term = Math.Exp(-m);
            for (int i = 1; i < df / 2; i++) { term *= m / i; sum += term; }
            return Math.Min(sum, 1.0);
        }

        static readonly string[] Shorteners = { "bit.ly", "tinyurl.com", "t.ly", "is.gd", "rb.gy", "cutt.ly", "shorturl.at", "ow.ly", "tiny.cc", "goo.su", "s.id", "v.gd", "rebrand.ly", "bl.ink", "short.io", "buff.ly" };
        static readonly string[] Phrases = { "act now", "100% free", "risk-free", "risk free", "no credit check", "winner", "you have won", "you've won", "claim your prize", "cash prize", "lottery",
            "million dollars", "wire transfer", "western union", "bitcoin investment", "crypto opportunity", "guaranteed income", "work from home and earn", "earn $", "double your", "miracle",
            "lose weight fast", "cheap meds", "viagra", "casino bonus", "free spins", "limited time offer", "click below to claim", "dear beneficiary", "inheritance", "next of kin",
            "unclaimed funds", "your account will be suspended", "verify your account", "confirm your identity", "update your payment", "unusual activity", "gift card", "hot singles", "congratulations you" };
        static readonly Regex Urgent = new Regex(@"\b(urgent|immediately|right away|within 24 hours|within 48 hours|final notice|last warning|suspended|locked|expire[sd]? today|act now|asap)\b", RegexOptions.IgnoreCase);

        public class Input
        {
            public MailItem Mail; public string Html; public List<string> LinkHosts = new List<string>(); public List<LinkCheck> Links = new List<LinkCheck>();
            public bool FirstTime; public bool Trusted; public int Images;
        }
        public static SpamVerdict Judge(BayesData b, Input x, string sensitivity, Func<string, bool> badware = null)
        {
            var v = new SpamVerdict(); var m = x.Mail;
            if (x.Trusted) { v.Why.Add("You've written to this sender, so it's never spam."); return v; }
            if (m.NotSpam) { v.Why.Add("You marked mail like this as not spam."); return v; }
            double sig = 0; Action<double, string> add = (w, why) => { sig += w; v.Why.Add(why); };
            var sh = m.SpamHeaders ?? "";
            if (Regex.IsMatch(sh, @"X-Spam-Flag:\s*YES", RegexOptions.IgnoreCase) || Regex.IsMatch(sh, @"X-Spam-Status:\s*Yes", RegexOptions.IgnoreCase) || Regex.IsMatch(sh, @"X-Spam:\s*(yes|true|spam)", RegexOptions.IgnoreCase)) add(0.45, "Your provider's filter marked it as spam (X-Spam headers).");
            var scl = Regex.Match(sh, @"SCL:\s*(-?\d)"); int sclv; if (scl.Success && int.TryParse(scl.Groups[1].Value, out sclv) && sclv >= 5) add(0.4, "Microsoft's filter rated it " + sclv + " out of 9 (SCL).");
            var auth = Phish.Auth(m.AuthResults);
            string a; if (auth.TryGetValue("dmarc", out a) && a == "fail") add(0.3, "DMARC failed - the sender's domain doesn't vouch for it.");
            if (auth.TryGetValue("spf", out a) && (a == "fail" || a == "softfail")) add(0.12, "SPF " + a + " - sent from a server the domain doesn't list.");
            if (auth.TryGetValue("dkim", out a) && a == "fail") add(0.12, "DKIM failed - the signature doesn't match.");
            var brand = Phish.BrandIn(m.FromName);
            if (brand != null) { var reg = Domains.Registrable(Domains.Of(m.FromAddr)); if (!Phish.Brands[brand].Any(d => Domains.Registrable(d) == reg)) add(0.35, "The name says " + brand + " but the address isn't " + brand + "'s."); }
            if (Phish.Lookalike(Domains.Of(m.FromAddr)) != null) add(0.35, "The sender's domain is a look-alike of a well-known one.");
            var mism = x.Links.Where(l => l.Mismatch).ToList(); if (mism.Count > 0) add(0.25, "A link's text names " + mism[0].TextHost + " but it goes to " + mism[0].Host + ".");
            var shorts = x.LinkHosts.Where(h => Shorteners.Any(s => h.Equals(s, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
            if (shorts.Count > 0) add(0.12, "Links hidden behind a URL shortener (" + shorts[0] + ").");
            if (badware != null) { var bw = x.LinkHosts.FirstOrDefault(badware); if (bw != null) add(0.45, "Links to " + bw + ", which Hallow Shield's lists know as harmful."); }
            var text = (m.Text ?? "").Trim();
            if (x.Html != null && x.Images > 0 && Regex.Replace(text, @"\s+", "").Length < 40) add(0.2, "Almost no text - the whole message is pictures.");
            var hay = ((m.Subject ?? "") + " " + text).ToLowerInvariant(); var found = Phrases.Where(p => hay.Contains(p)).Take(4).ToList();
            if (found.Count > 0) add(Math.Min(0.3, 0.1 * found.Count), "Typical spam wording (\"" + string.Join("\", \"", found) + "\").");
            if (x.FirstTime && Urgent.IsMatch(hay)) add(0.15, "A first-time sender pushing you to hurry.");
            sig = Math.Min(0.9, sig);
            var tokens = Tokens(m.Subject, m.Text, m.FromAddr, x.LinkHosts, HeaderTokens(m));
            v.Bayes = Score(b, tokens);
            double bs = Math.Max(0, (v.Bayes - 0.5) * 2) * 0.8;
            if (v.Bayes >= 0.9) v.Why.Insert(0, "It reads like the spam you've marked before (" + Math.Round(v.Bayes * 100) + "% by the learning filter).");
            v.Score = 1 - (1 - bs) * (1 - sig);
            v.IsSpam = v.Score >= Threshold(sensitivity);
            return v;
        }
        public static string HeaderTokens(MailItem m)
        {
            var l = new List<string>();
            if (!string.IsNullOrEmpty(m.ListUnsubscribe)) l.Add("unsub"); if (!string.IsNullOrEmpty(m.ListId)) l.Add("listid");
            if (m.HasAttachments) l.Add("attach");
            foreach (var kv in Phish.Auth(m.AuthResults)) l.Add(kv.Key + "=" + kv.Value);
            return string.Join(" ", l);
        }
        public static BayesData Seeded()
        {
            var b = new BayesData { Seeded = true };
            foreach (var s in Seed.SpamMail) Train(b, Tokens(s[0], s[1], s[2], null, null), true);
            foreach (var h in Seed.HamMail) Train(b, Tokens(h[0], h[1], h[2], null, null), false);
            return b;
        }
    }

    // the seed corpus: short made-up examples (subject, body, sender), written for Hallow Email. Example domains only.
    public static class Seed
    {
        public static readonly string[][] SpamMail = {
            new[] { "You have WON a $1,000 gift card!!", "Congratulations you have been selected as our lucky winner. Click below to claim your prize before it expires today. Limited time offer.", "promo@lucky-rewards.example" },
            new[] { "URGENT: Your account will be suspended", "We detected unusual activity. Verify your account within 24 hours or it will be locked. Click here to confirm your identity.", "security@acc0unt-check.example" },
            new[] { "Earn $5000 a week from home", "Work from home and earn guaranteed income. No experience needed. Double your money with our crypto opportunity.", "jobs@fast-cash.example" },
            new[] { "Dear Beneficiary", "I am a barrister contacting you about unclaimed funds of 4.5 million dollars left by a late client. As next of kin please send your details for the wire transfer.", "barrister@inherit-office.example" },
            new[] { "Cheap meds - no prescription", "Buy cheap meds online, viagra and more, discreet shipping, 100% free consultation.", "pharmacy@med-store.example" },
            new[] { "Hot singles in your area", "Hot singles want to meet you tonight. Click to see photos. Free signup.", "meet@date-now.example" },
            new[] { "Final notice: payment failed", "Your subscription payment failed. Update your payment details immediately to avoid suspension. Login here.", "billing@netfl1x-billing.example" },
            new[] { "Claim your casino bonus - 200 free spins", "Exclusive casino bonus: 200 free spins and a 500% deposit match. Act now, offer ends tonight!", "vip@spin-casino.example" },
            new[] { "Bitcoin investment returns 300%", "Our bitcoin investment program guarantees 300% returns in 30 days. Risk-free. Join thousands of happy investors.", "invest@coin-profit.example" },
            new[] { "Lose weight fast with this miracle pill", "Doctors hate this miracle pill. Lose weight fast without diet or exercise. Order now risk free.", "health@slim-miracle.example" },
            new[] { "Your package could not be delivered", "We tried to deliver your parcel. Pay the small customs fee of 1.99 to reschedule delivery: click the link below.", "parcel@dhl-delivery-notice.example" },
            new[] { "Re: invoice overdue", "Please see the attached invoice and pay immediately by wire transfer. This is the final notice before legal action.", "accounts@invoice-dept.example" },
            new[] { "Congratulations! You are selected", "You are selected for an exclusive reward. Confirm your shipping address and pay only for shipping to receive your free iPhone.", "rewards@prize-center.example" },
            new[] { "Your mailbox is full", "Your email storage is full and incoming mail will be suspended. Click here to upgrade your mailbox quota now.", "admin@mail-quota-alert.example" },
            new[] { "Important security update required", "Unusual sign-in activity was detected. Confirm your password at the link below or your account will be locked.", "no-reply@secure-login-verify.example" },
            new[] { "Get rich quick - secret method", "Discover the secret method millionaires use. Make money fast. Limited spots, act now.", "info@rich-secret.example" },
            new[] { "Lottery winner notification", "Your email address won the international lottery draw. To claim your cash prize contact our agent with your bank details.", "claims@euro-lotto.example" },
            new[] { "Gift card reward waiting", "A gift card reward is waiting for you. Complete a short survey and claim it today. Expires soon!", "survey@reward-hub.example" },
            new[] { "Re: Your order has been cancelled", "Your order was cancelled due to payment problem. To reactivate, verify your card details here within 48 hours.", "support@amaz0n-orders.example" },
            new[] { "Low interest loan approved", "Your loan is pre-approved. No credit check. Get cash today. Reply with your account number.", "loans@quick-approve.example" },
            new[] { "SEO services for your website", "We can get your website to the first page of Google guaranteed. Cheap SEO packages, reply for a price list.", "seo@rank-boost.example" },
            new[] { "Crypto wallet suspended", "Your crypto wallet has been suspended due to suspicious activity. Restore access immediately by entering your recovery phrase.", "support@wallet-restore.example" },
            new[] { "Exclusive deal just for you!!!", "BUY NOW and save 90%!!! Designer watches and bags at unbelievable prices. Free shipping worldwide.", "deals@replica-shop.example" },
            new[] { "Private message waiting", "You have a private message from a secret admirer. Click here to read it now. Free to join.", "notify@secret-admirer.example" },
            new[] { "Tax refund pending", "You are eligible for a tax refund of 482.10. Submit the form with your bank details to receive the payment.", "refunds@tax-return-gov.example" },
        };
        public static readonly string[][] HamMail = {
            new[] { "Dinner on Friday?", "Hey, are you free for dinner on Friday? Thinking of the Italian place near the station around seven.", "sam@example.org" },
            new[] { "Notes from today's meeting", "Hi all, attached are the notes from today's planning meeting. Next steps: draft the schedule and review the budget by Thursday.", "jordan@example.com" },
            new[] { "Your order has shipped", "Good news, your order 10443 has shipped and should arrive on Tuesday. You can track it from your account page.", "orders@shop.example.com" },
            new[] { "Receipt for your payment", "Thanks for your payment of 12.99 for your monthly plan. This receipt is for your records.", "billing@service.example.com" },
            new[] { "Weekly newsletter: what's new", "This week: three new recipes, a guide to repotting plants and our reader photo of the week. Unsubscribe anytime.", "news@garden-weekly.example.com" },
            new[] { "Photos from the weekend", "Here are the photos from the hike on Saturday. The one at the lake turned out great.", "riley@example.net" },
            new[] { "Re: project update", "Thanks for the update. I reviewed the changes and left a couple of comments. Let's talk tomorrow.", "casey@example.com" },
            new[] { "Your verification code", "Your sign-in verification code is 482913. It expires in 10 minutes. If you didn't try to sign in you can ignore this.", "no-reply@accounts.example.com" },
            new[] { "Flight booking confirmation", "Your booking reference is K7Q2PL. Flight departs Monday 08:40. Check in opens 24 hours before departure.", "booking@airline.example.com" },
            new[] { "Happy birthday!", "Happy birthday! Hope you have a great day. Let's celebrate this weekend.", "mum@example.org" },
            new[] { "Invoice 2026-114 from the plumber", "Hi, please find invoice 2026-114 for the repair on the 12th attached. Payment within 30 days is fine. Thanks.", "office@plumbing.example.com" },
            new[] { "Re: can you send the file?", "Sure, I've attached the spreadsheet. Let me know if anything looks off.", "taylor@example.com" },
            new[] { "Reminder: dentist appointment", "This is a reminder of your appointment on Wednesday at 3pm. Reply to reschedule.", "reception@dental.example.com" },
            new[] { "New comment on your post", "Morgan commented on your post: Looks great, where was this taken? Reply on the site.", "notifications@community.example.com" },
            new[] { "Library books due soon", "Two items you borrowed are due back on the 28th. You can renew them online.", "library@city.example.gov" },
            new[] { "Weekend plans", "We're thinking of going to the coast on Sunday, want to come? We can pick you up.", "alex@example.net" },
            new[] { "Your monthly statement is ready", "Your statement for October is ready to view in online banking. Sign in through the app as usual.", "statements@bank.example.com" },
            new[] { "Build succeeded", "The nightly build succeeded. 412 tests passed. Artifacts are available in the usual place.", "ci@builds.example.com" },
            new[] { "Re: lunch", "Sounds good, see you at 12:30 at the cafe.", "jamie@example.org" },
            new[] { "School trip consent form", "Please sign and return the consent form for next week's museum trip by Friday.", "office@school.example.org" },
            new[] { "Thanks for your help", "Just wanted to say thanks for helping me move last weekend, I owe you one.", "drew@example.net" },
            new[] { "Password changed", "The password for your account was changed. If this was you, no action is needed.", "security@accounts.example.com" },
            new[] { "Game night", "Game night at mine on Thursday, bring snacks. Starting around eight.", "pat@example.com" },
            new[] { "Delivery scheduled for tomorrow", "Your parcel is scheduled for delivery tomorrow between 9 and 1. No signature needed.", "tracking@courier.example.com" },
            new[] { "Quarterly report draft", "Attached is the first draft of the quarterly report. Please send feedback by the end of the week.", "finance@example.com" },
            new[] { "Re: holiday dates", "I've booked the 22nd to the 29th off. Can you cover the Monday?", "chris@example.com" },
            new[] { "Your subscription renewed", "Your annual subscription renewed today. Your receipt is attached. Manage your plan in account settings.", "billing@app.example.com" },
            new[] { "Recipe you asked for", "Here's the soup recipe: onions, carrots, lentils, stock, cumin. Simmer for forty minutes.", "grandma@example.org" },
            new[] { "Meeting moved to 3pm", "Heads up, the design review moved to 3pm today, same room.", "lee@example.com" },
            new[] { "Welcome to the team", "Welcome aboard! Your first day starts at 9. I'll meet you at reception and show you around.", "manager@example.com" },
        };
    }
}
