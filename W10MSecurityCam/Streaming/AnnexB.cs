using System;
using System.Collections.Generic;

namespace W10MSecurityCam.Streaming
{
    /// <summary>Splits an H.264 buffer into NAL units. Handles Annex-B start codes, or 4-byte length prefixes as a fallback.</summary>
    internal static class AnnexB
    {
        public static List<byte[]> Split(byte[] d)
        {
            var list = new List<byte[]>();
            int n = d.Length;
            int scLen;
            int first = FindStart(d, 0, out scLen);
            if (first < 0)
            {
                int p = 0;
                while (p + 4 <= n)
                {
                    int len = d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3];
                    p += 4;
                    if (len <= 0 || p + len > n) break;
                    var nal = new byte[len];
                    Buffer.BlockCopy(d, p, nal, 0, len);
                    list.Add(nal);
                    p += len;
                }
                return list;
            }

            int start = first + scLen;
            while (start < n)
            {
                int nextLen;
                int next = FindStart(d, start, out nextLen);
                int end = next < 0 ? n : next;
                if (next >= 0) while (end > start && d[end - 1] == 0) end--; // zero belonging to a 4-byte start code
                if (end > start)
                {
                    var nal = new byte[end - start];
                    Buffer.BlockCopy(d, start, nal, 0, end - start);
                    list.Add(nal);
                }
                if (next < 0) break;
                start = next + nextLen;
            }
            return list;
        }

        private static int FindStart(byte[] d, int from, out int len)
        {
            for (int i = from; i + 2 < d.Length; i++)
            {
                if (d[i + 2] > 1) { i += 2; continue; } // fast skip
                if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1) { len = 3; return i; }
            }
            len = 0;
            return -1;
        }
    }
}
