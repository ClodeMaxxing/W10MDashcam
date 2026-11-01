using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using W10MDashcam.Recording;
using W10MDashcam.Rtsp;

namespace W10MDashcam.Streaming
{
    internal sealed class HttpRequest
    {
        public string Method = "GET";
        public string Path = "/";
        public string Query = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body = "";
        /// <summary>True when the request came over the USB cable (Microsoft's IP-over-USB shows up as 127.0.0.1).</summary>
        public bool FromUsb;
        /// <summary>The target as sent on the request line (the Digest response must name it).</summary>
        public string Target = "/";
        /// <summary>The client's IP address (wrong logins are counted per address).</summary>
        public string RemoteIp;
    }

    /// <summary>A file sent in pieces (a recording): the reply head, then Length bytes of Body from its current position.</summary>
    internal sealed class FileReply
    {
        public byte[] Head;
        public System.IO.Stream Body;
        public long Length;
        /// <summary>Instead of a file: a complete reply (an error).</summary>
        public byte[] Error;
    }

    /// <summary>
    /// The small web interface, served on the log port (http://PHONE-IP:5001) and on the RTSP ports:
    ///   /, /camera    the settings page; /camera/state its JSON; POST /camera/set changes
    ///   /log, /stats  the app log and the graphs - only with the log and stats switch on
    ///   /status       health as JSON (for monitoring)
    ///   /login        asks the browser for the login, then back to /camera
    ///   /snapshot.jpg the latest picture, when snapshots are turned on (needs the login)
    ///   /config       view (GET) or change (POST, form fields) settings - only over the USB cable
    ///   /recordings   the dashcam recordings as JSON; /recordings/file?name=N[&amp;saved=1][&amp;dl=1] plays or downloads one;
    ///                 /recordings/srt?name=N[&amp;saved=1] its captions as an .srt file;
    ///                 POST /recordings/save keeps the current clip, POST /recordings/delete (name, saved) deletes one
    /// Reading is open on the home network (view only); changes, the snapshot and the recordings themselves need the
    /// stream login, over USB too. Changes (POST) also need the X-Requested-With: W10MDashcam header and the phone's
    /// address as Host.
    /// </summary>
    internal sealed class HttpApi
    {
        public Func<string> GetLog = () => "";
        public Func<string> GetStatusJson = () => "{}";
        /// <summary>Returns a JPEG, or null when snapshots are off or not possible.</summary>
        public Func<Task<byte[]>> GetSnapshotJpeg;
        public Func<IDictionary<string, string>, Task<string>> ApplyConfig;
        public Func<string> GetConfig = () => "";
        public Func<string> GetCameraJson = () => "{}";
        public DigestAuth Auth;
        public Action<string> Log = _ => { };
        public SegmentRecorder Recorder;
        /// <summary>The web page is switched on (phone: Network and login). Off: every HTTP request gets "switched off".</summary>
        public volatile bool WebEnabled = true;
        /// <summary>The log and stats pages are switched on.</summary>
        public volatile bool LogEnabled = true;
        /// <summary>The stats samples as JavaScript string literals, for the stats page.</summary>
        public Func<string> GetStatsRows = () => "";
        /// <summary>
        /// Phone switch "Delete saved clips from the web page" (off by default). Off: nothing over the network can delete,
        /// unlock (unlocked clips go back to loop recording) or replace saved clips, nor change the saved-clip limit.
        /// </summary>
        public volatile bool AllowDeleteSaved;

        private const string SavedLockedText = "Saved clips can't be deleted, unlocked or replaced from here: switch on \"Delete saved clips from the web page\" on the phone (Network and login).";

        /// <summary>Removes the saved-clip limit fields when the phone doesn't allow that; returns a note, or null.</summary>
        private string StripSavedLimits(IDictionary<string, string> f)
        {
            if (AllowDeleteSaved || !(f.Remove("savedlimit") | f.Remove("replacesaved"))) return null;
            return "Saved-clip limit not changed (switched off on the phone).";
        }

        private static byte[] InternalError() => Reply(500, "Internal Server Error", "text/plain", "Internal error, see the app log\n");
        private static byte[] WebOff() => SwitchedOff("web page");
        private static byte[] LogOff() => SwitchedOff("log and stats pages");
        private static byte[] SwitchedOff(string what) =>
            Reply(403, "Forbidden", "text/plain", "The " + what + (what.EndsWith("s") ? " are" : " is") + " switched off. Switch " + (what.EndsWith("s") ? "them" : "it") + " on on the phone (Network and login)" + (what == "web page" ? "" : " or on the settings page") + ".\n");

