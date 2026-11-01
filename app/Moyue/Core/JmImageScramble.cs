using System;
using System.Collections.Generic;

namespace Jm.Core
{
    /// <summary>一个水平条带：从源图 SrcY 处高 Height 的一条，贴到结果图 DstY 处。</summary>
    public readonly struct ImageStrip
    {
        public readonly int SrcY;
        public readonly int DstY;
        public readonly int Height;

        public ImageStrip(int srcY, int dstY, int height)
        {
            SrcY = srcY;
            DstY = dstY;
            Height = height;
        }
    }

    /// <summary>
    /// 禁漫图片乱序还原。服务器下发的图被切成 N 条水平条带并打乱上下顺序，
    /// 客户端需按 album id + 文件名算出 N，再把条带重排回去。
    /// 这里只算坐标（平台无关、可单测），UWP 层拿 ImageStrip 去 blit 即可。
    /// </summary>
    public static class JmImageScramble
    {
        public const int Scramble220980 = 220980; // 此 id 之前不打乱
        public const int Scramble268850 = 268850;
        public const int Scramble421926 = 421926;

        /// <summary>
        /// 算条带数 num。filename 是图片名（不含扩展名，如 "00001"）。
        /// 0 表示不打乱、原样显示。
        /// </summary>
        public static int GetSegmentCount(int aid, string filename, int scrambleId = Scramble220980)
        {
            if (aid < scrambleId)
                return 0;
            if (aid < Scramble268850)
                return 10;

            int x = aid < Scramble421926 ? 10 : 8;
            string s = aid.ToString() + filename;
            string hash = JmCrypto.Md5Hex(s);
            int num = hash[hash.Length - 1]; // 取 md5 十六进制串最后一个字符的 ASCII 码
            num %= x;
            num = num * 2 + 2;
            return num;
        }

        /// <summary>
        /// 给定条带数和图高，算出每条的源/目标 Y。对应 jmcomic 的 decode_and_save：
        /// 余数并入第一条（视觉上的最上面），其余条带自下而上取、自上而下贴。
        /// num &lt;= 1 时返回单条恒等映射（不打乱）。
        /// </summary>
        public static IReadOnlyList<ImageStrip> GetStrips(int num, int height)
        {
            if (num <= 1)
                return new[] { new ImageStrip(0, 0, height) };

            var strips = new List<ImageStrip>(num);
            int over = height % num;
            for (int i = 0; i < num; i++)
            {
                int move = height / num; // floor
                int ySrc = height - (move * (i + 1)) - over;
                int yDst = move * i;
                if (i == 0)
                    move += over; // 第一条吃掉余数
                else
                    yDst += over;
                strips.Add(new ImageStrip(ySrc, yDst, move));
            }
            return strips;
        }
    }
}
