// Hallow Email - privacy for HTML mail.
// Every HTML body is rebuilt token by token (MimeKit's HTML tokenizer) before it is shown:
//  - no scripts, frames, objects, forms, <base>, <meta refresh>, <link>, event handlers or javascript: links;
//  - remote images are blocked by default ("Load images from this sender" allows them for that sender). Tracking pixels stay blocked
//    even then: 1x1 / tiny images, known tracking domains (Hallow Shield's lists when they're on this PC, plus a short built-in list) and
//    image URLs carrying a unique ID;
//  - CSS can't fetch anything (url(...) / @import removed unless it's an inline data: image);
//  - every link gets a tooltip with its real destination, and a visible warning when the link text names a different site;
//  - a Content-Security-Policy on top, and the reader (WebView2) has scripts off and refuses any request we didn't allow.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using MimeKit.Text;

namespace HallowEmail
{
    public class LinkCheck { public string Text; public string Href; public string Host; public string TextHost; public bool Mismatch; }
    public class CleanHtml
    {
        public string Html; public int RemoteImages; public int Trackers; public int Blocked;
        public List<string> TrackerHosts = new List<string>(); public List<LinkCheck> Links = new List<LinkCheck>();
        public List<LinkCheck> Mismatches { get { return Links.Where(l => l.Mismatch).ToList(); } }
    }