        public async Task<byte[]> HandleAsync(HttpRequest r)
        {
            try
            {
                if (!WebEnabled) return WebOff();
                string path = r.Path.TrimEnd('/');
                // The pages, the log and the JSON are view only without a login (as in W10MWebCam). Changes, the snapshot,
                // the recordings themselves and /login need the stream login, over USB too: any program or web page on the
                // PC the phone is plugged into can reach the USB port, so being on USB proves nothing. A login the browser
                // sends must be right, though: a wrong or outdated one gets a 401, so the browser renews the nonce (stale)
                // or asks again instead of resending it forever. Checked once per request: with qop each nonce count may
                // only be used once.
                string authorization = Header(r, "Authorization");
                if (Auth != null && (authorization != null || path == "/login") && Auth.IsLockedOut(r.RemoteIp))
                    return Reply(429, "Too Many Requests", "text/plain", "Too many wrong logins - try again in 30 seconds\n", "Retry-After: 30\r\n");
                bool canWrite = false, stale = false;
                if (Auth != null && authorization != null && !(canWrite = Auth.Check(r.Method, r.Target, authorization, r.RemoteIp, out stale)))
                    return Reply(401, "Unauthorized", "text/plain", "Login required\n", Auth.Challenge(stale));
                if (path == "/log")
                    return LogEnabled ? Reply(200, "OK", "text/plain; charset=utf-8", GetLog(), "Cache-Control: no-store\r\n") : LogOff();

                if (path == "/stats")
                    return LogEnabled
                        ? Reply(200, "OK", "text/html; charset=utf-8", StatsPage.Html.Replace("/*ROWS*/", GetStatsRows()), "Cache-Control: no-store\r\n")
                        : LogOff();

                if (path == "/status")
                    return Reply(200, "OK", "application/json", GetStatusJson());

                if (path == "/login")
                {
                    // Already logged in: straight back to the page. Otherwise the browser asks for the login, then goes back.
                    if (canWrite) return Reply(302, "Found", "text/plain", "", "Location: /camera\r\n");
                    return Reply(401, "Unauthorized", "text/html; charset=utf-8",
                        "<!doctype html><meta http-equiv='refresh' content='0;url=/camera'><p>Login needed. <a href='/login'>Try again</a></p>\n",
                        Auth != null ? Auth.Challenge(stale) : "");
                }

                if (path == "/snapshot.jpg" || path == "/snapshot")
                {
                    if (!canWrite) return LoginNeeded(stale);
                    var jpg = GetSnapshotJpeg != null ? await GetSnapshotJpeg() : null;
                    if (jpg == null) return Reply(404, "Not Found", "text/plain", "Snapshots are turned off (or the camera is not streaming)\n");
                    return Reply(200, "OK", "image/jpeg", jpg, "Cache-Control: no-store\r\n");
                }

                if (path == "" || path == "/camera" || path == "/camera/state" || path == "/camera/set")
                    return await HandleCameraAsync(r, path == "" ? "/camera" : path, canWrite, stale);

                if (path == "/recordings/srt")
                    return canWrite ? await HandleCaptionsAsync(r) : LoginNeeded(stale);

                if (path == "/recordings" || path == "/recordings/save" || path == "/recordings/delete" || path == "/recordings/deleteall"
                    || path == "/recordings/lock" || path == "/recordings/unlock")
                    return await HandleRecordingsAsync(r, path, canWrite, stale);

                if (path == "/config")
                {
                    if (!r.FromUsb) return Reply(403, "Forbidden", "text/plain", "Settings can only be changed over the USB cable\n");
                    if (r.Method == "POST" && ApplyConfig != null)
                    {
                        if (!canWrite) return LoginNeeded(stale);
                        string refused = RefuseForeignPost(r);
                        if (refused != null) return Reply(403, "Forbidden", "text/plain", refused);
                        var form = ParseForm(r.Body); // changes only in the body, never in the URL
                        string note = StripSavedLimits(form);
                        // As on the web page: a login change needs the current password too ("current" field).
                        string loginNote = CheckCurrentPassword(form, form);
                        if (loginNote != null) note = note == null ? loginNote : note + " " + loginNote;
                        form.Remove("current");
                        string result = form.Count > 0 ? await ApplyConfig(form) : "";
                        if (note != null) result = note + (result.Length > 0 ? "\n" + result : "");
                        return Reply(200, "OK", "text/plain; charset=utf-8", result + "\n\n" + GetConfig());
                    }
                    return Reply(200, "OK", "text/plain; charset=utf-8", GetConfig());
                }

                return Reply(404, "Not Found", "text/plain", "Not found. Try /camera, /status, /log, /stats, /snapshot.jpg, /recordings\n");
            }
            catch (Exception ex)
            {
                Log("Web request " + r.Method + " " + r.Path + " failed: " + ex.Message); // the details stay in the log
                return InternalError();
            }
        }

