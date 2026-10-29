using System.Collections.Generic;

namespace W10MSecurityCam.Streaming
{
    internal sealed class H264Config
    {
        public int NalLengthSize = 4;
        public List<byte[]> Sps = new List<byte[]>();
        public List<byte[]> Pps = new List<byte[]>();
        public int Profile;
        public int Level;
    }

    /// <summary>
    /// Finds the 'avcC' record in a finished MP4 file. It holds the SPS/PPS
    /// "decoder setup" packets a viewer needs before it can show any picture.
    /// </summary>
    internal static class AvcConfigReader
    {
        public static H264Config Find(byte[] file)
        {
            // moov (with avcC) is normally at the end, so search backwards.
            for (int i = file.Length - 8; i >= 4; i--)
            {
                if (file[i] != (byte)'a' || file[i + 1] != (byte)'v' || file[i + 2] != (byte)'c' || file[i + 3] != (byte)'C')
                    continue;
                var cfg = TryParse(file, i - 4);
                if (cfg != null) return cfg;
            }
            return null;
        }

        private static H264Config TryParse(byte[] b, int boxStart)
        {
            try
            {
                int boxSize = b[boxStart] << 24 | b[boxStart + 1] << 16 | b[boxStart + 2] << 8 | b[boxStart + 3];
                int p = boxStart + 8;
                int end = boxStart + boxSize;
                if (boxSize < 15 || end > b.Length || b[p] != 1) return null;

                var cfg = new H264Config { Profile = b[p + 1], Level = b[p + 3], NalLengthSize = (b[p + 4] & 3) + 1 };
                int numSps = b[p + 5] & 0x1F;
                p += 6;
                for (int s = 0; s < numSps; s++)
                {
                    int len = b[p] << 8 | b[p + 1]; p += 2;
                    if (p + len > end) return null;
                    var sps = new byte[len];
                    System.Buffer.BlockCopy(b, p, sps, 0, len);
                    cfg.Sps.Add(sps); p += len;
                }
                int numPps = b[p++];
                for (int s = 0; s < numPps; s++)
                {
                    int len = b[p] << 8 | b[p + 1]; p += 2;
                    if (p + len > end) return null;
                    var pps = new byte[len];
                    System.Buffer.BlockCopy(b, p, pps, 0, len);
                    cfg.Pps.Add(pps); p += len;
                }
                if (cfg.Sps.Count == 0 || cfg.Pps.Count == 0) return null;
                if ((cfg.Sps[0][0] & 0x1F) != 7 || (cfg.Pps[0][0] & 0x1F) != 8) return null;
                return cfg;
            }
            catch
            {
                return null;
            }
        }
    }
}
