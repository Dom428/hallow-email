// Hallow Email - Font Awesome icons (path data in Icons.g.cs). Buttons get their icon from their label,
// so a label that changes ("Mute" -> "Muted") changes its icon too.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HallowEmail
{
    public static partial class Icons
    {
        static readonly Dictionary<string, Geometry> cache = new Dictionary<string, Geometry>();

        // exact label -> icon
        static readonly Dictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "New email", "pen-to-square" }, { "Reply", "reply" }, { "Reply all", "reply-all" }, { "Forward", "share" }, { "Archive", "box-archive" }, { "Delete", "trash" },
            { "Spam", "ban" }, { "Not spam", "circle-check" }, { "Star", "star" }, { "Unstar", "star" }, { "Snooze", "clock" }, { "More", "chevron-down" },
            { "Send", "paper-plane" }, { "Send later...", "hourglass-half" }, { "Save draft", "file" }, { "Discard", "trash" }, { "Attach...", "paperclip" }, { "Signature", "font" },
            { "Undo", "rotate-left" }, { "Check now", "arrows-rotate" }, { "Cancel", "xmark" }, { "OK", "check" }, { "Set", "check" }, { "Save rule", "check" },
            { "Open", "folder-open" }, { "Save as...", "download" }, { "Unsubscribe", "bell-slash" }, { "Unsubscribe...", "bell-slash" }, { "Archive automatically", "box-archive" },
            { "Make the rule", "list-check" }, { "Not now", "xmark" }, { "Load images from this sender", "image" }, { "Block images again", "eye-slash" },
            { "Yes, let them in", "check" }, { "No - archive their mail", "ban" }, { "Add an account", "user-plus" }, { "Edit", "pen-to-square" }, { "Remove", "xmark" },
            { "Signature...", "font" }, { "Send as...", "at" }, { "New rule", "plus" }, { "Check for updates", "arrows-rotate" }, { "Open data folder", "folder-open" },
            { "Show details", "eye" }, { "Open folder", "folder-open" }, { "Save and check", "cloud" }, { "Create masked address", "user-secret" }, { "Copy", "copy" }, { "Turn off", "ban" },
            { "Test and save", "check" }, { "Save anyway", "file" }, { "Sign in in my browser", "key" }, { "Signed in - sign in again", "key" },
            { "Send now", "paper-plane" }, { "Edit (take it out)", "pen-to-square" }, { "Move to...", "folder" }, { "Mark read", "envelope-open" }, { "Clear", "xmark" },
            { "Update", "download" }, { "Scan and open", "shield-halved" }, { "Open in browser", "globe" }, { "Open the page", "globe" }, { "Discard draft", "trash" },
        };
        static readonly string[][] Prefixes = { new[] { "Update to", "download" }, new[] { "Open ", "globe" } };
        public static string ForLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return null;
            string n; if (Labels.TryGetValue(label.Trim(), out n)) return n;
            foreach (var p in Prefixes) if (label.StartsWith(p[0], StringComparison.OrdinalIgnoreCase)) return p[1];
            return null;
        }

        public static Geometry Get(string name)
        {
            if (name == null) return null;
            Geometry g;
            if (!cache.TryGetValue(name, out g))
            {
                string d; if (!Data.TryGetValue(name, out d)) return null;
                g = Geometry.Parse(d); g.Freeze(); cache[name] = g;
            }
            return g;
        }

        // A standalone icon (strip headers, the header bar).
        public static FrameworkElement Make(string name, double size, Brush fill)
        {
            return new Path { Data = Get(name), Fill = fill, Stretch = Stretch.Uniform, Width = size, Height = size, SnapsToDevicePixels = true };
        }
        // Icon + text for places that aren't styled buttons.
        public static FrameworkElement WithText(string name, string text, double size, Brush fill)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = Make(name, size, fill); ic.Margin = new Thickness(0, 0, 7, 0); ic.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(ic); sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }
    }

    // Used by the button styles in Theme.xaml.
    public class IconConv : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c) { return Icons.Get(Icons.ForLabel(v as string)); }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }
    public class IconVis : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c) { return Icons.ForLabel(v as string) != null ? Visibility.Visible : Visibility.Collapsed; }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }
    // Space between icon and text (none when the text is hidden).
    public class IconGap : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
        {
            var s = v as string;
            return s != null && s.Length > 1 ? new Thickness(0, 0, 7, 0) : new Thickness(0);
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }
    // Hides the text of one-character labels that have an icon ("+", "x").
    public class LabelVis : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
        {
            var s = v as string;
            return s != null && s.Length == 1 && Icons.ForLabel(s) != null ? Visibility.Collapsed : Visibility.Visible;
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }
}