        /// <summary>A 401 that makes the browser ask for the login (stale: the login was right, only the nonce was old).</summary>
        private byte[] LoginNeeded(bool stale) =>
            Reply(401, "Unauthorized", "text/plain", "Login needed\n", Auth != null ? Auth.Challenge(stale) : "");

        /// <summary>
        /// The camera-settings page: view only without the login; canWrite = the request carried the right login
        /// (checked in HandleAsync), which /camera/set needs - a 401 makes the browser ask for it.
        /// </summary>
        private async Task<byte[]> HandleCameraAsync(HttpRequest r, string path, bool canWrite, bool stale)
        {
            string result = null;
            bool loginSet = Auth != null && Auth.Enabled;

            if (path == "/camera")
                return Reply(200, "OK", "text/html; charset=utf-8", CameraPage.Html, "Cache-Control: no-store\r\n");

            if (path == "/camera/set")
            {
                if (r.Method != "POST") return Reply(405, "Method Not Allowed", "text/plain", "POST only\n");
                if (!canWrite) return LoginNeeded(stale);
                string refused = RefuseForeignPost(r);
                if (refused != null) return Reply(403, "Forbidden", "text/plain", refused);
                var all = ParseForm(r.Body); // changes only in the body, never in the URL
                var only = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var k in new[] { "camera", "focus", "exposure", "wb", "format", "bitrate", "highbw", "powersave", "stream", "engine", "user", "password", "savepower", "saveois", "savefocus", "probe", "torch", "preview", "keeppreview", "record", "segment", "storage", "saveclip", "resume", "testincident", "powerloss", "testpowerloss", "log", "recordonopen", "live", "liveonopen", "location", "impact", "keepnext", "savedlimit", "replacesaved", "gps", "units", "subtitles" })
                    if (all.TryGetValue(k, out var v)) only[k] = v;
                string note = StripSavedLimits(only);
                string bad = CheckLoginFields(only) ?? CheckCurrentPassword(only, all);
                if (bad != null) result = bad;
                else if (ApplyConfig != null && only.Count > 0) result = await ApplyConfig(only);
                if (note != null) result = note + (result != null ? " " + result : "");
            }
            string json = GetCameraJson();
            if (result != null)
            {
                string first = result.Split('\n')[0].Replace("\\", "\\\\").Replace("\"", "\\\"");
                int end = json.LastIndexOf('}'); // only the outer brace: the JSON has nested objects
                if (end > 0) json = json.Substring(0, end) + ",\"result\":\"" + first + "\"}";
            }
            if (json.StartsWith("{")) json = "{\"canWrite\":" + (canWrite ? "true" : "false") + ",\"loginSet\":" + (loginSet ? "true" : "false")
                + ",\"fromUsb\":" + (r.FromUsb ? "true" : "false")
                + ",\"defaultLogin\":" + (Auth != null && Auth.IsDefault ? "true" : "false") + ",\"user\":\"" + (canWrite && Auth != null ? Auth.UserName.Replace("\\", "").Replace("\"", "") : "") + "\"" + (json.Length > 2 ? "," : "") + json.Substring(1);
            return Reply(200, "OK", "application/json", json, "Cache-Control: no-store\r\n");
        }

