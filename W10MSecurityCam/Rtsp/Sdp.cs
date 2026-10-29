using System;
using System.Text;
using W10MSecurityCam.Streaming;

namespace W10MSecurityCam.Rtsp
{
    /// <summary>Builds the session description returned by DESCRIBE: one H.264 video track (RFC 6184).</summary>
    internal static class Sdp
    {
        public const string TrackControl = "trackID=0";
        public const string AudioTrackControl = "trackID=1";

        public static string Build(string localIp, ulong sessionId, byte[] sps, byte[] pps, AudioFormat audio = null)
        {
            // profile-level-id = profile_idc, constraint flags, level_idc = SPS bytes 1..3
            string profileLevelId = sps.Length >= 4
                ? sps[1].ToString("X2") + sps[2].ToString("X2") + sps[3].ToString("X2")
                : "42E01F";
            var sb = new StringBuilder();
            sb.Append("v=0\r\n");
            sb.Append("o=- ").Append(sessionId).Append(" 1 IN IP4 ").Append(localIp).Append("\r\n");
            sb.Append("s=W10MSecurityCam\r\n");
            sb.Append("c=IN IP4 0.0.0.0\r\n");
            sb.Append("t=0 0\r\n");
            sb.Append("a=tool:W10MSecurityCam\r\n");
            sb.Append("a=control:*\r\n");
            sb.Append("a=range:npt=0-\r\n");
            sb.Append("m=video 0 RTP/AVP 96\r\n");
            sb.Append("a=rtpmap:96 H264/90000\r\n");
            sb.Append("a=fmtp:96 packetization-mode=1;profile-level-id=").Append(profileLevelId)
              .Append(";sprop-parameter-sets=").Append(Convert.ToBase64String(sps))
              .Append(',').Append(Convert.ToBase64String(pps)).Append("\r\n");
            sb.Append("a=control:").Append(TrackControl).Append("\r\n");
            if (audio != null && audio.Config != null && audio.Config.Length >= 2)
            {
                // AAC-LC, RFC 3640 (mpeg4-generic, AAC-hbr): what VLC, ffmpeg and NVRs expect from IP cameras.
                var cfg = new StringBuilder();
                foreach (var b in audio.Config) cfg.Append(b.ToString("X2"));
                sb.Append("m=audio 0 RTP/AVP 97\r\n");
                sb.Append("a=rtpmap:97 mpeg4-generic/").Append(audio.SampleRate).Append('/').Append(audio.Channels).Append("\r\n");
                sb.Append("a=fmtp:97 streamtype=5;profile-level-id=1;mode=AAC-hbr;sizelength=13;indexlength=3;indexdeltalength=3;config=")
                  .Append(cfg).Append("\r\n");
                sb.Append("a=control:").Append(AudioTrackControl).Append("\r\n");
            }
            return sb.ToString();
        }
    }
}
