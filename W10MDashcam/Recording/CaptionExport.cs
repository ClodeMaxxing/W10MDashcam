using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace W10MDashcam.Recording
{
    /// <summary>
    /// Turns the caption track inside a recording (timed text, track 3, written by FragmentedMp4Writer) into an .srt
    /// subtitle file, for players that only load captions from a file next to the video. Reads only the small
    /// fragment headers and the caption samples, skipping the video, so even a 3-minute 4K file takes a moment.
    /// Works on the file being recorded too (it stops at the last complete fragment).
    /// </summary>
    internal static class CaptionExport
    {
        private const uint TextTrack = 3;
        private const int TextTimescale = 1000;

        private struct Cue { public long Start, End; public string Text; }

        /// <summary>The .srt text, or null when the recording has no caption track (recorded before version 0.5).</summary>
        public static async Task<string> ToSrtAsync(Stream s)
        {
            var cues = new List<Cue>();
            long len = s.Length, pos = 0;
            var head = new byte[16];
            while (pos + 8 <= len)
            {
                s.Position = pos;
                if (await ReadFullyAsync(s, head, 8) < 8) break;
                long size = Be32(head, 0);
                string type = Encoding.ASCII.GetString(head, 4, 4);
                int headerLen = 8;
                if (size == 1)
                {
                    if (await ReadFullyAsync(s, head, 8, 8) < 8) break;
                    size = (long)Be64(head, 8);
                    headerLen = 16;
                }
                else if (size == 0) size = len - pos;
                if (size < headerLen || pos + size > len) break; // the last, unfinished fragment of a file being written

                if (type == "moof" && size < 4 * 1024 * 1024)
                {
                    var moof = new byte[size];
                    s.Position = pos;
                    if (await ReadFullyAsync(s, moof, (int)size) < size) break;
                    await ReadMoofAsync(s, moof, pos, len, cues);
                }
                pos += size;
            }
            if (cues.Count == 0) return null;

            // A second split across two fragments arrives as two back-to-back samples with the same text: join them.
            var merged = new List<Cue>();
            foreach (var c in cues)
            {
                if (merged.Count > 0)
                {
                    var last = merged[merged.Count - 1];
                    if (last.Text == c.Text && c.Start - last.End <= 5) { last.End = Math.Max(last.End, c.End); merged[merged.Count - 1] = last; continue; }
                }
                merged.Add(c);
            }

            var sb = new StringBuilder();
            int n = 1;
            foreach (var c in merged)
            {
                if (string.IsNullOrWhiteSpace(c.Text)) continue;
                sb.Append(n++).Append("\r\n")
                  .Append(Time(c.Start)).Append(" --> ").Append(Time(c.End)).Append("\r\n")
                  .Append(c.Text.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n\r\n");
            }
            return sb.ToString();
        }

        private static async Task ReadMoofAsync(Stream s, byte[] moof, long moofPos, long fileLen, List<Cue> cues)
        {
            for (int p = 8; p + 8 <= moof.Length;)
            {
                int size = (int)Be32(moof, p);
                if (size < 8 || p + size > moof.Length) return;
                if (Type(moof, p) == "traf") await ReadTrafAsync(s, moof, p + 8, p + size, moofPos, fileLen, cues);
                p += size;
            }
        }

        private static async Task ReadTrafAsync(Stream s, byte[] b, int from, int to, long moofPos, long fileLen, List<Cue> cues)
        {
            uint track = 0;
            long baseOffset = moofPos, decodeTime = 0;
            uint defaultDuration = 0, defaultSize = 0;
            for (int p = from; p + 8 <= to;)
            {
                int size = (int)Be32(b, p);
                if (size < 8 || p + size > to) return;
                string type = Type(b, p);
                int q = p + 8;
                if (type == "tfhd")
                {
                    uint flags = Be32(b, q) & 0xFFFFFF; q += 4;
                    track = Be32(b, q); q += 4;
                    if (track != TextTrack) return;
                    if ((flags & 0x1) != 0) { baseOffset = (long)Be64(b, q); q += 8; }
                    if ((flags & 0x2) != 0) q += 4;
                    if ((flags & 0x8) != 0) { defaultDuration = Be32(b, q); q += 4; }
                    if ((flags & 0x10) != 0) { defaultSize = Be32(b, q); q += 4; }
                }
                else if (type == "tfdt")
                {
                    bool v1 = b[q] == 1; q += 4;
                    decodeTime = v1 ? (long)Be64(b, q) : Be32(b, q);
                }
                else if (type == "trun" && track == TextTrack)
                {
                    uint flags = Be32(b, q) & 0xFFFFFF; q += 4;
                    uint count = Be32(b, q); q += 4;
                    long data = baseOffset;
                    if ((flags & 0x1) != 0) { data += unchecked((int)Be32(b, q)); q += 4; }
                    if ((flags & 0x4) != 0) q += 4;
                    long t = decodeTime;
                    for (uint i = 0; i < count && q <= p + size; i++)
                    {
                        uint dur = defaultDuration, len = defaultSize;
                        if ((flags & 0x100) != 0) { dur = Be32(b, q); q += 4; }
                        if ((flags & 0x200) != 0) { len = Be32(b, q); q += 4; }
                        if ((flags & 0x400) != 0) q += 4;
                        if ((flags & 0x800) != 0) q += 4;
                        if (len >= 2 && len < 4096 && data + len <= fileLen)
                        {
                            var sample = new byte[len];
                            s.Position = data;
                            if (await ReadFullyAsync(s, sample, (int)len) == len)
                            {
                                int textLen = Math.Min((sample[0] << 8) | sample[1], (int)len - 2);
                                cues.Add(new Cue { Start = t, End = t + dur, Text = Encoding.UTF8.GetString(sample, 2, textLen) });
                            }
                        }
                        data += len;
                        t += dur;
                    }
                }
                p += size;
            }
        }

        private static string Time(long ms)
        {
            ms = ms * 1000 / TextTimescale;
            return string.Format("{0:00}:{1:00}:{2:00},{3:000}", ms / 3600000, ms / 60000 % 60, ms / 1000 % 60, ms % 1000);
        }

        private static async Task<int> ReadFullyAsync(Stream s, byte[] buf, int count, int offset = 0)
        {
            int got = 0;
            while (got < count)
            {
                int n = await s.ReadAsync(buf, offset + got, count - got);
                if (n <= 0) break;
                got += n;
            }
            return got;
        }

        private static string Type(byte[] b, int p) => Encoding.ASCII.GetString(b, p + 4, 4);
        private static uint Be32(byte[] b, int p) => (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]);
        private static ulong Be64(byte[] b, int p) => (ulong)Be32(b, p) << 32 | Be32(b, p + 4);
    }
}
