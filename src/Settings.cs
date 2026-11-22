// Hallow Email settings. Two files in %APPDATA%\HallowEmail:
//   settings.json - only the look and app switches (nothing personal, nothing secret)
//   profile.bin   - accounts, rules, sender lists, the spam filter, snoozes and the send queue: encrypted with the cache key (Vault.cs)
//   secrets.dat   - passwords, app passwords, OAuth refresh tokens and the Cloudflare token: DPAPI (this Windows user only)
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace HallowEmail
{
    [DataContract]
    public class MailSettings
    {
        // developer tests: HALLOW_EMAIL_TESTDIR = data folder (and a separate pipe); HALLOW_EMAIL_HEADLESS=1 = no windows or tray.
        public static readonly string TestDir = Env("HALLOW_EMAIL_TESTDIR");
        public static readonly bool Headless = TestDir != null && Env("HALLOW_EMAIL_HEADLESS") == "1";
        public static bool Frozen;   // --snap: never saved
        public static string OverrideDir;   // unit tests point this at a temp folder
        static string Env(string n) { var v = Environment.GetEnvironmentVariable(n); return string.IsNullOrWhiteSpace(v) ? null : v; }
        public static string Dir { get { return OverrideDir ?? TestDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HallowEmail"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        [DataMember] public bool UseHallowIcon = true;
        [DataMember] public bool UseHallowAccent = true;
        [DataMember] public string Look = "hallow";          // "hallow" (theme.json's mode), "light", "dark"
        [DataMember] public bool NotifyPeople = true;         // a note through Hallow Hub for new mail from People
        [DataMember] public bool NotifyDuringGames;           // off: nothing while a game runs
        [DataMember] public bool SafeTextView;                // always show mail as plain text (no WebView2)
        [DataMember] public int UndoSeconds = 10;
        [DataMember] public bool Screener;                    // first-time senders wait in the Screener
        [DataMember] public bool SpamFilter = true;
        [DataMember] public string LastUpdateCheck;
        [DataMember] public string LastCrashShown;
        [DataMember] public bool TrayHintShown;
        [DataMember] public List<string> ExtraGames;

        [OnDeserializing] void Defaults(StreamingContext c) { UseHallowIcon = true; UseHallowAccent = true; Look = "hallow"; NotifyPeople = true; UndoSeconds = 10; SpamFilter = true; }

        public static MailSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var f = File.OpenRead(FilePath))
                    {
                        var s = (MailSettings)new DataContractJsonSerializer(typeof(MailSettings)).ReadObject(f);
                        if (s != null) { s.Fix(); return s; }
                    }
            }
            catch { }
            var d = new MailSettings(); d.Fix(); return d;
        }
        void Fix()
        {
            if (ExtraGames == null) ExtraGames = new List<string>();
            if (Look != "light" && Look != "dark") Look = "hallow";
            UndoSeconds = Math.Max(0, Math.Min(30, UndoSeconds));
        }
        static readonly object saveLock = new object();
        public void Save()
        {
            if (Frozen) return;
            lock (saveLock) { try { Files.WriteAtomic(FilePath, Json.Bytes(this)); } catch { } }
        }
    }

    public static class Json
    {
        public static byte[] Bytes<T>(T o) { using (var ms = new MemoryStream()) { new DataContractJsonSerializer(typeof(T)).WriteObject(ms, o); return ms.ToArray(); } }
        public static T Read<T>(byte[] b) where T : class { if (b == null) return null; using (var ms = new MemoryStream(b)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(ms); }
    }
    public static class Files
    {
        public static void WriteAtomic(string path, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            for (int i = 0; ; i++) { try { if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); break; } catch { if (i >= 5) throw; Thread.Sleep(50); } }
        }
    }

    // ---- one mail account. Server settings only - the password / token is in secrets.dat
    [DataContract]
    public class Account
    {
        [DataMember] public string Id;
        [DataMember] public string Email;
        [DataMember] public string Name;            // the name people see ("Alex Rivera")
        [DataMember] public string Label;           // short label in the list ("Personal")
        [DataMember] public string Preset;          // gmail, outlook, yahoo, icloud, custom
        [DataMember] public string ImapHost; [DataMember] public int ImapPort = 993; [DataMember] public string ImapSecurity = "ssl";   // ssl, starttls, none (none = local tests only)
        [DataMember] public string SmtpHost; [DataMember] public int SmtpPort = 465; [DataMember] public string SmtpSecurity = "ssl";
        [DataMember] public string UserName;
        [DataMember] public string Auth = "password";  // password, oauth-google, oauth-microsoft
        [DataMember] public string OAuthClientId;      // the user's own registered app (not secret)
        [DataMember] public string Signature;          // plain text signature, added to new mail
        [DataMember] public List<string> SendAs;       // extra From addresses (e.g. you@hallowservices.com via Cloudflare routing)
        [DataMember] public string Sensitivity = "normal";   // spam filter: low, normal, high
        [DataMember] public bool SentSavedByServer;    // Gmail / Outlook.com save sent mail themselves
        [DataMember] public bool Off;
        public string Display { get { return string.IsNullOrEmpty(Label) ? Email : Label; } }
    }

    // ---- a sorting rule (Settings > Rules, or "Create rule from this email")
    [DataContract]
    public class Rule
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public bool On = true;
        [DataMember] public string Account;            // null = every account
        // conditions (all that are set must match; text matches are "contains", case-insensitive)
        [DataMember] public string From; [DataMember] public string Subject; [DataMember] public string To; [DataMember] public string ListId;
        [DataMember] public bool? HasAttachment;
        // actions
        [DataMember] public string MoveTo;             // folder name on the server
        [DataMember] public string Label;              // local label (and an IMAP keyword where the server takes them)
        [DataMember] public bool MarkRead; [DataMember] public bool Star; [DataMember] public bool SkipInbox; [DataMember] public bool Notify;
        [DataMember] public string Category;           // people, newsletters, receipts, notifications
        [DataMember] public int Hits;
    }

    [DataContract] public class Snooze { [DataMember] public string Key; [DataMember] public string Until; [DataMember] public string Subject; }
    [DataContract]
    public class Queued
    {
        [DataMember] public string Id; [DataMember] public string Account; [DataMember] public string SendAt; [DataMember] public string Mime;   // base64 of the whole message
        [DataMember] public string To; [DataMember] public string Subject; [DataMember] public bool Undoable; [DataMember] public string Error; [DataMember] public int Tries;
    }
    [DataContract] public class MoveNote { [DataMember] public string Sender; [DataMember] public string Folder; [DataMember] public string When; }
    [DataContract] public class Masked { [DataMember] public string Address; [DataMember] public string RuleId; [DataMember] public string Note; [DataMember] public string Created; [DataMember] public bool Off; }

    // ---- everything personal, encrypted (Vault)
    [DataContract]
    public class Profile
    {
        [DataMember] public List<Account> Accounts = new List<Account>();
        [DataMember] public List<Rule> Rules = new List<Rule>();
        [DataMember] public Dictionary<string, string> SenderCategory = new Dictionary<string, string>();   // address or @domain -> category the user picked
        [DataMember] public List<string> ImageSenders = new List<string>();      // "Load images from this sender"
        [DataMember] public List<string> Known = new List<string>();             // addresses mail has come from (first-time note)
        [DataMember] public List<string> Trusted = new List<string>();           // people you've written to: never spam
        [DataMember] public List<string> ScreenerOk = new List<string>();        // approved in the Screener
        [DataMember] public List<string> ScreenerNo = new List<string>();        // screened out
        [DataMember] public Dictionary<string, List<string>> Labels = new Dictionary<string, List<string>>();   // message key -> labels
        [DataMember] public List<Snooze> Snoozed = new List<Snooze>();
        [DataMember] public List<Queued> Outbox = new List<Queued>();
        [DataMember] public List<MoveNote> Moves = new List<MoveNote>();        // for "you always move X to Y" (newest 400)
        [DataMember] public Dictionary<string, int[]> Opens = new Dictionary<string, int[]>();   // newsletter sender -> [received, opened]
        [DataMember] public List<string> Dismissed = new List<string>();         // suggestions the user said no to
        [DataMember] public BayesData Bayes;
        [DataMember] public List<Masked> MaskedAddresses = new List<Masked>();
        [DataMember] public string CfZone;            // Cloudflare: the domain (e.g. hallowservices.com) - the token itself is in secrets.dat
        [DataMember] public string CfZoneId; [DataMember] public string CfForwardTo;

        public void Fix()
        {
            if (Accounts == null) Accounts = new List<Account>(); if (Rules == null) Rules = new List<Rule>();
            if (SenderCategory == null) SenderCategory = new Dictionary<string, string>(); if (ImageSenders == null) ImageSenders = new List<string>();
            if (Known == null) Known = new List<string>(); if (Trusted == null) Trusted = new List<string>();
            if (ScreenerOk == null) ScreenerOk = new List<string>(); if (ScreenerNo == null) ScreenerNo = new List<string>();
            if (Labels == null) Labels = new Dictionary<string, List<string>>(); if (Snoozed == null) Snoozed = new List<Snooze>();
            if (Outbox == null) Outbox = new List<Queued>(); if (Moves == null) Moves = new List<MoveNote>();
            if (Opens == null) Opens = new Dictionary<string, int[]>(); if (Dismissed == null) Dismissed = new List<string>();
            if (MaskedAddresses == null) MaskedAddresses = new List<Masked>();
            foreach (var a in Accounts) { if (a.SendAs == null) a.SendAs = new List<string>(); if (string.IsNullOrEmpty(a.Sensitivity)) a.Sensitivity = "normal"; }
        }
        static string FilePath { get { return Path.Combine(MailSettings.Dir, "profile.bin"); } }
        public static Profile Load()
        {
            try { var b = Vault.ReadFile(FilePath); var p = b == null ? null : Json.Read<Profile>(b); if (p != null) { p.Fix(); return p; } } catch { }
            var d = new Profile(); d.Fix(); return d;
        }
        readonly object saveLock = new object();
        public void Save()
        {
            if (MailSettings.Frozen) return;
            lock (saveLock) { byte[] b; lock (this) b = Json.Bytes(this); Vault.WriteFile(FilePath, b); }
        }
        public Account Find(string id) { return Accounts.Find(a => a.Id == id); }
    }
}
