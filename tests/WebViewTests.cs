// Hallow Email tests - the reader's WebView2 sandbox, in a window placed far off-screen (never visible, no taskbar button, never
// activated). Loads a hostile test page and checks: page scripts don't run, remote requests never reach a local stand-in server, and a
// link click is handed to Hallow Email (asks first) instead of navigating. Skipped (and said so) when the WebView2 Runtime is missing.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HallowEmail.Tests
{
    public static class WebViewTests
    {
        static bool Pump(Func<bool> done, int ms)
        {
            var end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end)
            {
                if (done()) return true;
                var f = new DispatcherFrame(); var t = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Background, (s, e) => f.Continue = false, Dispatcher.CurrentDispatcher); t.Start();
                Dispatcher.PushFrame(f); t.Stop();
            }
            return done();
        }
        public static void Run(Action<bool, string> ok, string outDir)
        {
            if (!Reader.WebView2Available()) { ok(true, "webview2: runtime not found - sandbox test skipped"); return; }
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var http = new FakeHttp(); http.Answer = (m, p, a, b) => Tuple.Create(200, "{}");
            var win = new Window { Left = -32000, Top = -32000, Width = 500, Height = 400, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Topmost = false };
            var web = new WebView2(); win.Content = web; string link = null; CoreWebView2Environment env = null; Exception err = null; bool ready = false;
            try
            {
                win.Show();
                CoreWebView2Environment.CreateAsync(null, Path.Combine(outDir, "wv2"), new CoreWebView2EnvironmentOptions("--disable-background-networking --no-pings")).ContinueWith(t =>
                {
                    if (t.Exception != null) { err = t.Exception.InnerException; return; }
                    env = t.Result;
                }, TaskScheduler.FromCurrentSynchronizationContext());
                Pump(() => env != null || err != null, 30000);
                if (env == null) { ok(false, "webview2: environment " + (err == null ? "timed out" : err.Message)); return; }
                web.EnsureCoreWebView2Async(env).ContinueWith(t => { if (t.Exception != null) err = t.Exception.InnerException; else ready = true; }, TaskScheduler.FromCurrentSynchronizationContext());
                Pump(() => ready || err != null, 30000);
                if (!ready) { ok(false, "webview2: init " + (err == null ? "timed out" : err.Message)); return; }
                Reader.Lockdown(web.CoreWebView2, env, () => false, u => link = u);
                bool loaded = false; web.CoreWebView2.NavigationCompleted += (s, e) => loaded = true;
                // a hostile page WITHOUT our cleaning or CSP, so the WebView2 rules alone are tested
                var html = "<html><head><title>start</title><link rel=stylesheet href=\"" + http.Url + "css\"></head><body><script>document.title='script-ran'; fetch('" + http.Url + "fetch');</script>" +
                    "<img src=\"" + http.Url + "img.png\"><iframe src=\"" + http.Url + "frame\"></iframe><a id=l href=\"https://example.com/somewhere\">a link</a></body></html>";
                web.CoreWebView2.NavigateToString(html);
                ok(Pump(() => loaded, 15000), "webview2: test page loaded off-screen");
                Pump(() => false, 1500);
                string title = null; web.CoreWebView2.ExecuteScriptAsync("document.title").ContinueWith(t => title = t.Exception == null ? t.Result : "error", TaskScheduler.FromCurrentSynchronizationContext());
                Pump(() => title != null, 5000);
                ok(title == "\"start\"", "webview2: page scripts don't run (title " + title + ")");
                ok(http.Requests.Count == 0, "webview2: no request reached the stand-in server (image, stylesheet, frame, fetch) - " + http.Requests.Count);
                string clicked = null; web.CoreWebView2.ExecuteScriptAsync("document.getElementById('l').click(); 'x'").ContinueWith(t => clicked = "y", TaskScheduler.FromCurrentSynchronizationContext());
                Pump(() => link != null, 5000);
                ok(link == "https://example.com/somewhere", "webview2: a link click is handed to Hallow Email (asks before opening the browser), not opened inside");
                string still = null; web.CoreWebView2.ExecuteScriptAsync("document.title").ContinueWith(t => still = t.Exception == null ? t.Result : "error", TaskScheduler.FromCurrentSynchronizationContext());
                Pump(() => still != null, 5000);
                ok(still == "\"start\"" && http.Requests.Count == 0, "webview2: still on the mail after the click");
            }
            catch (Exception e) { ok(false, "webview2: " + e.Message); }
            finally { try { web.Dispose(); } catch { } try { win.Close(); } catch { } http.Stop(); }
        }
    }
}
