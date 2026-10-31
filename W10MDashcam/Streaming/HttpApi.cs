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
    ///   /             the app log (plain text)
    ///   /status       health as JSON (for monitoring)
    ///   /snapshot.jpg the latest picture, when snapshots are turned on (needs the login when one is set)
    ///   /config       view (GET) or change (POST, form fields) settings - only over the USB cable
    ///   /camera       focus / exposure / white balance page (changes need the stream login, or the USB cable)
    ///   /recordings   the dashcam recordings as JSON; /recordings/file?name=N[&amp;saved=1][&amp;dl=1] plays or downloads one;
    ///                 /recordings/srt?name=N[&amp;saved=1] its captions as an .srt file;
    ///                 POST /recordings/save keeps the current clip, POST /recordings/delete (name, saved) deletes one
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
        public SegmentRecorder Recorder;

        public async Task<byte[]> HandleAsync(HttpRequest r)
        {
            try
            {
                string path = r.Path.TrimEnd('/');
                // Everything needs the login except requests over the USB cable (only the PC it is plugged into can make those).
                if (Auth != null && Auth.Enabled && !r.FromUsb && !Auth.Check(r.Method, Header(r, "Authorization"), out bool gateStale))
                    return Reply(401, "Unauthorized", "text/plain", "Login required\n", Auth.Challenge(gateStale));
                if (path == "" || path == "/log")
                    return Reply(200, "OK", "text/plain; charset=utf-8", GetLog());

                if (path == "/status")
                    return Reply(200, "OK", "application/json", GetStatusJson());

                if (path == "/snapshot.jpg" || path == "/snapshot")
                {
                    if (Auth != null && !Auth.Check(r.Method, Header(r, "Authorization"), out bool stale))
                        return Reply(401, "Unauthorized", "text/plain", "Login required\n", Auth.Challenge(stale));
                    var jpg = GetSnapshotJpeg != null ? await GetSnapshotJpeg() : null;
                    if (jpg == null) return Reply(404, "Not Found", "text/plain", "Snapshots are turned off (or the camera is not streaming)\n");
                    return Reply(200, "OK", "image/jpeg", jpg, "Cache-Control: no-store\r\n");
                }

                if (path == "/camera" || path == "/camera/state" || path == "/camera/set")
                    return await HandleCameraAsync(r, path);

                if (path == "/recordings/srt")
                    return await HandleCaptionsAsync(r);

                if (path == "/recordings" || path == "/recordings/save" || path == "/recordings/delete" || path == "/recordings/lock" || path == "/recordings/unlock")
                    return await HandleRecordingsAsync(r, path);

                if (path == "/config")
                {
                    if (!r.FromUsb) return Reply(403, "Forbidden", "text/plain", "Settings can only be changed over the USB cable\n");
                    if (r.Method == "POST" && ApplyConfig != null)
                    {
                        var form = ParseForm(r.Body.Length > 0 ? r.Body : r.Query);
                        string result = await ApplyConfig(form);
                        return Reply(200, "OK", "text/plain; charset=utf-8", result + "\n\n" + GetConfig());
                    }
                    return Reply(200, "OK", "text/plain; charset=utf-8", GetConfig());
                }

                return Reply(404, "Not Found", "text/plain", "Not found. Try / (log), /status, /camera, /snapshot.jpg\n");
            }
            catch (Exception ex)
            {
                return Reply(500, "Internal Server Error", "text/plain", ex.Message + "\n");
            }
        }

        /// <summary>
        /// The camera-settings page. With a stream login set, everything here needs the login. With no login the
        /// page is view-only from the network (changes only over USB), as for /config.
        /// </summary>
        private async Task<byte[]> HandleCameraAsync(HttpRequest r, string path)
        {
            string result = null;
            bool loginSet = Auth != null && Auth.Enabled;
            bool canWrite = r.FromUsb || loginSet;
            if (loginSet && !r.FromUsb && !Auth.Check(r.Method, Header(r, "Authorization"), out bool stale))
                return Reply(401, "Unauthorized", "text/plain", "Login required\n", Auth.Challenge(stale));

            if (path == "/camera")
                return Reply(200, "OK", "text/html; charset=utf-8", CameraPage.Html, "Cache-Control: no-store\r\n");

            if (path == "/camera/set")
            {
                if (r.Method != "POST") return Reply(405, "Method Not Allowed", "text/plain", "POST only\n");
                if (!canWrite)
                    return Reply(403, "Forbidden", "text/plain", "Changes need the login (or the USB cable)\n");
                // A web page on another site could make the browser send a POST with the saved login; a custom header
                // cannot be added by such a page without our permission (we never grant it), so require it.
                if (!r.FromUsb && Header(r, "X-Requested-With") != "W10MDashcam")
                    return Reply(403, "Forbidden", "text/plain", "Use the settings page (missing X-Requested-With header)\n");
                var all = ParseForm(r.Body.Length > 0 ? r.Body : r.Query);
                var only = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var k in new[] { "camera", "focus", "exposure", "wb", "format", "bitrate", "highbw", "powersave", "stream", "engine", "user", "password", "savepower", "saveois", "savefocus", "probe", "torch", "record", "segment", "storage", "saveclip", "recordonopen", "live", "liveonopen", "location", "impact", "keepnext", "savedlimit", "replacesaved", "gps", "units", "subtitles" })
                    if (all.TryGetValue(k, out var v)) only[k] = v;
                string bad = CheckLoginFields(only);
                if (bad != null) result = bad;
                else if (ApplyConfig != null && only.Count > 0) result = await ApplyConfig(only);
            }
            string json = GetCameraJson();
            if (result != null)
            {
                string first = result.Split('\n')[0].Replace("\\", "\\\\").Replace("\"", "\\\"");
                json = json.Substring(0, json.LastIndexOf('}')) + ",\"result\":\"" + first + "\"}"; // only the outer brace: the JSON has nested objects
            }
            if (json.StartsWith("{")) json = "{\"canWrite\":" + (canWrite ? "true" : "false") + ",\"loginSet\":" + (loginSet ? "true" : "false")
                + ",\"defaultLogin\":" + (Auth != null && Auth.IsDefault ? "true" : "false") + ",\"user\":\"" + (Auth != null ? Auth.UserName.Replace("\\", "").Replace("\"", "") : "") + "\"" + (json.Length > 2 ? "," : "") + json.Substring(1);
            return Reply(200, "OK", "application/json", json, "Cache-Control: no-store\r\n");
        }

        /// <summary>The recordings list (JSON) and the save / delete actions (same rules as /camera/set).</summary>
        private async Task<byte[]> HandleRecordingsAsync(HttpRequest r, string path)
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
            bool canWrite = r.FromUsb || (Auth != null && Auth.Enabled);
            if (!canWrite) return Reply(403, "Forbidden", "text/plain", "Changes need the login (or the USB cable)\n");
            if (!r.FromUsb && Header(r, "X-Requested-With") != "W10MDashcam")
                return Reply(403, "Forbidden", "text/plain", "Use the settings page (missing X-Requested-With header)\n");
            if (path == "/recordings/save")
            {
                Recorder.SaveClip();
                return Reply(200, "OK", "text/plain", "Saving the clip\n");
            }
            var form = ParseForm(r.Body.Length > 0 ? r.Body : r.Query);
            string fname = form.TryGetValue("name", out var fn) ? fn : "";
            if (path == "/recordings/lock") { Recorder.Lock(fname); return Reply(200, "OK", "text/plain", "Keeping " + fname + "\n"); }
            if (path == "/recordings/unlock") { Recorder.Unlock(fname); return Reply(200, "OK", "text/plain", "Unlocking " + fname + "\n"); }
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
                if (Auth != null && Auth.Enabled && !r.FromUsb && !Auth.Check(r.Method, Header(r, "Authorization"), out bool stale))
                    return new FileReply { Error = Reply(401, "Unauthorized", "text/plain", "Login required\n", Auth.Challenge(stale)) };
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
                    "Content-Length: " + len + "\r\n" + extra +
                    "Connection: close\r\n\r\n");
                return new FileReply { Head = head, Body = body, Length = len };
            }
            catch (Exception ex)
            {
                return new FileReply { Error = Reply(500, "Internal Server Error", "text/plain", ex.Message + "\n") };
            }
        }

        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        /// <summary>Returns an error text when the user/password fields of a web change are not acceptable, otherwise null.</summary>
        private static string CheckLoginFields(IDictionary<string, string> f)
        {
            if (f.TryGetValue("user", out var u) && !System.Text.RegularExpressions.Regex.IsMatch(u ?? "", @"^[A-Za-z0-9._-]{1,32}$"))
            { f.Remove("user"); f.Remove("password"); return "Not changed: the user name may use letters, digits . _ - (1-32 characters)"; }
            if (f.TryGetValue("password", out var p) && (p == null || p.Length < 4 || p.Length > 64 || p.Any(ch => ch < ' ')))
            { f.Remove("user"); f.Remove("password"); return "Not changed: the password needs 4-64 characters"; }
            return null;
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
        public static HttpRequest Parse(string requestLine, Dictionary<string, string> headers, string body, bool fromUsb)
        {
            var parts = requestLine.Split(' ');
            string target = parts.Length > 1 ? parts[1] : "/";
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
                FromUsb = fromUsb
            };
        }

        public static byte[] Reply(int code, string reason, string contentType, string body, string extraHeaders = "")
            => Reply(code, reason, contentType, Encoding.UTF8.GetBytes(body ?? ""), extraHeaders);

        public static byte[] Reply(int code, string reason, string contentType, byte[] body, string extraHeaders = "")
        {
            byte[] head = Encoding.ASCII.GetBytes(
                "HTTP/1.0 " + code + " " + reason + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                extraHeaders +
                "Connection: close\r\n\r\n");
            var all = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(body, 0, all, head.Length, body.Length);
            return all;
        }
    }
}
