// Hallow Email - the two web services it can talk to, both only when you set them up:
//  - OAuth 2.0 (optional) for Gmail / Microsoft with a client ID YOU register (Google Cloud console / Microsoft Entra). Sign-in happens
//    in your own browser (PKCE, a one-time loopback address on 127.0.0.1); Hallow Email only keeps the refresh token, DPAPI-encrypted.
//  - Cloudflare Email Routing (optional) for masked addresses on your own domain, with an API token YOU create
//    (Zone > Email Routing Rules > Edit, and Zone > Zone > Read). The token is DPAPI-encrypted; nothing happens until it's set.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace HallowEmail
{
    public static class Http
    {
        static readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        public static Dictionary<string, object> Call(string method, string url, string bearer, string body, string contentType, out int status)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method; req.UserAgent = "HallowEmail"; req.Timeout = 20000; req.Accept = "application/json";
            if (bearer != null) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + bearer;
            if (body != null)
            {
                var b = Encoding.UTF8.GetBytes(body); req.ContentType = contentType ?? "application/json"; req.ContentLength = b.Length;
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException e) { resp = e.Response as HttpWebResponse; if (resp == null) throw; }
            using (resp)
            {
                status = (int)resp.StatusCode;
                string text; using (var r = new StreamReader(resp.GetResponseStream())) text = r.ReadToEnd();
                try { return js.Deserialize<Dictionary<string, object>>(text) ?? new Dictionary<string, object>(); } catch { return new Dictionary<string, object> { { "_raw", text } }; }
            }
        }
        public static string Form(params string[] kv) { var sb = new StringBuilder(); for (int i = 0; i + 1 < kv.Length; i += 2) { if (kv[i + 1] == null) continue; if (sb.Length > 0) sb.Append('&'); sb.Append(Uri.EscapeDataString(kv[i])).Append('=').Append(Uri.EscapeDataString(kv[i + 1])); } return sb.ToString(); }
        public static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null; }
    }

    // ---------------- OAuth
    public class OAuthProvider { public string Id, Name, AuthUrl, TokenUrl, Scope, ConsoleHelp; }
    public static class OAuth
    {
        public static string TokenOverride;   // tests: a stand-in token endpoint
        public static readonly OAuthProvider Google = new OAuthProvider { Id = "oauth-google", Name = "Google", AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth", TokenUrl = "https://oauth2.googleapis.com/token", Scope = "https://mail.google.com/",
            ConsoleHelp = "Google Cloud console > APIs & Services: create a project, set up the OAuth consent screen (External, add yourself as a test user), then Credentials > Create credentials > OAuth client ID > Desktop app. Copy the client ID (and the client secret Google shows - for desktop apps it isn't really secret, but Google asks for it)." };
        public static readonly OAuthProvider Microsoft = new OAuthProvider { Id = "oauth-microsoft", Name = "Microsoft", AuthUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize", TokenUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token", Scope = "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send offline_access",
            ConsoleHelp = "Microsoft Entra admin center (entra.microsoft.com) > App registrations > New registration: any name, \"Personal Microsoft accounts and accounts in any organizational directory\", Redirect URI type \"Public client/native\" = http://localhost. Then API permissions > Add a permission > Microsoft Graph > Delegated: IMAP.AccessAsUser.All, SMTP.Send and offline_access. Copy the Application (client) ID." };
        public static OAuthProvider For(string id) { return id == Google.Id ? Google : id == Microsoft.Id ? Microsoft : null; }

        public static string Verifier() { var b = new byte[32]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); return B64Url(b); }
        public static string Challenge(string verifier) { using (var s = SHA256.Create()) return B64Url(s.ComputeHash(Encoding.ASCII.GetBytes(verifier))); }
        static string B64Url(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        public static string AuthorizeUrl(OAuthProvider p, string clientId, string redirect, string state, string challenge, string loginHint)
        {
            var q = Http.Form("client_id", clientId, "redirect_uri", redirect, "response_type", "code", "scope", p.Scope, "state", state, "code_challenge", challenge, "code_challenge_method", "S256",
                "access_type", p == Google ? "offline" : null, "prompt", p == Google ? "consent" : "select_account", "login_hint", loginHint);
            return p.AuthUrl + "?" + q;
        }
        public class Tokens { public string Access, Refresh; public DateTime Expires; public string Error; }
        static Tokens Parse(Dictionary<string, object> d, int status)
        {
            var t = new Tokens { Access = Http.Str(d, "access_token"), Refresh = Http.Str(d, "refresh_token") };
            int sec; t.Expires = DateTime.UtcNow.AddSeconds(int.TryParse(Http.Str(d, "expires_in"), out sec) ? Math.Max(60, sec - 60) : 3000);
            if (t.Access == null) t.Error = (Http.Str(d, "error_description") ?? Http.Str(d, "error") ?? ("the provider answered " + status)).Split('\n')[0];
            return t;
        }
        public static Tokens Exchange(OAuthProvider p, string clientId, string clientSecret, string code, string verifier, string redirect)
        {
            int st; var d = Http.Call("POST", TokenOverride ?? p.TokenUrl, null, Http.Form("grant_type", "authorization_code", "code", code, "redirect_uri", redirect, "client_id", clientId, "client_secret", string.IsNullOrEmpty(clientSecret) ? null : clientSecret, "code_verifier", verifier), "application/x-www-form-urlencoded", out st);
            return Parse(d, st);
        }
        public static Tokens Refresh(OAuthProvider p, string clientId, string clientSecret, string refresh)
        {
            int st; var d = Http.Call("POST", TokenOverride ?? p.TokenUrl, null, Http.Form("grant_type", "refresh_token", "refresh_token", refresh, "client_id", clientId, "client_secret", string.IsNullOrEmpty(clientSecret) ? null : clientSecret, "scope", p == Microsoft ? p.Scope : null), "application/x-www-form-urlencoded", out st);
            var t = Parse(d, st); if (t.Refresh == null) t.Refresh = refresh; return t;
        }
        // the whole sign-in: opens the browser, waits (max 5 min) for the provider to come back to 127.0.0.1, swaps the code for tokens
        public static Tokens SignIn(OAuthProvider p, string clientId, string clientSecret, string email, Action<string> openBrowser, CancellationToken ct)
        {
            int port; var l0 = new TcpListener(IPAddress.Loopback, 0); l0.Start(); port = ((IPEndPoint)l0.LocalEndpoint).Port; l0.Stop();
            var redirect = "http://127.0.0.1:" + port + "/";
            var verifier = Verifier(); var state = Verifier().Substring(0, 16);
            using (var http = new HttpListener())
            {
                http.Prefixes.Add(redirect); http.Start();
                openBrowser(AuthorizeUrl(p, clientId, redirect, state, Challenge(verifier), email));
                var task = http.GetContextAsync();
                var deadline = DateTime.UtcNow.AddMinutes(5);
                while (!task.Wait(500)) { if (ct.IsCancellationRequested || DateTime.UtcNow > deadline) return new Tokens { Error = "Sign-in was cancelled or took too long." }; }
                var ctx = task.Result; var q = ctx.Request.QueryString;
                var page = Encoding.UTF8.GetBytes("<!DOCTYPE html><meta charset=utf-8><body style=\"font:16px Segoe UI;padding:40px\"><b>Hallow Email</b><p>" + (q["code"] != null && q["state"] == state ? "Signed in. You can close this tab." : "Sign-in didn't finish. You can close this tab and try again.") + "</p>");
                ctx.Response.ContentType = "text/html; charset=utf-8"; ctx.Response.OutputStream.Write(page, 0, page.Length); ctx.Response.Close();
                if (q["state"] != state) return new Tokens { Error = "The answer didn't match this sign-in (state)." };
                if (q["code"] == null) return new Tokens { Error = q["error_description"] ?? q["error"] ?? "No code came back." };
                return Exchange(p, clientId, clientSecret, q["code"], verifier, redirect);
            }
        }
    }

    // ---------------- Cloudflare Email Routing (masked addresses)
    public static class Cloudflare
    {
        public static string ApiOverride;   // tests: a stand-in API
        static string Api { get { return (ApiOverride ?? "https://api.cloudflare.com/client/v4").TrimEnd('/'); } }
        static string Err(Dictionary<string, object> d, int st)
        {
            object e; if (d.TryGetValue("errors", out e) && e is System.Collections.IEnumerable)
                foreach (var x in ((System.Collections.IEnumerable)e).OfType<Dictionary<string, object>>()) { var m = Http.Str(x, "message"); if (m != null) return "Cloudflare: " + m; }
            return "Cloudflare answered " + st + ".";
        }
        static bool Ok(Dictionary<string, object> d, int st) { object s; return st >= 200 && st < 300 && d.TryGetValue("success", out s) && s is bool && (bool)s; }
        public static string ZoneId(string token, string domain, out string error)
        {
            error = null; int st;
            try
            {
                var d = Http.Call("GET", Api + "/zones?name=" + Uri.EscapeDataString(domain), token, null, null, out st);
                if (!Ok(d, st)) { error = Err(d, st); return null; }
                var res = d["result"] as System.Collections.ArrayList;
                if (res == null || res.Count == 0) { error = "Cloudflare doesn't have a zone called " + domain + " for this token."; return null; }
                return Http.Str((Dictionary<string, object>)res[0], "id");
            }
            catch (Exception e) { error = "Couldn't reach Cloudflare: " + e.Message; return null; }
        }
        // a new random address on the domain that forwards to your inbox
        public static string NewAlias(string domain, string hint)
        {
            var b = new byte[4]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            var word = new string((hint ?? "").ToLowerInvariant().Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')).Take(12).ToArray());
            return (word.Length > 0 ? word + "." : "m.") + BitConverter.ToString(b).Replace("-", "").ToLowerInvariant() + "@" + domain;
        }
        public static string CreateRule(string token, string zoneId, string alias, string forwardTo, string note, out string error)
        {
            error = null; int st;
            try
            {
                var body = new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                    { "name", "Hallow Email masked: " + (note ?? alias) }, { "enabled", true },
                    { "matchers", new[] { new Dictionary<string, object> { { "type", "literal" }, { "field", "to" }, { "value", alias } } } },
                    { "actions", new[] { new Dictionary<string, object> { { "type", "forward" }, { "value", new[] { forwardTo } } } } } });
                var d = Http.Call("POST", Api + "/zones/" + zoneId + "/email/routing/rules", token, body, "application/json", out st);
                if (!Ok(d, st)) { error = Err(d, st); return null; }
                var r = d["result"] as Dictionary<string, object>; return Http.Str(r, "id") ?? Http.Str(r, "tag");
            }
            catch (Exception e) { error = "Couldn't reach Cloudflare: " + e.Message; return null; }
        }
        public static string DeleteRule(string token, string zoneId, string ruleId)
        {
            int st;
            try { var d = Http.Call("DELETE", Api + "/zones/" + zoneId + "/email/routing/rules/" + ruleId, token, null, null, out st); return Ok(d, st) ? null : Err(d, st); }
            catch (Exception e) { return "Couldn't reach Cloudflare: " + e.Message; }
        }
    }
}
