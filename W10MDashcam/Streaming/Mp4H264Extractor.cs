using System;
using System.Text;

namespace W10MDashcam.Streaming
{
    /// <summary>
    /// Reads the byte stream of an MP4 file as Windows writes it and pulls out each
    /// H.264 NAL unit (the pieces that make up a video frame) from the 'mdat' box.
    /// Windows puts some bookkeeping bytes at the start of mdat, so instead of trusting
    /// the layout we scan for data that looks like valid length-prefixed NAL units and
    /// only lock on once two in a row check out.
    /// </summary>
    internal sealed class Mp4H264Extractor
    {
        private const uint MaxNalSynced = 16 * 1024 * 1024;
        private const uint MaxNalUnsynced = 3 * 1024 * 1024;

        public event Action<byte[], int> NalReady; // (nal without start code, nal_unit_type)
        public int NalLengthSize = 4;
        public volatile bool Stopped;

        private readonly Action<string> _log;

        // box header parsing
        private bool _inMdat;
        private readonly byte[] _hdr = new byte[16];
        private int _hdrHave;
        private int _hdrNeed = 8;
        private long _skipLeft;

        // mdat payload buffer
        private byte[] _buf = new byte[1 << 20];
        private int _bufLen;
        private bool _synced;
        private long _skipped;
        private bool _dumped;
        private int _nalsLogged;
        private bool _dead;

        public Mp4H264Extractor(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        public void Feed(byte[] d, int off, int count)
        {
            if (Stopped || _dead) return;

            while (count > 0 && !_inMdat)
            {
                if (_skipLeft > 0)
                {
                    int n = (int)Math.Min(count, _skipLeft);
                    off += n; count -= n; _skipLeft -= n;
                    continue;
                }
                int take = Math.Min(count, _hdrNeed - _hdrHave);
                Buffer.BlockCopy(d, off, _hdr, _hdrHave, take);
                _hdrHave += take; off += take; count -= take;
                if (_hdrHave < _hdrNeed) return;

                uint size32 = BE32(_hdr, 0);
                string type = Encoding.UTF8.GetString(_hdr, 4, 4);
                if (size32 == 1 && _hdrNeed == 8) { _hdrNeed = 16; continue; }
                long boxSize = size32 == 1 ? (long)BE64(_hdr, 8) : size32;
                int hdrLen = _hdrNeed;
                _hdrHave = 0; _hdrNeed = 8;
                _log("MP4 box '" + type + "' (" + boxSize + " bytes)");

                if (type == "mdat") { _inMdat = true; break; }
                if (boxSize < hdrLen) { _log("Unexpected MP4 layout - stopping extractor"); _dead = true; return; }
                _skipLeft = boxSize - hdrLen;
            }

            if (count > 0 && _inMdat) FeedMdat(d, off, count);
        }

        private void FeedMdat(byte[] d, int off, int count)
        {
            if (_bufLen + count > _buf.Length)
            {
                var bigger = new byte[Math.Max(_buf.Length * 2, _bufLen + count)];
                Buffer.BlockCopy(_buf, 0, bigger, 0, _bufLen);
                _buf = bigger;
            }
            Buffer.BlockCopy(d, off, _buf, _bufLen, count);
            _bufLen += count;

            if (!_dumped && _bufLen >= 64)
            {
                _dumped = true;
                var sb = new StringBuilder("First 64 bytes of video data: ");
                for (int i = 0; i < 64; i++) sb.Append(_buf[i].ToString("X2")).Append(i % 4 == 3 ? " " : "");
                _log(sb.ToString());
            }

            int lenSize = NalLengthSize;
            int p = 0;
            while (_bufLen - p >= lenSize + 1)
            {
                uint len = ReadLen(p, lenSize);
                bool ok = Plausible(len, _buf[p + lenSize], _synced ? MaxNalSynced : MaxNalUnsynced);

                if (ok && !_synced)
                {
                    long next = (long)p + lenSize + len;
                    if (next + lenSize + 1 > _bufLen) break; // need more data to confirm
                    uint len2 = ReadLen((int)next, lenSize);
                    ok = Plausible(len2, _buf[next + lenSize], MaxNalUnsynced);
                    if (ok)
                    {
                        _synced = true;
                        _log("Locked onto H.264 data" + (_skipped > 0 ? " after skipping " + _skipped + " bytes" : ""));
                        _skipped = 0;
                    }
                }

                if (!ok)
                {
                    if (_synced)
                    {
                        _synced = false;
                        _log("Lost H.264 sync (length 0x" + len.ToString("X8") + "), rescanning");
                    }
                    p++;
                    if (++_skipped > 64L * 1024 * 1024)
                    {
                        _log("Could not find H.264 data - stopping extractor");
                        _dead = true;
                        return;
                    }
                    continue;
                }

                if (p + lenSize + len > _bufLen) break; // wait for the rest of this NAL

                var nal = new byte[len];
                Buffer.BlockCopy(_buf, p + lenSize, nal, 0, (int)len);
                p += lenSize + (int)len;

                int type = nal[0] & 0x1F;
                if (_nalsLogged < 6)
                {
                    _nalsLogged++;
                    _log("NAL #" + _nalsLogged + ": type " + type + ", " + len + " bytes");
                }
                NalReady?.Invoke(nal, type);
            }

            if (p > 0)
            {
                Buffer.BlockCopy(_buf, p, _buf, 0, _bufLen - p);
                _bufLen -= p;
            }
        }

        private static bool Plausible(uint len, byte header, uint max)
        {
            if (len < 2 || len > max) return false;
            if ((header & 0x80) != 0) return false; // forbidden_zero_bit
            int type = header & 0x1F;
            return type == 1 || type == 5 || type == 6 || type == 7 || type == 8 || type == 9;
        }

        private uint ReadLen(int p, int size)
        {
            uint v = 0;
            for (int i = 0; i < size; i++) v = (v << 8) | _buf[p + i];
            return v;
        }

        private static uint BE32(byte[] b, int o) =>
            (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

        private static ulong BE64(byte[] b, int o) =>
            ((ulong)BE32(b, o) << 32) | BE32(b, o + 4);
    }
}