        /// <summary>The recordings list (JSON, view only) and the save / delete actions (same rules as /camera/set).</summary>
        private async Task<byte[]> HandleRecordingsAsync(HttpRequest r, string path, bool canWrite, bool stale)
        {
            if (Recorder == null) return Reply(404, "Not Found", "text/plain", "No recorder\n");
            if (path == "/recordings")
            {
                var sb = new StringBuilder("{\"location\":\"" + Esc(Recorder.Location) + "\",\"files\":[");
                bool first = true;
                foreach (var f in await Recorder.ListAsync())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"name\":\"").Append(Esc(f.Name)).Append("\",\"size\":").Append(f.Size)
                      .Append(",\"saved\":").Append(f.Saved ? "true" : "false").Append(",\"recording\":").Append(f.Recording ? "true" : "false")
                      .Append(",\"note\":\"").Append(Esc(f.Note)).Append("\"}");
                }
                sb.Append("]}");
                return Reply(200, "OK", "application/json", sb.ToString(), "Cache-Control: no-store\r\n");
            }
            if (r.Method != "POST") return Reply(405, "Method Not Allowed", "text/plain", "POST only\n");
            if (!canWrite) return LoginNeeded(stale);
            string refused = RefuseForeignPost(r);
            if (refused != null) return Reply(403, "Forbidden", "text/plain", refused);
            if (path == "/recordings/save")
            {
                Recorder.SaveClip();
                return Reply(200, "OK", "text/plain", "Saving the clip\n");
            }
            var form = ParseForm(r.Body); // changes only in the body, never in the URL
            bool touchesSaved = path == "/recordings/unlock"
                || ((path == "/recordings/delete" || path == "/recordings/deleteall") && form.TryGetValue("saved", out var s0) && s0 == "1");
            if (touchesSaved && !AllowDeleteSaved) return Reply(403, "Forbidden", "text/plain", SavedLockedText + "\n");
            string fname = form.TryGetValue("name", out var fn) ? fn : "";
            if (path == "/recordings/lock") { Recorder.Lock(fname); return Reply(200, "OK", "text/plain", "Keeping " + fname + "\n"); }
            if (path == "/recordings/unlock") { Recorder.Unlock(fname); return Reply(200, "OK", "text/plain", "Unlocking " + fname + "\n"); }
            if (path == "/recordings/deleteall")
            {
                // the page asks the user to type DELETE; the check here keeps a stray request from wiping everything
                if (!form.TryGetValue("confirm", out var cf) || cf != "DELETE") return Reply(400, "Bad Request", "text/plain", "confirm=DELETE is needed\n");
                string wiped = await Recorder.DeleteAllAsync(form.TryGetValue("saved", out var withSaved) && withSaved == "1");
                return Reply(200, "OK", "text/plain", wiped + "\n");
            }
            string err = await Recorder.DeleteAsync(form.TryGetValue("name", out var n) ? n : "", form.TryGetValue("saved", out var sv) && sv == "1");
            return err == null ? Reply(200, "OK", "text/plain", "Deleted\n") : Reply(400, "Bad Request", "text/plain", err + "\n");
        }

        /// <summary>
        /// GET /recordings/srt?name=N[&amp;saved=1]: the recording's captions (date/time, speed, position) as an .srt file,
        /// for players that load captions from a file with the same name as the video.
        /// </summary>
        private async Task<byte[]> HandleCaptionsAsync(HttpRequest r)
        {
            if (Recorder == null) return Reply(404, "Not Found", "text/plain", "No recorder\n");
            var q = ParseForm(r.Query);
            string name = q.TryGetValue("name", out var n) ? n : "";
            bool saved = q.TryGetValue("saved", out var sv) && sv == "1";
            using (var body = await Recorder.OpenReadAsync(name, saved))
            {
                if (body == null) return Reply(404, "Not Found", "text/plain", "No such recording\n");
                string srt = await CaptionExport.ToSrtAsync(body);
                if (srt == null) return Reply(404, "Not Found", "text/plain; charset=utf-8", "This recording has no captions (it was made before version 0.5, or with captions turned off).\n");
                string srtName = System.IO.Path.GetFileNameWithoutExtension(name) + ".srt";
                return Reply(200, "OK", "application/x-subrip; charset=utf-8", "\uFEFF" + srt,
                    "Content-Disposition: attachment; filename=\"" + srtName + "\"\r\nCache-Control: no-store\r\n");
            }
        }

        /// <summary>
        /// GET /recordings/file?name=N[&amp;saved=1][&amp;dl=1]: a recording, sent in pieces by the caller (it can be
        /// gigabytes). Supports one byte range so browsers and players can seek. Null when the path is something else.
        /// </summary>
        public async Task<FileReply> TryOpenFileAsync(HttpRequest r)
        {
            if (Recorder == null || r.Path.TrimEnd('/') != "/recordings/file") return null;
            try
            {
                if (!WebEnabled) return new FileReply { Error = WebOff() };
                byte[] refusedLogin = CheckLogin(r);
                if (refusedLogin != null) return new FileReply { Error = refusedLogin };
                var q = ParseForm(r.Query);
                string name = q.TryGetValue("name", out var n) ? n : "";
                bool saved = q.TryGetValue("saved", out var sv) && sv == "1";
                var body = await Recorder.OpenReadAsync(name, saved);
                if (body == null) return new FileReply { Error = Reply(404, "Not Found", "text/plain", "No such recording\n") };
                long total = body.Length, from = 0, to = total - 1;
                int code = 200;
                string extra = "Accept-Ranges: bytes\r\nCache-Control: no-store\r\n";
                var m = System.Text.RegularExpressions.Regex.Match(Header(r, "Range") ?? "", @"^bytes=(\d*)-(\d*)$");
                if (m.Success && total > 0)
                {
                    if (m.Groups[1].Value.Length > 0)
                    {
                        from = long.Parse(m.Groups[1].Value);
                        if (m.Groups[2].Value.Length > 0) to = Math.Min(to, long.Parse(m.Groups[2].Value));
                    }
                    else if (m.Groups[2].Value.Length > 0) from = Math.Max(0, total - long.Parse(m.Groups[2].Value)); // last N bytes
                    if (from > to || from >= total)
                    {
                        body.Dispose();
                        return new FileReply { Error = Reply(416, "Range Not Satisfiable", "text/plain", "", "Content-Range: bytes */" + total + "\r\n") };
                    }
                    code = 206;
                    extra += "Content-Range: bytes " + from + "-" + to + "/" + total + "\r\n";
                }
                if (q.TryGetValue("dl", out var dl) && dl == "1") extra += "Content-Disposition: attachment; filename=\"" + name + "\"\r\n";
                body.Position = from;
                long len = Math.Max(0, to - from + 1);
                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.0 " + code + (code == 206 ? " Partial Content" : " OK") + "\r\n" +
                    "Content-Type: video/mp4\r\n" +
                    "Content-Length: " + len + "\r\n" + extra + SecurityHeaders +
                    "Connection: close\r\n\r\n");
                return new FileReply { Head = head, Body = body, Length = len };
            }
            catch (Exception ex)
            {
                Log("Web request " + r.Method + " " + r.Path + " failed: " + ex.Message);
                return new FileReply { Error = InternalError() };
            }
        }

        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        /// <summary>Null when the request carries the right login, otherwise the 401 (or 429 after too many wrong logins).</summary>
        private byte[] CheckLogin(HttpRequest r)
        {
            if (Auth == null) return null;
            if (Auth.IsLockedOut(r.RemoteIp))
                return Reply(429, "Too Many Requests", "text/plain", "Too many wrong logins - try again in 30 seconds\n", "Retry-After: 30\r\n");
            if (!Auth.Check(r.Method, r.Target, Header(r, "Authorization"), r.RemoteIp, out bool stale))
                return Reply(401, "Unauthorized", "text/plain", "Login required\n", Auth.Challenge(stale));
            return null;
        }

        /// <summary>Returns an error text when the user/password fields of a web change are not acceptable, otherwise null.</summary>
        private static string CheckLoginFields(IDictionary<string, string> f)
        {
            if (f.TryGetValue("user", out var u) && !System.Text.RegularExpressions.Regex.IsMatch(u ?? "", @"^[A-Za-z0-9._-]{1,32}$"))
            { f.Remove("user"); f.Remove("password"); return "Not changed: the user name may use letters, digits . _ - (1-32 characters)"; }
            if (f.TryGetValue("password", out var p) && (p == null || p.Length < DigestAuth.MinPasswordLength || p.Length > 64 || p.Any(ch => ch < ' ')))
            { f.Remove("user"); f.Remove("password"); return "Not changed: the password needs " + DigestAuth.MinPasswordLength + "-64 characters"; }
            return null;
        }

        public const string ClientHeader = "W10MDashcam";

        /// <summary>
        /// Null when a change request may go ahead, otherwise why it is refused. A web page on another site could make the
        /// browser send a POST with the saved login. Such a page cannot add a custom header without our permission (never
        /// granted), and a DNS-rebinding page would send its own host name instead of an IP address, so both checks
        /// together leave only this app's own page and the tools. Over USB too.
        /// </summary>
        private static string RefuseForeignPost(HttpRequest r)
        {
            if (Header(r, "X-Requested-With") != ClientHeader)
                return "Use the settings page (missing X-Requested-With header)\n";
            if (!HostIsAddress(Header(r, "Host"), r.FromUsb))
                return "Use the phone's address (http://127.0.0.1:8020 over USB, http://PHONE-IP:5001 over Wi-Fi)\n";
            return null;
        }

        /// <summary>The Host header is the phone's address: 127.0.0.1 / localhost over USB, an IP address over Wi-Fi.</summary>
        private static bool HostIsAddress(string host, bool fromUsb)
        {
            if (string.IsNullOrEmpty(host)) return true; // HTTP/1.0 clients may leave it out; browsers always send it
            string name = host;
            if (name.StartsWith("["))
            {
                int close = name.IndexOf(']');
                name = close > 0 ? name.Substring(1, close - 1) : name;
            }
            else
            {
                int colon = name.LastIndexOf(':');
                if (colon > 0) name = name.Substring(0, colon);
            }
            if (fromUsb) return name == "127.0.0.1" || name.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            return System.Net.IPAddress.TryParse(name, out _);
        }

        /// <summary>
        /// A login change from the web page needs the current password as well ("current" field): the request itself is
        /// signed with it, but a signed request captured on the network could otherwise be replayed with a new password.
        /// </summary>
        private string CheckCurrentPassword(IDictionary<string, string> only, IDictionary<string, string> all)
        {
            if (!only.ContainsKey("user") && !only.ContainsKey("password")) return null;
            if (Auth != null && Auth.IsCurrentPassword(all.TryGetValue("current", out var c) ? c : null)) return null;
            only.Remove("user"); only.Remove("password");
            return "Not changed: enter the current password to change the login";
        }

        private static string Header(HttpRequest r, string name) => r.Headers.TryGetValue(name, out var v) ? v : null;

        public static Dictionary<string, string> ParseForm(string s)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in (s ?? "").Split(new[] { '&', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string k = eq < 0 ? pair : pair.Substring(0, eq);
                string v = eq < 0 ? "" : pair.Substring(eq + 1);
                d[Unescape(k.Trim())] = Unescape(v.TrimEnd('\r'));
            }
            return d;
        }

        private static string Unescape(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

        /// <summary>Parses "GET /path?query HTTP/1.1" plus headers into a request.</summary>
        public static HttpRequest Parse(string requestLine, Dictionary<string, string> headers, string body, bool fromUsb, string remoteIp)
        {
            var parts = requestLine.Split(' ');
            string target = parts.Length > 1 ? parts[1] : "/";
            string original = target;
            // absolute form (http://host:port/path) -> path
            int scheme = target.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
            {
                int slash = target.IndexOf('/', scheme + 3);
                target = slash < 0 ? "/" : target.Substring(slash);
            }
            int q = target.IndexOf('?');
            return new HttpRequest
            {
                Method = parts[0].ToUpperInvariant(),
                Path = q < 0 ? target : target.Substring(0, q),
                Query = q < 0 ? "" : target.Substring(q + 1),
                Headers = headers,
                Body = body ?? "",
                FromUsb = fromUsb,
                Target = original,
                RemoteIp = remoteIp
            };
        }

        /// <summary>No framing by other sites (clickjacking the settings page), no guessing of content types.</summary>
        private const string SecurityHeaders = "X-Frame-Options: DENY\r\nContent-Security-Policy: frame-ancestors 'none'\r\nX-Content-Type-Options: nosniff\r\n";

        public static byte[] Reply(int code, string reason, string contentType, string body, string extraHeaders = "")
            => Reply(code, reason, contentType, Encoding.UTF8.GetBytes(body ?? ""), extraHeaders);

        public static byte[] Reply(int code, string reason, string contentType, byte[] body, string extraHeaders = "")
        {
            byte[] head = Encoding.ASCII.GetBytes(
                "HTTP/1.0 " + code + " " + reason + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                SecurityHeaders +
                extraHeaders +
                "Connection: close\r\n\r\n");
            var all = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(body, 0, all, head.Length, body.Length);
            return all;
        }
    }
}
