using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace W10MDashcam.Recording
{
    /// <summary>One encoded video frame: its NAL units (no start codes) and its time in 100 ns units.</summary>
    internal sealed class VideoFrame
    {
        public IList<byte[]> Nals;
        public long Time;
        public bool Key;
        public int Bytes;
        public DateTime Wall;   // when it arrived (UTC): the subtitles' clock
    }

    /// <summary>One subtitle (timed text) sample: shown from Time for Duration (both 100 ns units).</summary>
    internal sealed class TextSample
    {
        public long Time;
        public long Duration;
        public string Text;
    }

    /// <summary>One raw AAC frame (1024 samples) and its time in 100 ns units.</summary>
    internal sealed class AudioSample
    {
        public byte[] Data;
        public long Time;
    }

    /// <summary>
    /// Writes a fragmented MP4 file (H.264 video, optional AAC audio): a header (ftyp + moov) and then one
    /// moof + mdat pair per fragment. Every fragment is complete on its own, so a file cut off by a crash or a
    /// dead battery still plays up to its last whole fragment. Plain .NET (any seekable or non-seekable Stream).
    ///
    /// Not thread-safe: one writer thread only.
    /// </summary>
    internal sealed class FragmentedMp4Writer
    {
        private const int VideoTrack = 1, AudioTrack = 2, TextTrack = 3;
        private const int TextTimescale = 1000;
        private const int VideoTimescale = 90000;
        private const int MovieTimescale = 1000;
        private const int AacFrameSamples = 1024;

        private readonly Stream _out;
        private readonly byte[] _sps, _pps;
        private readonly int _width, _height;
        private readonly int _audioRate, _audioChannels;
        private readonly byte[] _audioConfig; // AudioSpecificConfig, or null for a video-only file
        private readonly long _t0;            // time (100 ns) of the first frame = decode time 0
        private long _mehdPos = -1;
        private uint _sequence;
        private long _videoEnd;               // video decode time (90 kHz) where the next fragment starts
        private long _audioNext = -1;         // audio decode time (samples) where the next audio sample goes
        private long _lastFrameDuration = VideoTimescale / 30;

        public long BytesWritten { get; private set; }
        /// <summary>Video duration written so far, in 100 ns units.</summary>
        public long Duration => _videoEnd * 10000000L / VideoTimescale;
        public bool HasAudio => _audioConfig != null;
        /// <summary>A timed-text (subtitle) track: date/time, speed, position. Set before WriteHeader.</summary>
        public bool HasText { get; set; }
        private long _textNext;               // text decode time (ms) where the next sample goes

        public FragmentedMp4Writer(Stream output, byte[] sps, byte[] pps, long firstFrameTime,
                                   byte[] audioConfig = null, int audioRate = 0, int audioChannels = 0)
        {
            _out = output;
            _sps = sps;
            _pps = pps;
            _t0 = firstFrameTime;
            var size = SpsInfo.Parse(sps);
            _width = size.Width;
            _height = size.Height;
            if (audioConfig != null && audioConfig.Length >= 2 && audioRate > 0 && audioChannels > 0)
            {
                _audioConfig = audioConfig;
                _audioRate = audioRate;
                _audioChannels = audioChannels;
            }
        }

        public int Width => _width;
        public int Height => _height;

        /// <summary>Writes ftyp + moov. Call once, before the first fragment.</summary>
        public void WriteHeader()
        {
            var b = new Box();
            b.Begin("ftyp");
            b.Fourcc("isom"); b.U32(0x200);
            b.Fourcc("isom"); b.Fourcc("iso5"); b.Fourcc("iso6"); b.Fourcc("avc1"); b.Fourcc("mp41");
            b.End();

            b.Begin("moov");
            WriteMvhd(b);
            WriteVideoTrak(b);
            if (HasAudio) WriteAudioTrak(b);
            if (HasText) WriteTextTrak(b);
            b.Begin("mvex");
            // Room for an 'mehd' (total duration). It stays a 'free' box unless Close() fills it in,
            // so a file cut off mid-recording is still valid.
            long free = b.Length;
            b.Begin("free"); b.U32(0); b.U64(0); b.End(); // 20 bytes = same size as mehd version 1
            WriteTrex(b, VideoTrack);
            if (HasAudio) WriteTrex(b, AudioTrack);
            if (HasText) WriteTrex(b, TextTrack);
            b.End(); // mvex
            b.End(); // moov

            if (_out.CanSeek) _mehdPos = _out.Position + free;
            Emit(b);
        }

        /// <summary>
        /// Writes one fragment: video frames (the first should be a keyframe), the audio that belongs to the same time span,
        /// and the time (100 ns) of the frame that follows the last one (used for the last frame's duration; -1 if unknown).
        /// </summary>
        public void WriteFragment(IList<VideoFrame> frames, IList<AudioSample> audio, long nextFrameTime, IList<TextSample> text = null)
        {
            if (frames.Count == 0) return;
            int n = frames.Count;

            // Decode times from the frames' own timestamps (no drift), durations from their differences.
            var dts = new long[n + 1];
            for (int i = 0; i < n; i++) dts[i] = ToVideoTime(frames[i].Time);
            dts[0] = _videoEnd; // stay contiguous with the previous fragment
            for (int i = 1; i < n; i++) if (dts[i] <= dts[i - 1]) dts[i] = dts[i - 1] + 1; // never go backwards
            long next = nextFrameTime >= 0 ? ToVideoTime(nextFrameTime) : -1;
            dts[n] = next > dts[n - 1] ? next : dts[n - 1] + _lastFrameDuration;
            if (n > 1) _lastFrameDuration = Math.Max(1, (dts[n - 1] - dts[0]) / (n - 1));

            // Sample payloads: 4-byte length + NAL, parameter sets and delimiters left out (they live in avcC).
            var videoSizes = new int[n];
            int videoBytes = 0;
            for (int i = 0; i < n; i++)
            {
                int s = 0;
                foreach (var nal in frames[i].Nals) if (KeepNal(nal)) s += 4 + nal.Length;
                videoSizes[i] = s;
                videoBytes += s;
            }

            int an = HasAudio && audio != null ? audio.Count : 0;
            long audioBase = 0;
            int audioBytes = 0;
            if (an > 0)
            {
                long fromTime = ToAudioTime(audio[0].Time);
                // Continue seamlessly unless the audio jumped (dropped frames, restart): then follow its clock.
                audioBase = _audioNext < 0 || Math.Abs(fromTime - _audioNext) > 2 * AacFrameSamples ? Math.Max(0, fromTime) : _audioNext;
                if (_audioNext >= 0 && audioBase < _audioNext) audioBase = _audioNext;
                foreach (var a in audio) audioBytes += a.Data.Length;
            }

            var m = new Box();
            m.Begin("moof");
            m.FullBox("mfhd", 0, 0); m.U32(++_sequence); m.End();

            // video traf
            m.Begin("traf");
            m.FullBox("tfhd", 0, 0x020000); m.U32(VideoTrack); m.End(); // default-base-is-moof
            m.FullBox("tfdt", 1, 0); m.U64((ulong)dts[0]); m.End();
            m.FullBox("trun", 0, 0x000001 | 0x000100 | 0x000200 | 0x000400);
            m.U32((uint)n);
            int videoOffsetPos = m.Length; m.U32(0);
            for (int i = 0; i < n; i++)
            {
                m.U32((uint)(dts[i + 1] - dts[i]));
                m.U32((uint)videoSizes[i]);
                m.U32(frames[i].Key ? 0x02000000u : 0x01010000u); // sync sample / depends on others, not sync
            }
            m.End(); // trun
            m.End(); // traf

            // timed text: samples back to back from where the last fragment's text ended
            int tn = HasText && text != null ? text.Count : 0;
            var textData = new List<byte[]>();
            var textDur = new List<uint>();
            long textBase = 0;
            int textBytes = 0;
            if (tn > 0)
            {
                textBase = Math.Max(_textNext, Math.Max(0, (text[0].Time - _t0) / 10000));
                long t = textBase;
                foreach (var ts in text)
                {
                    long end = Math.Max(t + 1, (ts.Time + ts.Duration - _t0) / 10000);
                    var utf = Encoding.UTF8.GetBytes(ts.Text ?? "");
                    var d = new byte[2 + utf.Length];
                    d[0] = (byte)(utf.Length >> 8); d[1] = (byte)utf.Length;
                    Buffer.BlockCopy(utf, 0, d, 2, utf.Length);
                    textData.Add(d);
                    textDur.Add((uint)(end - t));
                    textBytes += d.Length;
                    t = end;
                }
                _textNext = t;
            }

            int audioOffsetPos = -1;
            if (an > 0)
            {
                m.Begin("traf");
                m.FullBox("tfhd", 0, 0x020000 | 0x000008); m.U32(AudioTrack); m.U32(AacFrameSamples); m.End(); // default duration
                m.FullBox("tfdt", 1, 0); m.U64((ulong)audioBase); m.End();
                m.FullBox("trun", 0, 0x000001 | 0x000200);
                m.U32((uint)an);
                audioOffsetPos = m.Length; m.U32(0);
                foreach (var a in audio) m.U32((uint)a.Data.Length);
                m.End();
                m.End();
                _audioNext = audioBase + (long)an * AacFrameSamples;
            }
            int textOffsetPos = -1;
            if (tn > 0)
            {
                m.Begin("traf");
                m.FullBox("tfhd", 0, 0x020000); m.U32(TextTrack); m.End();
                m.FullBox("tfdt", 1, 0); m.U64((ulong)textBase); m.End();
                m.FullBox("trun", 0, 0x000001 | 0x000100 | 0x000200);
                m.U32((uint)tn);
                textOffsetPos = m.Length; m.U32(0);
                for (int i = 0; i < tn; i++) { m.U32(textDur[i]); m.U32((uint)textData[i].Length); }
                m.End();
                m.End();
            }
            m.End(); // moof

            int moofSize = m.Length;
            m.Patch32(videoOffsetPos, (uint)(moofSize + 8));
            if (audioOffsetPos >= 0) m.Patch32(audioOffsetPos, (uint)(moofSize + 8 + videoBytes));
            if (textOffsetPos >= 0) m.Patch32(textOffsetPos, (uint)(moofSize + 8 + videoBytes + audioBytes));

            // mdat: header, then the samples (written straight into the same buffer: one write per fragment)
            long mdatSize = 8L + videoBytes + audioBytes + textBytes;
            m.U32((uint)mdatSize); m.Fourcc("mdat");
            for (int i = 0; i < n; i++)
                foreach (var nal in frames[i].Nals)
                    if (KeepNal(nal)) { m.U32((uint)nal.Length); m.Bytes(nal); }
            if (an > 0) foreach (var a in audio) m.Bytes(a.Data);
            foreach (var d in textData) m.Bytes(d);

            Emit(m);
            _videoEnd = dts[n];
        }

        /// <summary>Fills in the total duration (when the stream can seek) and flushes. Does not close the stream.</summary>
        public void Finish()
        {
            if (_mehdPos >= 0 && _out.CanSeek)
            {
                long end = _out.Position;
                var b = new Box();
                b.FullBox("mehd", 1, 0); b.U64((ulong)(_videoEnd * MovieTimescale / VideoTimescale)); b.End();
                _out.Position = _mehdPos;
                _out.Write(b.Buffer, 0, b.Length);
                _out.Position = end;
            }
            _out.Flush();
        }

        // ---------------------------------------------------------------- helpers

        private long ToVideoTime(long t) => (t - _t0) * VideoTimescale / 10000000L;
        private long ToAudioTime(long t) => (t - _t0) * _audioRate / 10000000L;

        private static bool KeepNal(byte[] nal)
        {
            if (nal == null || nal.Length == 0) return false;
            int type = nal[0] & 0x1F;
            return type != 7 && type != 8 && type != 9;
        }

        private void Emit(Box b)
        {
            _out.Write(b.Buffer, 0, b.Length);
            _out.Flush();
            BytesWritten += b.Length;
        }

        private void WriteMvhd(Box b)
        {
            b.FullBox("mvhd", 0, 0);
            b.U32(0); b.U32(0);                 // creation / modification time
            b.U32(MovieTimescale); b.U32(0);    // timescale, duration (fragmented: in mehd)
            b.U32(0x00010000); b.U16(0x0100);   // rate 1.0, volume 1.0
            b.Zeros(10);
            WriteMatrix(b);
            b.Zeros(24);                        // pre_defined
            b.U32(HasText ? 4u : HasAudio ? 3u : 2u); // next_track_ID
            b.End();
        }

        private static void WriteMatrix(Box b)
        {
            b.U32(0x00010000); b.U32(0); b.U32(0);
            b.U32(0); b.U32(0x00010000); b.U32(0);
            b.U32(0); b.U32(0); b.U32(0x40000000);
        }

        private void WriteTkhd(Box b, int track, bool audio)
        {
            b.FullBox("tkhd", 0, 3);            // enabled, in movie
            b.U32(0); b.U32(0);
            b.U32((uint)track); b.U32(0);
            b.U32(0);                           // duration
            b.Zeros(8);
            b.U16(0); b.U16(audio ? (ushort)1 : (ushort)0); // layer, alternate group
            b.U16(audio ? (ushort)0x0100 : (ushort)0); b.U16(0);
            WriteMatrix(b);
            b.U32(audio ? 0u : (uint)_width << 16);
            b.U32(audio ? 0u : (uint)_height << 16);
            b.End();
        }

        private static void WriteMdhd(Box b, int timescale)
        {
            b.FullBox("mdhd", 0, 0);
            b.U32(0); b.U32(0);
            b.U32((uint)timescale); b.U32(0);
            b.U16(0x55C4);                      // language "und"
            b.U16(0);
            b.End();
        }

        private static void WriteHdlr(Box b, string type, string name)
        {
            b.FullBox("hdlr", 0, 0);
            b.U32(0); b.Fourcc(type); b.Zeros(12);
            b.Bytes(Encoding.ASCII.GetBytes(name)); b.U8(0);
            b.End();
        }

        private static void WriteDinf(Box b)
        {
            b.Begin("dinf");
            b.FullBox("dref", 0, 0); b.U32(1);
            b.FullBox("url ", 0, 1); b.End();   // media is in this file
            b.End();
            b.End();
        }

        private static void WriteEmptyTables(Box b)
        {
            b.FullBox("stts", 0, 0); b.U32(0); b.End();
            b.FullBox("stsc", 0, 0); b.U32(0); b.End();
            b.FullBox("stsz", 0, 0); b.U32(0); b.U32(0); b.End();
            b.FullBox("stco", 0, 0); b.U32(0); b.End();
        }

        private void WriteVideoTrak(Box b)
        {
            b.Begin("trak");
            WriteTkhd(b, VideoTrack, false);
            b.Begin("mdia");
            WriteMdhd(b, VideoTimescale);
            WriteHdlr(b, "vide", "VideoHandler");
            b.Begin("minf");
            b.FullBox("vmhd", 0, 1); b.U16(0); b.Zeros(6); b.End();
            WriteDinf(b);
            b.Begin("stbl");
            b.FullBox("stsd", 0, 0); b.U32(1);
            b.Begin("avc1");
            b.Zeros(6); b.U16(1);               // data_reference_index
            b.Zeros(16);
            b.U16((ushort)_width); b.U16((ushort)_height);
            b.U32(0x00480000); b.U32(0x00480000); // 72 dpi
            b.U32(0); b.U16(1);                 // frame_count
            b.Zeros(32);                        // compressor name
            b.U16(0x0018); b.U16(0xFFFF);       // depth, pre_defined -1
            b.Begin("avcC");
            b.U8(1); b.U8(_sps[1]); b.U8(_sps[2]); b.U8(_sps[3]);
            b.U8(0xFF);                         // 4-byte NAL lengths
            b.U8(0xE1); b.U16((ushort)_sps.Length); b.Bytes(_sps);
            b.U8(1); b.U16((ushort)_pps.Length); b.Bytes(_pps);
            int profile = _sps[1];
            if (profile == 100 || profile == 110 || profile == 122 || profile == 144)
            {
                // High profiles: chroma format and bit depths (4:2:0, 8-bit), no SPS extensions.
                b.U8(0xFC | 1); b.U8(0xF8); b.U8(0xF8); b.U8(0);
            }
            b.End(); // avcC
            b.End(); // avc1
            b.End(); // stsd
            WriteEmptyTables(b);
            b.End(); // stbl
            b.End(); // minf
            b.End(); // mdia
            b.End(); // trak
        }

        private void WriteAudioTrak(Box b)
        {
            b.Begin("trak");
            WriteTkhd(b, AudioTrack, true);
            b.Begin("mdia");
            WriteMdhd(b, _audioRate);
            WriteHdlr(b, "soun", "SoundHandler");
            b.Begin("minf");
            b.FullBox("smhd", 0, 0); b.U16(0); b.U16(0); b.End();
            WriteDinf(b);
            b.Begin("stbl");
            b.FullBox("stsd", 0, 0); b.U32(1);
            b.Begin("mp4a");
            b.Zeros(6); b.U16(1);
            b.Zeros(8);
            b.U16((ushort)_audioChannels); b.U16(16);
            b.U16(0); b.U16(0);
            b.U32((uint)_audioRate << 16);
            // esds: ES descriptor > decoder config (AAC, audio stream) > AudioSpecificConfig, + SL config
            b.FullBox("esds", 0, 0);
            int asc = _audioConfig.Length;
            int dcd = 13 + 2 + asc;              // DecoderConfigDescriptor payload
            int es = 3 + (2 + dcd) + 3;          // ES_Descriptor payload
            b.U8(0x03); b.U8((byte)es); b.U16(AudioTrack); b.U8(0);
            b.U8(0x04); b.U8((byte)dcd);
            b.U8(0x40); b.U8(0x15);              // MPEG-4 AAC, audio stream
            b.U8(0); b.U16(0);                   // buffer size
            b.U32(128000); b.U32(0);             // max / average bitrate
            b.U8(0x05); b.U8((byte)asc); b.Bytes(_audioConfig);
            b.U8(0x06); b.U8(1); b.U8(2);
            b.End(); // esds
            b.End(); // mp4a
            b.End(); // stsd
            WriteEmptyTables(b);
            b.End();
            b.End();
            b.End();
            b.End();
        }

        /// <summary>3GPP timed text (tx3g), shown by VLC, ffmpeg-based and most desktop players as a subtitle track.</summary>
        private void WriteTextTrak(Box b)
        {
            b.Begin("trak");
            b.FullBox("tkhd", 0, 3);            // enabled, in movie: shown by default where the player allows
            b.U32(0); b.U32(0);
            b.U32(TextTrack); b.U32(0);
            b.U32(0);
            b.Zeros(8);
            b.U16(0xFFFF); b.U16(0);            // layer -1 (in front of the video), alternate group
            b.U16(0); b.U16(0);
            WriteMatrix(b);
            b.U32((uint)_width << 16);
            b.U32((uint)_height << 16);
            b.End();
            b.Begin("mdia");
            WriteMdhd(b, TextTimescale);
            WriteHdlr(b, "sbtl", "SubtitleHandler");
            b.Begin("minf");
            b.FullBox("nmhd", 0, 0); b.End();
            WriteDinf(b);
            b.Begin("stbl");
            b.FullBox("stsd", 0, 0); b.U32(1);
            b.Begin("tx3g");
            b.Zeros(6); b.U16(1);               // data_reference_index
            b.U32(0);                           // display flags
            b.U8(0); b.U8(0xFF);                // left aligned, bottom
            b.U8(0); b.U8(0); b.U8(0); b.U8(0x80); // background: half-transparent black
            b.U16(0); b.U16(0); b.U16(0); b.U16(0); // default text box (player decides)
            b.U16(0); b.U16(0); b.U16(1); b.U8(0); b.U8(18); // style: chars 0-0, font 1, plain, 18
            b.U8(0xFF); b.U8(0xFF); b.U8(0xFF); b.U8(0xFF);  // white
            b.Begin("ftab"); b.U16(1); b.U16(1); b.U8(10); b.Bytes(Encoding.ASCII.GetBytes("Sans-Serif")); b.End();
            b.End(); // tx3g
            b.End(); // stsd
            WriteEmptyTables(b);
            b.End();
            b.End();
            b.End();
            b.End();
        }

        private static void WriteTrex(Box b, int track)
        {
            b.FullBox("trex", 0, 0);
            b.U32((uint)track);
            b.U32(1);                            // sample description 1
            b.U32(0); b.U32(0); b.U32(0);        // defaults: duration, size, flags (set per fragment)
            b.End();
        }

        /// <summary>A growable big-endian byte buffer with nested box sizes filled in at End().</summary>
        private sealed class Box
        {
            public byte[] Buffer = new byte[4096];
            public int Length;
            private readonly Stack<int> _starts = new Stack<int>();

            private void Room(int n)
            {
                if (Length + n <= Buffer.Length) return;
                int size = Buffer.Length * 2;
                while (size < Length + n) size *= 2;
                Array.Resize(ref Buffer, size);
            }

            public void Begin(string type) { _starts.Push(Length); U32(0); Fourcc(type); }
            public void FullBox(string type, int version, int flags) { Begin(type); U32((uint)((version << 24) | (flags & 0xFFFFFF))); }
            public void End() { int s = _starts.Pop(); Patch32(s, (uint)(Length - s)); }
            public void Patch32(int pos, uint v)
            {
                Buffer[pos] = (byte)(v >> 24); Buffer[pos + 1] = (byte)(v >> 16); Buffer[pos + 2] = (byte)(v >> 8); Buffer[pos + 3] = (byte)v;
            }
            public void U8(int v) { Room(1); Buffer[Length++] = (byte)v; }
            public void U16(int v) { Room(2); Buffer[Length++] = (byte)(v >> 8); Buffer[Length++] = (byte)v; }
            public void U32(uint v) { Room(4); Patch32(Length, v); Length += 4; }
            public void U64(ulong v) { U32((uint)(v >> 32)); U32((uint)v); }
            public void Fourcc(string s) { Room(4); for (int i = 0; i < 4; i++) Buffer[Length++] = (byte)s[i]; }
            public void Zeros(int n) { Room(n); Array.Clear(Buffer, Length, n); Length += n; }
            public void Bytes(byte[] d) { Room(d.Length); System.Buffer.BlockCopy(d, 0, Buffer, Length, d.Length); Length += d.Length; }
        }
    }

    /// <summary>Reads the picture size out of an H.264 sequence parameter set.</summary>
    internal static class SpsInfo
    {
        public struct Size { public int Width, Height; }

        public static Size Parse(byte[] sps)
        {
            // Remove emulation-prevention bytes (00 00 03 -> 00 00).
            var rbsp = new List<byte>(sps.Length);
            for (int i = 0; i < sps.Length; i++)
            {
                if (i >= 2 && sps[i] == 3 && sps[i - 1] == 0 && sps[i - 2] == 0 && rbsp.Count >= 2 && rbsp[rbsp.Count - 1] == 0 && rbsp[rbsp.Count - 2] == 0) continue;
                rbsp.Add(sps[i]);
            }
            var r = new BitReader(rbsp.ToArray(), 1); // skip the NAL header byte
            int profile = r.Bits(8);
            r.Bits(16);                                // constraint flags + level
            r.Ue();                                    // seq_parameter_set_id
            int chroma = 1;
            if (profile == 100 || profile == 110 || profile == 122 || profile == 244 || profile == 44 ||
                profile == 83 || profile == 86 || profile == 118 || profile == 128 || profile == 138 || profile == 139 || profile == 134)
            {
                chroma = r.Ue();
                if (chroma == 3) r.Bits(1);
                r.Ue(); r.Ue();                        // bit depths
                r.Bits(1);
                if (r.Bits(1) == 1)                    // scaling matrices
                    for (int i = 0; i < (chroma != 3 ? 8 : 12); i++)
                        if (r.Bits(1) == 1) SkipScalingList(r, i < 6 ? 16 : 64);
            }
            r.Ue();                                    // log2_max_frame_num
            int pocType = r.Ue();
            if (pocType == 0) r.Ue();
            else if (pocType == 1)
            {
                r.Bits(1); r.Se(); r.Se();
                int n = r.Ue();
                for (int i = 0; i < n; i++) r.Se();
            }
            r.Ue(); r.Bits(1);                         // max_num_ref_frames, gaps
            int wMbs = r.Ue() + 1, hMapUnits = r.Ue() + 1;
            int frameMbsOnly = r.Bits(1);
            if (frameMbsOnly == 0) r.Bits(1);
            r.Bits(1);                                 // direct_8x8_inference
            int cl = 0, cr = 0, ct = 0, cb = 0;
            if (r.Bits(1) == 1) { cl = r.Ue(); cr = r.Ue(); ct = r.Ue(); cb = r.Ue(); }
            int cropX = chroma == 0 || chroma == 3 ? 1 : 2;
            int cropY = (chroma == 1 ? 2 : 1) * (2 - frameMbsOnly);
            return new Size
            {
                Width = wMbs * 16 - (cl + cr) * cropX,
                Height = (2 - frameMbsOnly) * hMapUnits * 16 - (ct + cb) * cropY
            };
        }

        private static void SkipScalingList(BitReader r, int size)
        {
            int last = 8, next = 8;
            for (int j = 0; j < size; j++)
            {
                if (next != 0) next = (last + r.Se() + 256) % 256;
                last = next == 0 ? last : next;
            }
        }

        private sealed class BitReader
        {
            private readonly byte[] _d;
            private int _bit;
            public BitReader(byte[] d, int startByte) { _d = d; _bit = startByte * 8; }
            public int Bits(int n)
            {
                int v = 0;
                for (int i = 0; i < n; i++)
                {
                    int b = _bit >> 3 < _d.Length ? (_d[_bit >> 3] >> (7 - (_bit & 7))) & 1 : 0;
                    v = (v << 1) | b;
                    _bit++;
                }
                return v;
            }
            public int Ue()
            {
                int zeros = 0;
                while (Bits(1) == 0 && zeros < 31) zeros++;
                return (1 << zeros) - 1 + (zeros > 0 ? Bits(zeros) : 0);
            }
            public int Se() { int k = Ue(); return (k & 1) == 1 ? (k + 1) / 2 : -(k / 2); }
        }
    }
}
