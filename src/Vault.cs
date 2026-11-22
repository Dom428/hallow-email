// Hallow Email - encryption at rest.
// cache.key   : 64 random bytes, protected with DPAPI (CurrentUser, plus app entropy) - only this Windows user on this PC can open it.
//               32 bytes are the AES-256 key, 32 the HMAC-SHA256 key.
// every cache / profile file: "HEM1" | 16-byte IV | AES-256-CBC ciphertext | HMAC-SHA256(magic|IV|ciphertext) - encrypt-then-MAC,
//               a changed or truncated file is refused (never half-read).
// secrets.dat : passwords, app passwords, refresh tokens, the Cloudflare token - DPAPI directly. Never logged, never in status or crash
//               reports (Crash.Secrets scrubs them from a report just in case).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace HallowEmail
{
    public static class Vault
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HallowEmail.cache.v1");
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("HEM1");
        static byte[] encKey, macKey; static readonly object keyLock = new object();
        static string KeyPath { get { return Path.Combine(MailSettings.Dir, "cache.key"); } }

        public static void ResetKeyForTests() { lock (keyLock) { encKey = null; macKey = null; } }
        static void EnsureKey()
        {
            lock (keyLock)
            {
                if (encKey != null) return;
                byte[] raw = null;
                if (File.Exists(KeyPath)) raw = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), Entropy, DataProtectionScope.CurrentUser);
                else
                {
                    raw = new byte[64]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
                    if (!MailSettings.Frozen) Files.WriteAtomic(KeyPath, ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser));
                }
                encKey = raw.Take(32).ToArray(); macKey = raw.Skip(32).Take(32).ToArray();
            }
        }
        public static byte[] Encrypt(byte[] plain)
        {
            EnsureKey();
            using (var aes = Aes.Create())
            {
                aes.Key = encKey; aes.GenerateIV(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                byte[] ct; using (var e = aes.CreateEncryptor()) ct = e.TransformFinalBlock(plain, 0, plain.Length);
                var body = Magic.Concat(aes.IV).Concat(ct).ToArray();
                byte[] mac; using (var h = new HMACSHA256(macKey)) mac = h.ComputeHash(body);
                return body.Concat(mac).ToArray();
            }
        }
        public static byte[] Decrypt(byte[] data)
        {
            EnsureKey();
            if (data == null || data.Length < 4 + 16 + 16 + 32) throw new CryptographicException("too short");
            for (int i = 0; i < 4; i++) if (data[i] != Magic[i]) throw new CryptographicException("not a Hallow Email file");
            int bodyLen = data.Length - 32;
            byte[] mac; using (var h = new HMACSHA256(macKey)) mac = h.ComputeHash(data, 0, bodyLen);
            int diff = 0; for (int i = 0; i < 32; i++) diff |= mac[i] ^ data[bodyLen + i];
            if (diff != 0) throw new CryptographicException("the file was changed or damaged");
            using (var aes = Aes.Create())
            {
                aes.Key = encKey; aes.IV = data.Skip(4).Take(16).ToArray(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (var d = aes.CreateDecryptor()) return d.TransformFinalBlock(data, 20, bodyLen - 20);
            }
        }
        public static byte[] ReadFile(string path) { return File.Exists(path) ? Decrypt(File.ReadAllBytes(path)) : null; }
        public static void WriteFile(string path, byte[] plain) { if (MailSettings.Frozen) return; Files.WriteAtomic(path, Encrypt(plain)); }
    }

    // ---- secrets.dat (DPAPI). Keys: "pw:<account id>", "rt:<account id>" (OAuth refresh token), "cs:<account id>" (OAuth client secret,
    // if the provider gave one), "cf" (Cloudflare API token)
    [DataContract] public class SecretBag { [DataMember] public Dictionary<string, string> Items = new Dictionary<string, string>(); }
    public static class Secrets
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HallowEmail.secrets.v1");
        static string FilePath { get { return Path.Combine(MailSettings.Dir, "secrets.dat"); } }
        static readonly object l = new object();
        static Dictionary<string, string> cache;
        static Dictionary<string, string> All()
        {
            if (cache != null) return cache;
            var d = new Dictionary<string, string>();
            try
            {
                if (File.Exists(FilePath))
                {
                    var b = Json.Read<SecretBag>(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser));
                    if (b != null && b.Items != null) d = b.Items;
                }
            }
            catch { }
            cache = d; Scrub(); return d;
        }
        public static void Reset() { lock (l) cache = null; }
        public static string Get(string key) { lock (l) { string v; return All().TryGetValue(key, out v) ? v : null; } }
        public static bool Has(string key) { return !string.IsNullOrEmpty(Get(key)); }
        public static void Set(string key, string value)
        {
            lock (l)
            {
                var d = All();
                if (string.IsNullOrEmpty(value)) d.Remove(key); else d[key] = value;
                if (MailSettings.Frozen) return;
                Files.WriteAtomic(FilePath, ProtectedData.Protect(Json.Bytes(new SecretBag { Items = d }), Entropy, DataProtectionScope.CurrentUser));
                Scrub();
            }
        }
        public static void RemoveAccount(string id) { Set("pw:" + id, null); Set("rt:" + id, null); Set("cs:" + id, null); }
        static void Scrub() { Crash.Secrets = cache.Values.Where(v => !string.IsNullOrEmpty(v)).ToArray(); }
    }
}
