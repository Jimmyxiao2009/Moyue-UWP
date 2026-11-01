using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Moyue
{
    public static class PixelTransforms
    {
        public static byte[] DecryptManwa(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length % 16 != 0)
                throw new FormatException("漫蛙加密图片长度无效");
            using (var aes = Aes.Create())
            {
                aes.Key = Encoding.UTF8.GetBytes("my2ecret782ecret");
                aes.IV = aes.Key;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                using (var d = aes.CreateDecryptor())
                    return d.TransformFinalBlock(bytes, 0, bytes.Length);
            }
        }

        public static byte[] Reorder(
            byte[] src,
            int width,
            int height,
            int parts,
            bool horizontal,
            CancellationToken ct
        )
        {
            if (src == null || width < 1 || height < 1 || (long)width * height * 4 != src.Length)
                throw new ArgumentException("无效的 BGRA 像素");
            if (parts <= 1 || (horizontal && height >= 4000))
                return src;
            if (parts > (horizontal ? width : height) || parts > 128)
                throw new FormatException("无效的图片重排条带数");
            var dst = new byte[src.Length];
            if (!horizontal)
                foreach (var strip in Jm.Core.JmImageScramble.GetStrips(parts, height))
                {
                    ct.ThrowIfCancellationRequested();
                    Buffer.BlockCopy(
                        src,
                        strip.SrcY * width * 4,
                        dst,
                        strip.DstY * width * 4,
                        strip.Height * width * 4
                    );
                }
            else
            {
                int part = width / parts;
                for (int i = 1; i <= parts; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    int length = i == parts ? width - part * (parts - 1) : part;
                    int from = i == parts ? 0 : width - part * i;
                    int to = i == parts ? width - length : part * (i - 1);
                    for (int y = 0; y < height; y++)
                        Buffer.BlockCopy(
                            src,
                            (y * width + from) * 4,
                            dst,
                            (y * width + to) * 4,
                            length * 4
                        );
                }
            }
            return dst;
        }

        public static void CheckBudget(
            int width,
            int height,
            int encodedLength,
            bool rearrange,
            bool oldDevice
        )
        {
            long pixels = (long)width * height;
            // Include source + destination + encoder/native scratch and encoded buffers.
            long estimate = pixels * 4 * (rearrange ? 3 : 2) + (long)encodedLength * 2;
            long budget = oldDevice ? 64L * 1024 * 1024 : 160L * 1024 * 1024;
            if (width < 1 || height < 1 || estimate > budget)
                throw new InvalidOperationException("图片超过解码内存预算");
        }

        public static bool Jpeg(byte[] b)
        {
            return b != null
                && b.Length >= 4
                && b[0] == 255
                && b[1] == 216
                && b[b.Length - 2] == 255
                && b[b.Length - 1] == 217;
        }

        public static string Hash(byte[] b)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(b).Select(v => v.ToString("x2")));
        }
    }
}
