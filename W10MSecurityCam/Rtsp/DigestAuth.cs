using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;

namespace W10MSecurityCam.Rtsp
{
    /// <summary>
    /// Login for RTSP (and the snapshot URL). Uses HTTP Digest authentication (RFC 2617 without qop, which is what
    /// VLC/live555, ffmpeg and NVRs all support); Basic can be allowed too for older NVRs.
    /// The user name and password are kept in the Windows PasswordVault, not in app settings.
    /// The login is always on: a fresh install uses admin / admin (change it on the settings page or the phone).
    /// </summary>
    internal sealed class DigestAuth
    {
        public const string Realm = "W10MSecurityCam";
        private const string VaultResource = "W10MSecurityCam.RTSP";
        private static readonly TimeSpan NonceLifetime = TimeSpan.FromHours(12);

        private readonly object _lock = new object();
        private readonly string _secret = Guid.NewGuid().ToString("N");
        private string _user, _password;

        /// <summary>Also accept Basic authentication (password sent unencrypted). Off by default.</summary>
        public bool AllowBasic;

        public const string DefaultUser = "admin";
        public const string DefaultPassword = "admin";

        public bool Enabled { get { lock (_lock) return !string.IsNullOrEmpty(_password); } }
        /// <summary>True while the factory login (admin / admin) is in use.</summary>
        public bool IsDefault { get { lock (_lock) return _user == DefaultUser && _password == DefaultPassword; } }
        public string UserName { get { lock (_lock) return _user ?? ""; } }

        public void Load()
        {
            try
            {
                var vault = new PasswordVault();
                var cred = vault.FindAllByResource(VaultResource).FirstOrDefault();
                if (cred != null)
                {
                    cred.RetrievePassword();
                    lock (_lock) { _user = cred.UserName; _password = cred.Password; }
                }
            }
            catch { /* FindAllByResource throws when nothing is stored */ }
            if (!Enabled) Save(DefaultUser, DefaultPassword); // first run: the factory login
        }

        /// <summary>Sets the login. An empty password resets it to the factory login (admin / admin); the login is never off.</summary>
        public void Save(string user, string password)
        {
            var vault = new PasswordVault();
            try { foreach (var c in vault.FindAllByResource(VaultResource).ToList()) vault.Remove(c); } catch { }
            user = (user ?? "").Trim();
            if (string.IsNullOrEmpty(password)) { user = DefaultUser; password = DefaultPassword; }
            if (user.Length == 0) user = DefaultUser;
            vault.Add(new PasswordCredential(VaultResource, user, password));
            lock (_lock) { _user = user; _password = password; }
        }

        /// <summary>The WWW-Authenticate header(s) for a 401 answer (each line ends with CRLF).</summary>
        public string Challenge(bool stale = false)
        {
            string ts = DateTime.UtcNow.Ticks.ToString("X");
            string nonce = ts + Md5(_secret + ":" + ts).Substring(0, 16);
            string h = "WWW-Authenticate: Digest realm=\"" + Realm + "\", nonce=\"" + nonce + "\"" + (stale ? ", stale=TRUE" : "") + "\r\n";
            if (AllowBasic) h += "WWW-Authenticate: Basic realm=\"" + Realm + "\"\r\n";
            return h;
        }

        /// <summary>True if the request may proceed. 'method' and the Authorization header come from the request.</summary>
        public bool Check(string method, string authorization, out bool stale)
        {
            stale = false;
            string user, password;
            lock (_lock) { user = _user; password = _password; }
            if (string.IsNullOrEmpty(password)) return true;
            if (string.IsNullOrEmpty(authorization)) return false;

            if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                if (!AllowBasic) return false;
                try
                {
                    string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization.Substring(6).Trim()));
                    int colon = decoded.IndexOf(':');
                    return colon >= 0 && FixedEquals(decoded.Substring(0, colon), user) && FixedEquals(decoded.Substring(colon + 1), password);
                }
                catch { return false; }
            }

            if (!authorization.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase)) return false;
            var p = ParseParams(authorization.Substring(7));
            string pu, realm, nonce, uri, response;
            if (!p.TryGetValue("username", out pu) || !p.TryGetValue("realm", out realm) || !p.TryGetValue("nonce", out nonce) ||
                !p.TryGetValue("uri", out uri) || !p.TryGetValue("response", out response))
                return false;
            if (!FixedEquals(pu, user) || realm != Realm) return false;
            if (!NonceValid(nonce, out stale)) return false;

            string ha1 = Md5(user + ":" + Realm + ":" + password);
            string ha2 = Md5(method + ":" + uri);
            string expected;
            string qop;
            if (p.TryGetValue("qop", out qop) && qop.Length > 0)
            {
                // We don't offer qop, but accept a client that uses qop=auth anyway.
                string nc, cnonce;
                p.TryGetValue("nc", out nc);
                p.TryGetValue("cnonce", out cnonce);
                expected = Md5(ha1 + ":" + nonce + ":" + nc + ":" + cnonce + ":" + qop + ":" + ha2);
            }
            else
            {
                expected = Md5(ha1 + ":" + nonce + ":" + ha2);
            }
            return FixedEquals(expected, response.ToLowerInvariant());
        }

        private bool NonceValid(string nonce, out bool stale)
        {
            stale = false;
            if (nonce.Length <= 16) return false;
            string ts = nonce.Substring(0, nonce.Length - 16);
            if (!FixedEquals(Md5(_secret + ":" + ts).Substring(0, 16), nonce.Substring(nonce.Length - 16))) return false;
            long ticks;
            if (!long.TryParse(ts, System.Globalization.NumberStyles.HexNumber, null, out ticks)) return false;
            if (DateTime.UtcNow.Ticks - ticks > NonceLifetime.Ticks) { stale = true; return false; }
            return true;
        }

        private static Dictionary<string, string> ParseParams(string s)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == ',')) i++;
                int eq = s.IndexOf('=', i);
                if (eq < 0) break;
                string key = s.Substring(i, eq - i).Trim();
                i = eq + 1;
                string value;
                if (i < s.Length && s[i] == '"')
                {
                    int end = s.IndexOf('"', i + 1);
                    if (end < 0) end = s.Length;
                    value = s.Substring(i + 1, end - i - 1);
                    i = end + 1;
                }
                else
                {
                    int end = s.IndexOf(',', i);
                    if (end < 0) end = s.Length;
                    value = s.Substring(i, end - i).Trim();
                    i = end;
                }
                d[key] = value;
            }
            return d;
        }

        private static string Md5(string s)
        {
            var alg = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Md5);
            var hash = alg.HashData(CryptographicBuffer.ConvertStringToBinary(s, BinaryStringEncoding.Utf8));
            return CryptographicBuffer.EncodeToHexString(hash).ToLowerInvariant();
        }

        /// <summary>Compares without stopping at the first difference (no timing hints).</summary>
        private static bool FixedEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
