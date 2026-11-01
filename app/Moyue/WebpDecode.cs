using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;

namespace Moyue
{
    /// <summary>解码出的 BGRA 像素 + 尺寸。</summary>
    public sealed class DecodedImage
    {
        public byte[] Bgra;
        public int Width;
        public int Height;
    }

    /// <summary>用逆向复用的 Universal.WebP 原生 WinRT 组件解码 WebP（ARM32 可用）。</summary>
    public static class WebpDecode
    {
        /// <summary>RIFF....WEBP 魔数判断。</summary>
        public static bool IsWebp(byte[] b)
            => b != null && b.Length >= 12
               && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
               && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P';

        /// <summary>解码 WebP 字节为 BGRA8 像素 + 宽高。</summary>
        public static async Task<DecodedImage> DecodeBgraAsync(byte[] bytes)
        {
            var decoder = new Universal.WebP.WebPDecoder();
            var size = await decoder.GetSizeAsync(bytes);
            var buffer = await decoder.DecodeBgraAsync(bytes);
            return new DecodedImage
            {
                Bgra = buffer.ToArray(),
                Width = (int)size.Width,
                Height = (int)size.Height,
            };
        }
    }
}