    public static class Trackers
    {
        // a short built-in list of well-known mail-tracking / analytics hosts (used with or without Hallow Shield's lists)
        static readonly string[] BuiltIn = {
            "doubleclick.net", "google-analytics.com", "googletagmanager.com", "mailtrack.io", "mailtrack.com", "yesware.com", "getnotify.com", "bananatag.com",
            "mixmax.com", "streak.com", "cirrusinsight.com", "hubspot.com", "hs-analytics.net", "hubspotlinks.com", "hubspotemail.net", "list-manage.com",
            "mcsv.net", "mailchimp.com", "sendgrid.net", "mandrillapp.com", "mailgun.org", "sparkpostmail.com", "exacttarget.com", "returnpath.net",
            "litmus.com", "emltrk.com", "pardot.com", "marketo.com", "mktoweb.com", "eloqua.com", "en25.com", "klaviyo.com", "klclick.com",
            "constantcontact.com", "rs6.net", "createsend1.com", "cmail19.com", "cmail20.com", "responsys.net", "bronto.com", "sailthru.com",
            "omnisend.com", "convertkit-mail.com", "ck.page", "mailjet.com", "sendinblue.com", "brevo.com", "awstrack.me", "pixel.watch", "trk.email",
            "facebook.com", "connect.facebook.net", "pixel.facebook.com", "bat.bing.com", "ads.linkedin.com", "px.ads.linkedin.com", "t.co", "analytics.twitter.com" };
        static HashSet<string> set; static string source = "built-in";
        public static string Source { get { Load(); return source; } }
        public static int Count { get { Load(); return set.Count; } }
        public static string ShieldRulesDir;   // tests point this at fixture rules
        static readonly object l = new object();
        public static void Reset() { lock (l) { set = null; bad = null; } }
        static void Load()
        {
            lock (l)
            {
                if (set != null) return;
                var s = new HashSet<string>(BuiltIn, StringComparer.OrdinalIgnoreCase);
                int n = 0;
                foreach (var dir in Candidates())
                {
                    try
                    {
                        if (!Directory.Exists(dir)) continue;
                        foreach (var f in new[] { "easyprivacy.json", "plowe.json", "ublock-privacy.json", "ublock-badware.json" })
                        {
                            var p = Path.Combine(dir, f); if (!File.Exists(p) || new FileInfo(p).Length > 40 * 1024 * 1024) continue;
                            n += ReadShield(File.ReadAllText(p), s);
                        }
                        if (n > 0) { source = "Hallow Shield's lists + built-in"; break; }
                    }
                    catch { }
                }
                set = s;
            }
        }
        static IEnumerable<string> Candidates()
        {
            if (ShieldRulesDir != null) { yield return ShieldRulesDir; yield break; }
            if (Snap.On || MailSettings.TestDir != null) yield break;
            var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(la, @"Programs\Hallow Browser\shield\rules");
            yield return Path.Combine(la, @"Programs\Hallow Shield\rules");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"HallowShield\extension\rules");
        }
        // Shield's rules are Chrome declarativeNetRequest JSON: block rules with "requestDomains" or a plain "||host^" urlFilter
        public static int ReadShield(string json, HashSet<string> into)
        {
            int n = 0;
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var rules = js.Deserialize<object[]>(json);
            foreach (var r in rules.OfType<Dictionary<string, object>>())
            {
                object a, c, t; var act = r.TryGetValue("action", out a) ? a as Dictionary<string, object> : null;
                if (act == null || !act.TryGetValue("type", out t) || (t as string) != "block") continue;
                var cond = r.TryGetValue("condition", out c) ? c as Dictionary<string, object> : null; if (cond == null) continue;
                object rd, uf;
                if (cond.TryGetValue("requestDomains", out rd) && rd is System.Collections.IEnumerable)
                    foreach (var d in ((System.Collections.IEnumerable)rd).OfType<string>()) if (into.Add(d.ToLowerInvariant())) n++;
                if (cond.TryGetValue("urlFilter", out uf) && uf is string)
                {
                    var m = Regex.Match((string)uf, @"^\|\|([a-z0-9.-]+\.[a-z]{2,})\^$", RegexOptions.IgnoreCase);
                    if (m.Success && into.Add(m.Groups[1].Value.ToLowerInvariant())) n++;
                }
            }
            return n;
        }
        // Hallow Shield's badware list (ublock-badware.json) when it's on this PC: hosts known for scams / malware
        static HashSet<string> bad;
        public static bool IsBadware(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            lock (l)
            {
                if (bad == null)
                {
                    bad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var dir in Candidates()) try { var p = Path.Combine(dir, "ublock-badware.json"); if (File.Exists(p)) { ReadShield(File.ReadAllText(p), bad); if (bad.Count > 0) break; } } catch { }
                }
            }
            var h = host.ToLowerInvariant().TrimEnd('.');
            while (true) { if (bad.Contains(h)) return true; var i = h.IndexOf('.'); if (i < 0 || h.IndexOf('.', i + 1) < 0) return false; h = h.Substring(i + 1); }
        }
        public static bool IsTracker(string host)
        {
            if (string.IsNullOrEmpty(host)) return false; Load();
            var h = host.ToLowerInvariant().TrimEnd('.');
            while (true) { if (set.Contains(h)) return true; var i = h.IndexOf('.'); if (i < 0 || h.IndexOf('.', i + 1) < 0) return false; h = h.Substring(i + 1); }
        }
        // a URL that carries a unique ID: a long hex / base64-ish value in the query or path, or the typical pixel names
        public static bool HasUniqueId(string url)
        {
            Uri u; if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            foreach (var part in (u.Query.TrimStart('?')).Split('&'))
            {
                var kv = part.Split(new[] { '=' }, 2); if (kv.Length < 2) continue;
                var v = Uri.UnescapeDataString(kv[1]);
                if (Regex.IsMatch(v, @"^[A-Za-z0-9_\-+/=.%]{20,}$") && Regex.IsMatch(v, @"\d") && Regex.IsMatch(v, "[A-Za-z]")) return true;
                if (Regex.IsMatch(v, @"^[0-9a-f]{16,}$", RegexOptions.IgnoreCase)) return true;
                if (Regex.IsMatch(v, @"^[0-9a-f]{8}-[0-9a-f]{4}-", RegexOptions.IgnoreCase)) return true;
            }
            var path = u.AbsolutePath.ToLowerInvariant();
            if (Regex.IsMatch(path, @"/(open|pixel|track|tracking|beacon|wf/open|o)(\.gif|\.png|\.php|/)?") && Regex.IsMatch(path, @"[0-9a-z_\-]{20,}")) return true;
            if (Regex.IsMatch(path, @"/[0-9a-f]{24,}(/|\.gif|\.png|$)")) return true;
            return false;
        }
    }

    public static class Privacy
    {
        static readonly HashSet<string> DropWithContent = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "script", "iframe", "frame", "frameset", "object", "embed", "applet", "svg", "math", "audio", "video", "template", "noembed", "title", "head-script", "canvas" };
        static readonly HashSet<string> DropTagOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "html", "head", "body", "form", "input", "button", "select", "option", "textarea", "base", "meta", "link", "noscript", "source", "track", "param", "portal", "dialog" };
        static readonly HashSet<string> UrlAttrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "href", "src", "background", "action", "formaction", "poster", "longdesc", "lowsrc", "dynsrc", "cite", "data", "srcset", "ping", "xlink:href" };

        public static CleanHtml Sanitize(string html, bool allowRemote, Func<string, string> cid, bool dark = false)
        {
            var r = new CleanHtml(); var sb = new StringBuilder();
            var tok = new HtmlTokenizer(new StringReader(html ?? "")) { DecodeCharacterReferences = true };
            HtmlToken t; string skip = null; int skipDepth = 0; bool inStyle = false;
            LinkCheck link = null; var linkText = new StringBuilder();
            while (tok.ReadNextToken(out t))
            {
                if (t.Kind == HtmlTokenKind.Tag)
                {
                    var tag = (HtmlTagToken)t; var name = tag.Name.ToLowerInvariant();
                    if (skip != null)
                    {
                        if (name == skip) { if (tag.IsEndTag) { if (--skipDepth <= 0) skip = null; } else if (!tag.IsEmptyElement) skipDepth++; }
                        continue;
                    }
                    if (DropWithContent.Contains(name)) { if (!tag.IsEndTag && !tag.IsEmptyElement && !Void(name)) { skip = name; skipDepth = 1; } r.Blocked++; continue; }
                    if (DropTagOnly.Contains(name)) continue;
                    if (name == "style") { inStyle = !tag.IsEndTag && !tag.IsEmptyElement; sb.Append(tag.IsEndTag ? "</style>" : "<style>"); continue; }
                    if (tag.IsEndTag)
                    {
                        sb.Append("</").Append(name).Append('>');
                        if (name == "a" && link != null) { FinishLink(link, linkText.ToString(), r, sb); link = null; }
                        continue;
                    }
                    if (!Regex.IsMatch(name, "^[a-z][a-z0-9]*$")) continue;
                    sb.Append('<').Append(name);
                    string src = null; int w = -1, h = -1; string style = null;
                    foreach (var a in tag.Attributes)
                    {
                        var an = a.Name.ToLowerInvariant(); var av = a.Value ?? "";
                        if (an == "width") int.TryParse(Regex.Match(av, @"\d+").Value, out w);
                        if (an == "height") int.TryParse(Regex.Match(av, @"\d+").Value, out h);
                    }
                    foreach (var a in tag.Attributes)
                    {
                        var an = a.Name.ToLowerInvariant(); var av = a.Value ?? "";
                        if (an.StartsWith("on") || an == "srcdoc" || an == "formaction" || an == "action" || an == "ping" || an == "srcset" || an == "integrity" || an == "http-equiv" || !Regex.IsMatch(an, "^[a-z][a-z0-9_:-]*$")) continue;
                        if (an == "style") { style = av; av = CleanCss(av, r); if (av.Length == 0) continue; }
                        else if (name == "img" && an == "src") { src = av; continue; }
                        else if (an == "href" && name == "a")
                        {
                            var href = av.Trim();
                            if (!SafeLink(href)) continue;
                            Uri u; string host = Uri.TryCreate(href, UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https") ? u.Host : null;
                            link = new LinkCheck { Href = href, Host = host }; linkText.Clear();
                            sb.Append(" href=").Append(Attr(href));
                            if (host != null) sb.Append(" title=").Append(Attr("Goes to: " + host));
                            else if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) sb.Append(" title=").Append(Attr(href));
                            continue;
                        }
                        else if (an == "title" && name == "a") continue;   // our own title says where it goes
                        else if (UrlAttrs.Contains(an)) { if (an == "background" && av.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) { } else continue; }
                        sb.Append(' ').Append(an).Append('=').Append(Attr(av));
                    }
                    if (name == "img")
                    {
                        var s = ImageSrc(src, w, h, style, allowRemote, cid, r);
                        if (s != null) sb.Append(" src=").Append(Attr(s));
                        else sb.Append(" data-hallow-blocked=\"1\"");
                    }
                    sb.Append(tag.IsEmptyElement || Void(name) ? " />" : ">");
                    if (name == "a" && (tag.IsEmptyElement)) link = null;
                }
                else if (t.Kind == HtmlTokenKind.Data || t.Kind == HtmlTokenKind.CData)
                {
                    if (skip != null) continue;
                    var d = t.Kind == HtmlTokenKind.Data ? ((HtmlDataToken)t).Data : ((HtmlCDataToken)t).Data;
                    if (inStyle) { sb.Append(CleanCss(d, r).Replace("</", "")); continue; }
                    if (link != null) linkText.Append(d);
                    sb.Append(HtmlUtils.HtmlEncode(d));
                }
                // comments, doctypes and script data are dropped
            }
            r.Html = Wrap(sb.ToString(), allowRemote, dark);
            return r;
        }
        static bool Void(string n) { return n == "img" || n == "br" || n == "hr" || n == "col" || n == "area" || n == "wbr"; }
        static string Attr(string s) { return HtmlUtils.HtmlAttributeEncode(s ?? ""); }
        public static bool SafeLink(string href)
        {
            if (string.IsNullOrEmpty(href)) return false;
            if (href.StartsWith("#")) return true;
            Uri u; if (!Uri.TryCreate(href, UriKind.Absolute, out u)) return false;
            return u.Scheme == "http" || u.Scheme == "https" || u.Scheme == "mailto";
        }
        static void FinishLink(LinkCheck l, string text, CleanHtml r, StringBuilder sb)
        {
            l.Text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            if (l.Host != null)
            {
                var th = TextHost(l.Text);
                if (th != null) { l.TextHost = th; l.Mismatch = !SameSite(th, l.Host); }
                if (l.Mismatch) sb.Append("<span style=\"background:#FFE9C7;color:#6B3A00;border:1px solid #C27A00;border-radius:3px;padding:0 4px;margin-left:3px;font:600 11px Segoe UI,sans-serif\">&#9888; really goes to ").Append(HtmlUtils.HtmlEncode(l.Host)).Append("</span>");
            }
            r.Links.Add(l);
        }
        // the site a link's TEXT names ("www.paypal.com/login", "https://bank.example"), or null if the text isn't an address
        public static string TextHost(string text)
        {
            var m = Regex.Match(text ?? "", @"^(?:https?://)?((?:[a-z0-9-]+\.)+[a-z]{2,})(?:[:/?#]\S*)?$", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }
        public static bool SameSite(string a, string b) { return Domains.Registrable(a) == Domains.Registrable(b); }

        static string ImageSrc(string src, int w, int h, string style, bool allowRemote, Func<string, string> cid, CleanHtml r)
        {
            if (string.IsNullOrEmpty(src)) return null;
            src = src.Trim();
            if (src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && !src.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase)) return src;
            if (src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)) return cid == null ? null : cid(src.Substring(4));
            Uri u; if (!Uri.TryCreate(src, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) return null;
            r.RemoteImages++;
            if (IsTracker(src, w, h, style)) { r.Trackers++; if (!r.TrackerHosts.Contains(u.Host)) r.TrackerHosts.Add(u.Host); r.Blocked++; return null; }
            if (!allowRemote) { r.Blocked++; return null; }
            return u.Scheme == "http" ? null : src;   // remote images only over https, even when allowed
        }
        public static bool IsTracker(string url, int w, int h, string style)
        {
            if ((w >= 0 && w <= 2) || (h >= 0 && h <= 2)) return true;
            if (style != null && Regex.IsMatch(style, @"(width|height)\s*:\s*[0-2](\.\d+)?px|display\s*:\s*none|visibility\s*:\s*hidden|opacity\s*:\s*0(\.0+)?\s*(;|$)", RegexOptions.IgnoreCase)) return true;
            Uri u; if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            return Trackers.IsTracker(u.Host) || Trackers.HasUniqueId(url);
        }
        static string CleanCss(string css, CleanHtml r)
        {
            var c = css ?? "";
            c = Regex.Replace(c, @"@import[^;]*;?", "", RegexOptions.IgnoreCase);
            c = Regex.Replace(c, @"url\s*\(\s*(['""]?)(?!data:image/(png|gif|jpe?g|webp))[^)]*\)", m => { r.Blocked++; return "none"; }, RegexOptions.IgnoreCase);
            c = Regex.Replace(c, @"expression\s*\(|behavior\s*:|-moz-binding|javascript:", "", RegexOptions.IgnoreCase);
            c = Regex.Replace(c, @"position\s*:\s*fixed", "position:static", RegexOptions.IgnoreCase);
            return c.Trim();
        }
        static string Wrap(string body, bool allowRemote, bool dark)
        {
            var csp = "default-src 'none'; img-src data:" + (allowRemote ? " https:" : "") + "; style-src 'unsafe-inline'; font-src data:; form-action 'none'; frame-src 'none'; base-uri 'none'";
            return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"" + csp + "\">" +
                "<meta name=\"referrer\" content=\"no-referrer\"><style>html{background:" + (dark ? "#1C1726" : "#F6F4FA") + "}body{margin:0;padding:18px;background:#FFFFFF;color:#1E1A26;font:14px/1.5 'Segoe UI',sans-serif;word-wrap:break-word;border-radius:4px}" +
                "img[data-hallow-blocked]{display:inline-block;min-width:16px;min-height:16px;background:repeating-linear-gradient(45deg,#EEEAF5,#EEEAF5 6px,#E2DCEC 6px,#E2DCEC 12px);border:1px dashed #C8BFD8}" +
                "a{color:#5B3FA8}pre{white-space:pre-wrap}</style></head><body>" + body + "</body></html>";
        }

        // plain text mail as safe HTML (links clickable, nothing remote)
        public static string TextToHtml(string text, bool dark)
        {
            var enc = HtmlUtils.HtmlEncode(text ?? "");
            enc = Regex.Replace(enc, @"https?://[^\s<>&""]+", m => "<a href=\"" + m.Value + "\" title=\"Goes to: " + HostOf(m.Value) + "\">" + m.Value + "</a>");
            return Wrap("<pre style=\"font:14px/1.5 'Segoe UI',sans-serif;margin:0\">" + enc + "</pre>", false, dark);
        }
        public static string HostOf(string url) { Uri u; return Uri.TryCreate(url, UriKind.Absolute, out u) ? u.Host : ""; }
        public static string ToText(string html)
        {
            try { return Regex.Replace(new HtmlToText().Convert(html ?? ""), @"(?m)^#{1,6} ", ""); } catch { return Regex.Replace(html ?? "", "<[^>]+>", " "); }
        }
    }

    public static class Domains
    {
        static readonly HashSet<string> Second = new HashSet<string> { "co", "com", "org", "net", "gov", "ac", "edu", "ne", "or", "go", "gob", "nhs", "ltd", "plc" };
        public static string Registrable(string host)
        {
            var h = (host ?? "").ToLowerInvariant().Trim().TrimEnd('.');
            var p = h.Split('.'); if (p.Length <= 2) return h;
            if (p[p.Length - 1].Length == 2 && Second.Contains(p[p.Length - 2])) return string.Join(".", p.Skip(p.Length - 3));
            return string.Join(".", p.Skip(p.Length - 2));
        }
        public static string Of(string addr) { var a = addr ?? ""; var i = a.LastIndexOf('@'); return i < 0 ? "" : a.Substring(i + 1).ToLowerInvariant().Trim('>', ' '); }
    }
}
