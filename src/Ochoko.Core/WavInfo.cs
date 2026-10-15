using System;
using System.Text;

namespace Ochoko.Core;

public static class WavInfo
{
    /// <summary>PCM WAV のヘッダーから長さ(ミリ秒)を求める。読めなければ null。</summary>
    public static long? TryGetDurationMs(byte[] wav)
    {
        if (wav.Length < 12 || Tag(wav, 0) != "RIFF" || Tag(wav, 8) != "WAVE")
        {
            return null;
        }

        long? byteRate = null;
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = Tag(wav, pos);
            long size = BitConverter.ToUInt32(wav, pos + 4);
            var body = pos + 8;
            if (id == "fmt " && size >= 16 && body + 16 <= wav.Length)
            {
                byteRate = BitConverter.ToUInt32(wav, body + 8);
            }
            else if (id == "data")
            {
                if (byteRate == null || byteRate.Value == 0)
                {
                    return null;
                }

                // ストリーム書き出しのヘッダーはサイズが実体と合わないことがあるので、実際に残っている分で頭打ちにする
                var available = wav.Length - body;
                var length = Math.Min(size, available);
                return length * 1000 / byteRate.Value;
            }

            pos = body + (int)Math.Min(size + (size & 1), int.MaxValue - body);
        }

        return null;
    }

    private static string Tag(byte[] b, int offset) => Encoding.ASCII.GetString(b, offset, 4);
}
