// Hallow Email - the small themed dialogs (built in code; --snap renders them without showing them) and the crash notice.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace HallowEmail
{
    public static class Dialogs
    {
        public static Window Make(string title, double width, Window owner)
        {
            var w = new Window(); Ui.Dress(w, title, width);
            w.ResizeMode = ResizeMode.NoResize; w.ShowInTaskbar = false;
            if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; } else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return w;
        }
        static StackPanel Body(Window w) { var sp = new StackPanel { Margin = new Thickness(22, 18, 22, 18) }; w.Content = sp; return sp; }
        static WrapPanel Buttons(StackPanel sp) { var row = new WrapPanel { Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right }; sp.Children.Add(row); return row; }

        public static Window ConfirmWindow(string title, string text, string okLabel, bool danger, Action<bool> done, Window owner = null)
        {
            var w = Make("Hallow Email", 480, owner); var sp = Body(w);
            sp.Children.Add(Ui.T(title, 15, true, null, 6)); if (!string.IsNullOrEmpty(text)) sp.Children.Add(Ui.Muted(text, 12.5));
            var row = Buttons(sp); bool answer = false;
            row.Children.Add(Ui.Btn("Cancel", () => w.Close()));
            var ok = danger ? Ui.Danger(okLabel, () => { answer = true; w.Close(); }) : Ui.Btn(okLabel, () => { answer = true; w.Close(); }, true); ok.Margin = new Thickness(0); ok.MinWidth = 90;
            row.Children.Add(ok);
            w.Closed += (s, e) => { if (done != null) done(answer); };
            w.Loaded += (s, e) => ((Button)row.Children[0]).Focus();   // Cancel has the focus: Enter never sends or deletes by accident
            return w;
        }
        public static Func<string, bool> AutoAnswer;   // tests
        public static bool Confirm(Window owner, string title, string text, string okLabel, bool danger)
        {
            if (AutoAnswer != null) return AutoAnswer(title);
            bool r = false; var w = ConfirmWindow(title, text, okLabel, danger, a => r = a, owner); w.ShowDialog(); return r;
        }
        public static Window InfoWindow(string title, string text, Action done, Window owner = null)
        {
            var w = Make("Hallow Email", 520, owner); var sp = Body(w);
            sp.Children.Add(Ui.T(title, 15, true, null, 6));
            var tb = new TextBox { Text = text ?? "", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Ui.R("Bg"), Foreground = Ui.R("Muted"), MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 12.5 };
            sp.Children.Add(tb);
            var row = Buttons(sp); var ok = Ui.Btn("OK", () => w.Close(), true); ok.Margin = new Thickness(0); ok.MinWidth = 80; row.Children.Add(ok);
            w.Closed += (s, e) => { if (done != null) done(); };
            return w;
        }
        public static void Info(Window owner, string title, string text) { if (AutoAnswer != null) return; InfoWindow(title, text, null, owner).ShowDialog(); }
        // Save / Discard / Cancel
        public static bool Three(Window owner, string title, string text, string yes, string no, out MessageBoxResult r)
        {
            var w = Make("Hallow Email", 460, owner); var sp = Body(w);
            sp.Children.Add(Ui.T(title, 15, true, null, 6)); sp.Children.Add(Ui.Muted(text, 12.5));
            var row = Buttons(sp); var res = MessageBoxResult.Cancel;
            row.Children.Add(Ui.Btn("Cancel", () => w.Close()));
            row.Children.Add(Ui.Btn(no, () => { res = MessageBoxResult.No; w.Close(); }));
            var y = Ui.Btn(yes, () => { res = MessageBoxResult.Yes; w.Close(); }, true); y.Margin = new Thickness(0); row.Children.Add(y);
            w.ShowDialog(); r = res; return res == MessageBoxResult.Yes;
        }
        public static string Ask(Window owner, string title, string text, string value)
        {
            var w = Make("Hallow Email", 440, owner); var sp = Body(w);
            sp.Children.Add(Ui.T(title, 15, true, null, 6)); if (!string.IsNullOrEmpty(text)) sp.Children.Add(Ui.Muted(text, 12, 10));
            var f = Ui.Field(value); sp.Children.Add(f); string r = null;
            var row = Buttons(sp); row.Children.Add(Ui.Btn("Cancel", () => w.Close()));
            var ok = Ui.Btn("OK", () => { r = f.Text; w.Close(); }, true); ok.Margin = new Thickness(0); ok.IsDefault = true; row.Children.Add(ok);
            w.Loaded += (s, e) => f.Focus(); w.ShowDialog(); return r;
        }
        public static string PickFolder(Window owner, Controller c, string account)
        {
            var w = Make("Hallow Email", 380, owner); var sp = Body(w);
            sp.Children.Add(Ui.T("Move to which folder?", 15, true, null, 8));
            string pick = null; List<FolderInfo> fs; var idx = c.Store.Index(account); lock (c.Store.Lock) fs = idx.Folders.ToList();
            var list = new StackPanel();
            foreach (var f in fs.OrderBy(x => x.Name)) { var ff = f; var b = new Button { Style = Ui.S("Btn"), Content = f.Name, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) }; b.Click += (s, e) => { pick = ff.Name; w.Close(); }; list.Children.Add(b); }
            sp.Children.Add(new ScrollViewer { Content = list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var row = Buttons(sp); var cn = Ui.Btn("Cancel", () => w.Close()); cn.Margin = new Thickness(0); row.Children.Add(cn);
            w.ShowDialog(); return pick;
        }
        public static Window TimeWindow(string title, string text, Action<DateTime?> done, DateTime now)
        {
            var w = Make("Hallow Email", 440, null); var sp = Body(w);
            sp.Children.Add(Ui.T(title, 15, true, null, 6)); sp.Children.Add(Ui.Muted(text, 12, 10));
            DateTime? pick = null;
            int toMon = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7; if (toMon == 0) toMon = 7;
            foreach (var o in new[] { Tuple.Create("In 1 hour", now.AddHours(1)), Tuple.Create("Tomorrow morning", now.Date.AddDays(1).AddHours(8)), Tuple.Create("Tomorrow afternoon", now.Date.AddDays(1).AddHours(13)), Tuple.Create("Monday morning", now.Date.AddDays(toMon).AddHours(8)) })
            {
                var oo = o; var b = new Button { Style = Ui.S("Btn"), Content = o.Item1 + "   " + o.Item2.ToString("ddd d MMM, HH:mm"), HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 5) };
                b.Click += (s, e) => { pick = oo.Item2; w.Close(); }; sp.Children.Add(b);
            }
            sp.Children.Add(Ui.Muted("Or a date and time (yyyy-MM-dd HH:mm):", 12, 4));
            var f = Ui.Field(now.AddDays(1).Date.AddHours(9).ToString("yyyy-MM-dd HH:mm")); sp.Children.Add(f);
            var err = Ui.Muted("", 12); err.Foreground = Ui.R("Red"); sp.Children.Add(err);
            var row = Buttons(sp); row.Children.Add(Ui.Btn("Cancel", () => w.Close()));
            var ok = Ui.Btn("Set", () =>
            {
                DateTime d; if (!DateTime.TryParseExact(f.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) { err.Text = "Write it like 2026-12-24 09:30."; return; }
                if (d <= DateTime.Now) { err.Text = "That's in the past."; return; }
                pick = d; w.Close();
            }, true); ok.Margin = new Thickness(0); row.Children.Add(ok);
            w.Closed += (s, e) => { if (done != null) done(pick); };
            return w;
        }
        public static DateTime? PickTime(Window owner, string title, string text)
        {
            DateTime? r = null; var w = TimeWindow(title, text, d => r = d, DateTime.Now); if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; } w.ShowDialog(); return r;
        }
    }

    public static class CrashNotice
    {
        public static Window Make(string file)
        {
            var w = new Window(); Ui.Dress(w, "Hallow Email", 440);
            w.ResizeMode = ResizeMode.NoResize; w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.Topmost = true;
            var sp = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            sp.Children.Add(Ui.T("Hallow Email closed unexpectedly last time", 15, true, null, 6));
            DateTime when = DateTime.Now; try { if (File.Exists(file)) when = File.GetLastWriteTime(file); } catch { }
            sp.Children.Add(Ui.Muted("On " + when.ToString("d MMM yyyy, HH:mm") + ". A report was saved on this PC - nothing was sent anywhere, and it never contains your mail or passwords.", 12.5, 14));
            var row = new WrapPanel();
            row.Children.Add(Ui.Btn("Show details", () => { try { System.Diagnostics.Process.Start("notepad.exe", "\"" + file + "\""); } catch { } }));
            row.Children.Add(Ui.Btn("Open folder", () => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { } }));
            var ok = Ui.Btn("OK", () => w.Close(), true); ok.MinWidth = 70; row.Children.Add(ok);
            sp.Children.Add(row); w.Content = sp;
            return w;
        }
    }
}
