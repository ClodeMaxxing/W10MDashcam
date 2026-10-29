using System;
using System.Collections.Generic;

namespace W10MSecurityCam.Rtsp
{
    /// <summary>
    /// Splits one H.264 frame into RTP payloads per RFC 6184 (packetization-mode=1):
    /// a NAL unit that fits goes in one packet as-is; a bigger one is cut into FU-A fragments.
    /// The payloads are built once per frame and shared by every viewer; each viewer adds its own RTP header.
    /// </summary>
    internal static class RtpPacketizer
    {
        /// <summary>Largest RTP payload. 1400 + 12 (RTP) + 20 (TCP) + 20 (IP) stays under a 1500-byte Ethernet/Wi-Fi MTU.</summary>
        public const int MaxPayload = 1400;

        public static List<byte[]> Packetize(IList<byte[]> nals, byte[] sps, byte[] pps, bool keyframe)
        {
            var payloads = new List<byte[]>();

            // Send SPS + PPS in-band in front of every keyframe, so a viewer (or an NVR cutting a new
            // recording segment) can always start decoding there, even if it ignored the SDP.
            if (keyframe && sps != null && pps != null)
            {
                bool hasSps = false, hasPps = false;
                foreach (var n in nals)
                {
                    int t = n[0] & 0x1F;
                    if (t == 7) hasSps = true;
                    if (t == 8) hasPps = true;
                }
                if (!hasSps) payloads.Add(sps);
                if (!hasPps) payloads.Add(pps);
            }

            foreach (var nal in nals)
            {
                if (nal.Length <= MaxPayload)
                {
                    payloads.Add(nal);
                    continue;
                }

                byte indicator = (byte)((nal[0] & 0xE0) | 28); // F + NRI from the NAL, type 28 = FU-A
                byte type = (byte)(nal[0] & 0x1F);
                const int chunk = MaxPayload - 2;
                int pos = 1; // the original NAL header byte is carried in the FU indicator/header instead
                while (pos < nal.Length)
                {
                    int len = Math.Min(chunk, nal.Length - pos);
                    var p = new byte[len + 2];
                    p[0] = indicator;
                    p[1] = (byte)(type | (pos == 1 ? 0x80 : 0) | (pos + len == nal.Length ? 0x40 : 0)); // S and E bits
                    Buffer.BlockCopy(nal, pos, p, 2, len);
                    payloads.Add(p);
                    pos += len;
                }
            }
            return payloads;
        }

        /// <summary>
        /// RTP payload for one AAC frame, RFC 3640 mode AAC-hbr: AU-headers-length (16 bits), one AU header
        /// (13-bit size, 3-bit index 0), then the raw AAC frame.
        /// </summary>
        public static byte[] AacPayload(byte[] frame)
        {
            int size = Math.Min(frame.Length, 8191);
            var p = new byte[4 + size];
            p[0] = 0; p[1] = 16;                 // one 16-bit AU header
            p[2] = (byte)(size >> 5);
            p[3] = (byte)((size & 0x1F) << 3);
            Buffer.BlockCopy(frame, 0, p, 4, size);
            return p;
        }

        /// <summary>One RTP packet per payload, for sending as separate UDP datagrams. Marker bit on the last one.</summary>
        public static List<byte[]> BuildPackets(List<byte[]> payloads, ushort firstSeq, uint timestamp, uint ssrc, byte payloadType = 96)
        {
            var list = new List<byte[]>(payloads.Count);
            ushort seq = firstSeq;
            for (int i = 0; i < payloads.Count; i++)
            {
                var p = payloads[i];
                var b = new byte[12 + p.Length];
                b[0] = 0x80;
                b[1] = (byte)((i == payloads.Count - 1 ? 0x80 : 0) | payloadType);
                b[2] = (byte)(seq >> 8); b[3] = (byte)seq;
                b[4] = (byte)(timestamp >> 24); b[5] = (byte)(timestamp >> 16); b[6] = (byte)(timestamp >> 8); b[7] = (byte)timestamp;
                b[8] = (byte)(ssrc >> 24); b[9] = (byte)(ssrc >> 16); b[10] = (byte)(ssrc >> 8); b[11] = (byte)ssrc;
                Buffer.BlockCopy(p, 0, b, 12, p.Length);
                list.Add(b);
                seq++;
            }
            return list;
        }

        /// <summary>
        /// Builds the bytes for one frame as sent over an RTSP TCP connection: each RTP packet framed as
        /// '$', channel, 16-bit length (RFC 2326 section 10.12). The marker bit is set on the last packet.
        /// Uses sequence numbers firstSeq .. firstSeq + payloads.Count - 1.
        /// </summary>
        public static byte[] BuildInterleaved(List<byte[]> payloads, byte channel, ushort firstSeq, uint timestamp, uint ssrc, byte payloadType = 96)
        {
            int total = 0;
            foreach (var p in payloads) total += 4 + 12 + p.Length;
            var buf = new byte[total];
            int o = 0;
            ushort seq = firstSeq;
            for (int i = 0; i < payloads.Count; i++)
            {
                var p = payloads[i];
                int rtpLen = 12 + p.Length;
                buf[o++] = (byte)'$';
                buf[o++] = channel;
                buf[o++] = (byte)(rtpLen >> 8);
                buf[o++] = (byte)rtpLen;
                buf[o++] = 0x80;                                                  // V=2, no padding/extension/CSRC
                buf[o++] = (byte)((i == payloads.Count - 1 ? 0x80 : 0) | payloadType); // marker + payload type
                buf[o++] = (byte)(seq >> 8);
                buf[o++] = (byte)seq;
                buf[o++] = (byte)(timestamp >> 24);
                buf[o++] = (byte)(timestamp >> 16);
                buf[o++] = (byte)(timestamp >> 8);
                buf[o++] = (byte)timestamp;
                buf[o++] = (byte)(ssrc >> 24);
                buf[o++] = (byte)(ssrc >> 16);
                buf[o++] = (byte)(ssrc >> 8);
                buf[o++] = (byte)ssrc;
                Buffer.BlockCopy(p, 0, buf, o, p.Length);
                o += p.Length;
                seq++;
            }
            return buf;
        }
    }
}
