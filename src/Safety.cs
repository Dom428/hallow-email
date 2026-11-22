// Hallow Email - safety: phishing hints, attachment flags and the Microsoft Defender scan before an attachment opens.
// These are hints, not verdicts: they tell you what looks off and why. Nothing is deleted or blocked without you.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HallowEmail
{
    public class Hint { public string Level; public string Text; public Hint(string level, string text) { Level = level; Text = text; } }   // Level: red, amber, info, green

    public static class Phish
    {
        // brands that phishers like to borrow, and the domains their real mail comes from
        public static readonly Dictionary<string, string[]> Brands = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
            { "PayPal", new[] { "paypal.com", "paypal.co.uk", "paypal.de" } }, { "Apple", new[] { "apple.com", "icloud.com", "me.com" } },
            { "iCloud", new[] { "apple.com", "icloud.com" } }, { "Microsoft", new[] { "microsoft.com", "outlook.com", "live.com", "office.com", "microsoftonline.com", "office365.com", "xbox.com" } },
            { "Outlook", new[] { "microsoft.com", "outlook.com", "live.com", "office.com" } }, { "Office 365", new[] { "microsoft.com", "office.com", "office365.com" } },
            { "Amazon", new[] { "amazon.com", "amazon.co.uk", "amazon.de", "amazon.fr", "amazon.it", "amazon.es", "amazon.ca", "amazonses.com" } },
            { "Google", new[] { "google.com", "gmail.com", "youtube.com" } }, { "Gmail", new[] { "google.com", "gmail.com" } }, { "Netflix", new[] { "netflix.com" } },
            { "Steam", new[] { "steampowered.com", "steamcommunity.com", "valvesoftware.com" } }, { "Discord", new[] { "discord.com", "discordapp.com" } },
            { "Facebook", new[] { "facebook.com", "facebookmail.com", "meta.com" } }, { "Instagram", new[] { "instagram.com", "mail.instagram.com", "facebookmail.com" } },
            { "WhatsApp", new[] { "whatsapp.com" } }, { "DHL", new[] { "dhl.com", "dhl.de", "dhl.co.uk" } }, { "FedEx", new[] { "fedex.com" } }, { "UPS", new[] { "ups.com" } },
            { "USPS", new[] { "usps.com" } }, { "Royal Mail", new[] { "royalmail.com" } }, { "Coinbase", new[] { "coinbase.com" } }, { "Binance", new[] { "binance.com" } },
            { "Chase", new[] { "chase.com" } }, { "Bank of America", new[] { "bankofamerica.com", "bofa.com" } }, { "Wells Fargo", new[] { "wellsfargo.com" } },
            { "HMRC", new[] { "hmrc.gov.uk", "gov.uk" } }, { "IRS", new[] { "irs.gov" } }, { "LinkedIn", new[] { "linkedin.com" } }, { "Dropbox", new[] { "dropbox.com", "dropboxmail.com" } },
            { "DocuSign", new[] { "docusign.com", "docusign.net" } }, { "Adobe", new[] { "adobe.com" } }, { "eBay", new[] { "ebay.com", "ebay.co.uk", "ebay.de" } },
            { "Epic Games", new[] { "epicgames.com" } }, { "Roblox", new[] { "roblox.com" } }, { "Spotify", new[] { "spotify.com" } }, { "Twitch", new[] { "twitch.tv" } },
            { "Norton", new[] { "norton.com", "nortonlifelock.com" } }, { "McAfee", new[] { "mcafee.com" } }, { "Geek Squad", new[] { "bestbuy.com", "geeksquad.com" } },
            { "Wise", new[] { "wise.com" } }, { "Revolut", new[] { "revolut.com" } }, { "Battle.net", new[] { "battle.net", "blizzard.com" } }, { "Xbox", new[] { "xbox.com", "microsoft.com" } },
            { "PlayStation", new[] { "playstation.com", "sony.com" } } };

        public static List<Hint> Check(MailItem m, bool firstTime)
        {
            var h = new List<Hint>();
            var dom = Domains.Of(m.FromAddr); var reg = Domains.Registrable(dom);
            // 1. the name says a brand, the address doesn't
            var brand = BrandIn(m.FromName);
            if (brand != null && !Brands[brand].Any(d => reg == Domains.Registrable(d) || dom.EndsWith("." + d)))
                h.Add(new Hint("red", "The name says \"" + brand + "\", but the mail comes from " + (dom.Length > 0 ? dom : "an unknown address") + " - not one of " + brand + "'s own domains."));
            // 2. an address in the display name that isn't the real one
            var inName = Regex.Match(m.FromName ?? "", @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}");
            if (inName.Success && !inName.Value.Equals(m.FromAddr ?? "", StringComparison.OrdinalIgnoreCase))
                h.Add(new Hint("red", "The name shows " + inName.Value + ", but the real sender is " + m.FromAddr + "."));
            // 3. lookalike domains
            var look = Lookalike(dom);
            if (look != null) h.Add(new Hint("red", look));
            // 4. what the receiving server checked (Authentication-Results)
            var auth = Auth(m.AuthResults);
            if (auth.Count > 0)
            {
                var bad = auth.Where(kv => kv.Value == "fail" || kv.Value == "softfail" || kv.Value == "permerror").Select(kv => kv.Key.ToUpperInvariant() + " " + kv.Value).ToList();
                if (bad.Count > 0) h.Add(new Hint(auth.ContainsKey("dmarc") && auth["dmarc"] == "fail" ? "red" : "amber", "Your provider's checks failed: " + string.Join(", ", bad) + " - the sender may be faked."));
                else if (auth.ContainsKey("dmarc") && auth["dmarc"] == "pass") h.Add(new Hint("green", "Sender verified by your provider (" + string.Join(", ", auth.Select(kv => kv.Key.ToUpperInvariant() + " " + kv.Value)) + ")."));
                else if (auth.Count > 0) h.Add(new Hint("info", "Provider checks: " + string.Join(", ", auth.Select(kv => kv.Key.ToUpperInvariant() + " " + kv.Value)) + "."));
            }
            // 5. replies go somewhere else
            var rd = Domains.Of(m.ReplyTo);
            if (rd.Length > 0 && reg.Length > 0 && Domains.Registrable(rd) != reg) h.Add(new Hint("amber", "Replies go to " + rd + ", not to the sender's domain " + dom + "."));
            // 6. first time
            if (firstTime) h.Add(new Hint("info", "First mail from this sender."));
            return h;
        }
        public static string BrandIn(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var b in Brands.Keys.OrderByDescending(k => k.Length))
                if (Regex.IsMatch(name, @"(^|[^A-Za-z])" + Regex.Escape(b) + @"($|[^A-Za-z])", RegexOptions.IgnoreCase)) return b;
            return null;
        }
        // punycode labels, and domains that are one swap away from a brand's (paypa1.com, rnicrosoft.com, arnazon.com)
        public static string Lookalike(string domain)
        {
            if (string.IsNullOrEmpty(domain)) return null;
            var d = domain.ToLowerInvariant();
            if (d.Split('.').Any(l => l.StartsWith("xn--")))
            {
                string shown = d; try { shown = new IdnMapping().GetUnicode(d); } catch { }
                return "The sender's domain uses look-alike letters from other alphabets (" + shown + " is really " + d + ").";
            }
            var reg = Domains.Registrable(d); var label = reg.Split('.')[0];
            var norm = label.Replace("rn", "m").Replace("vv", "w").Replace("0", "o").Replace("1", "l").Replace("3", "e").Replace("5", "s").Replace("|", "l");
            foreach (var kv in Brands) foreach (var real in kv.Value) if (reg == Domains.Registrable(real) || d.EndsWith("." + real)) return null;   // the brand's own domain
            foreach (var kv in Brands)
                foreach (var real in kv.Value)
                {
                    var rr = Domains.Registrable(real); var rl = rr.Split('.')[0];
                    if (reg == rr) return null;
                    if (rl.Length < 4) continue;
                    if ((norm == rl && label != rl) || (label != rl && Lev(label, rl) == 1) || (norm.StartsWith(rl) && norm.Length > rl.Length && Regex.IsMatch(norm.Substring(rl.Length), "^[-]?(secure|login|verify|support|account|service|help|billing|update|online|team|alert|check)")) || (norm.EndsWith(rl) && norm.Length > rl.Length + 1 && norm[norm.Length - rl.Length - 1] == '-'))
                        return "The sender's domain " + d + " looks like " + rr + " but isn't it.";
                }
            return null;
        }
        static int Lev(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i; for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++) for (int j = 1; j <= b.Length; j++)
                {
                    int c = a[i - 1] == b[j - 1] ? 0 : 1; d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + c);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);   // a swap counts as one
                }
            return d[a.Length, b.Length];
        }
        // "mx.example.net; spf=pass smtp.mailfrom=...; dkim=pass header.d=...; dmarc=fail" -> spf/dkim/dmarc
        public static Dictionary<string, string> Auth(string header)
        {
            var r = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(header ?? "", @"\b(spf|dkim|dmarc)\s*=\s*([a-z]+)", RegexOptions.IgnoreCase))
            {
                var k = m.Groups[1].Value.ToLowerInvariant(); var v = m.Groups[2].Value.ToLowerInvariant();
                if (!r.ContainsKey(k) || v == "fail") r[k] = v;
            }
            return r;
        }
    }

    public class AttachInfo { public string Name; public long Size; public string Type; public List<Hint> Flags = new List<Hint>(); public bool Dangerous { get { return Flags.Any(f => f.Level == "red"); } } }
    public static class Attachments
    {
        static readonly HashSet<string> Exe = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".exe", ".scr", ".com", ".pif", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ps1", ".psm1", ".msi", ".msp", ".msix", ".appx", ".hta", ".cpl",
            ".jar", ".lnk", ".reg", ".dll", ".sys", ".inf", ".scf", ".url", ".application", ".gadget", ".msc", ".chm", ".iqy", ".slk", ".xll", ".appref-ms", ".settingcontent-ms", ".library-ms", ".one" };
        static readonly HashSet<string> Macro = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm", ".potm", ".ppam", ".ppsm", ".sldm", ".xlsb" };
        static readonly HashSet<string> OldOffice = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".doc", ".xls", ".ppt", ".rtf" };
        static readonly HashSet<string> Archive = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z", ".gz", ".tar", ".cab", ".arj", ".ace", ".z", ".xz", ".bz2" };
        static readonly HashSet<string> Disk = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".iso", ".img", ".vhd", ".vhdx" };
        static readonly HashSet<string> Doc = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".jpg", ".jpeg", ".png", ".gif", ".txt", ".mp3", ".mp4", ".zip", ".ppt", ".pptx", ".csv", ".rtf" };

        public static AttachInfo Inspect(string name, byte[] data)
        {
            var a = new AttachInfo { Name = name ?? "attachment", Size = data == null ? 0 : data.Length };
            var n = (name ?? "").Trim().TrimEnd('.', ' ');
            if (n.IndexOf('‮') >= 0) a.Flags.Add(new Hint("red", "The name hides its real ending with a right-to-left character."));
            var ext = Path.GetExtension(n);
            if (Exe.Contains(ext)) a.Flags.Add(new Hint("red", "It's a program or script (" + ext + ") - it can run code on your PC."));
            if (Macro.Contains(ext)) a.Flags.Add(new Hint("red", "Office file with macros (" + ext + ") - macros are a common way in for malware."));
            if (OldOffice.Contains(ext)) a.Flags.Add(new Hint("amber", "Older Office format (" + ext + ") that can carry macros."));
            if (Disk.Contains(ext)) a.Flags.Add(new Hint("red", "A disk image (" + ext + ") - often used to slip programs past checks."));
            if (ext.Equals(".html", StringComparison.OrdinalIgnoreCase) || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".svg", StringComparison.OrdinalIgnoreCase))
                a.Flags.Add(new Hint("amber", "A web page attachment - these are often fake sign-in pages."));
            var inner = Path.GetExtension(Path.GetFileNameWithoutExtension(n));
            if (inner.Length > 1 && Doc.Contains(inner) && ext.Length > 0 && !ext.Equals(inner, StringComparison.OrdinalIgnoreCase) && (Exe.Contains(ext) || Macro.Contains(ext) || Disk.Contains(ext) || Archive.Contains(ext)))
                a.Flags.Add(new Hint("red", "Double extension: it looks like a " + inner + " but is really a " + ext + "."));
            if (Regex.IsMatch(n, @"\s{4,}\.\w+$")) a.Flags.Add(new Hint("red", "Lots of spaces before the ending hide what kind of file it really is."));
            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) && data != null)
            {
                try
                {
                    using (var z = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
                    {
                        var bad = z.Entries.Select(e => e.FullName).Where(e => Exe.Contains(Path.GetExtension(e)) || Macro.Contains(Path.GetExtension(e)) || Disk.Contains(Path.GetExtension(e))).Take(3).ToList();
                        if (bad.Count > 0) a.Flags.Add(new Hint("red", "The archive contains programs or macro files: " + string.Join(", ", bad.Select(Path.GetFileName)) + "."));
                        if (z.Entries.Any(e => Archive.Contains(Path.GetExtension(e.FullName)))) a.Flags.Add(new Hint("amber", "An archive inside an archive - a common trick to hide things."));
                    }
                }
                catch { a.Flags.Add(new Hint("amber", "The archive is password-protected or damaged, so its contents can't be checked.")); }
            }
            else if (Archive.Contains(ext)) a.Flags.Add(new Hint("amber", "Archive (" + ext + ") - its contents can't be checked here; Defender scans it before it opens."));
            return a;
        }
    }

    // Microsoft Defender's own command-line scanner (the engine Hallow Guard uses), at Idle priority, on one file
    public static class Defender
    {
        public static string Override;   // tests: a stand-in MpCmdRun
        public static string Find()
        {
            if (Override != null) return Override;
            if (MailSettings.TestDir != null) { var t = Environment.GetEnvironmentVariable("HALLOW_EMAIL_MPCMDRUN"); return string.IsNullOrEmpty(t) ? null : t; }
            try
            {
                var plat = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows Defender\Platform");
                if (Directory.Exists(plat))
                {
                    var best = Directory.GetDirectories(plat).Select(d => { Version v; return new { d, v = Version.TryParse(Path.GetFileName(d).Split('-')[0], out v) ? v : null }; })
                        .Where(x => x.v != null && File.Exists(Path.Combine(x.d, "MpCmdRun.exe"))).OrderByDescending(x => x.v).FirstOrDefault();
                    if (best != null) return Path.Combine(best.d, "MpCmdRun.exe");
                }
            }
            catch { }
            var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
            return File.Exists(pf) ? pf : null;
        }
        public static string Args(string file)
        {
            if (string.IsNullOrEmpty(file) || file.IndexOf('"') >= 0) throw new ArgumentException("bad path");
            return "-Scan -ScanType 3 -File \"" + file + "\" -DisableRemediation";
        }
        // clean = true only when Defender said exit 0 and named no threat
        public static string Verdict(int exit, string output, out bool clean)
        {
            var names = Regex.Matches(output ?? "", @"Threat\s*:\s*(.+)").Cast<Match>().Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();
            clean = false;
            if (exit == 2 || names.Count > 0) return "Microsoft Defender found something: " + (names.Count > 0 ? string.Join(", ", names) : "a threat") + ". It wasn't opened.";
            if (exit == 0) { clean = true; return "Microsoft Defender found nothing in it."; }
            return "Microsoft Defender couldn't scan it (code " + exit + "), so it wasn't opened.";
        }
        public static string Scan(string file, out bool clean)
        {
            clean = false;
            var exe = Find(); if (exe == null) return "Microsoft Defender's scanner wasn't found on this PC, so the attachment wasn't opened.";
            try
            {
                var psi = new ProcessStartInfo(exe, Args(file)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    try { p.PriorityClass = ProcessPriorityClass.Idle; } catch { }
                    var outTask = p.StandardOutput.ReadToEndAsync(); p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(300000)) { try { p.Kill(); } catch { } return "The scan took too long and was stopped - the attachment wasn't opened."; }
                    return Verdict(p.ExitCode, outTask.Result, out clean);
                }
            }
            catch (Exception e) { return "Couldn't start the scan: " + e.Message; }
        }
    }
}
