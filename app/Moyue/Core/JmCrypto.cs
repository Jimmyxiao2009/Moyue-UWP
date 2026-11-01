using System;
using System.Security.Cryptography;
using System.Text;

namespace Jm.Core
{
    /// <summary>token + tokenparam 的返回值。用 struct 而非 ValueTuple，
    /// 避免老旧 Windows Phone 上缺 System.ValueTuple.dll 导致运行时加载失败。</summary>
    public readonly struct JmToken
    {
        public readonly string Token;
        public readonly string TokenParam;

        public JmToken(string token, string tokenParam)
        {
            Token = token;
            TokenParam = tokenParam;
        }

        // 保留 var (a, b) = ... 的解构写法（解构走此方法，不依赖 ValueTuple）
        public void Deconstruct(out string token, out string tokenParam)
        {
            token = Token;
            tokenParam = TokenParam;
        }
    }

    /// <summary>
    /// 禁漫移动端 API 的加解密工具。
    /// 关键：请求 token 与响应解密 key 都由"同一个时间戳 ts"推导，
    /// 因此一次请求必须自始至终用同一个 ts。
    /// </summary>
    public static class JmCrypto
    {
        // token = md5(ts + 这个常量)
        public const string TokenSecret = "18comicAPPContent";

        // 响应 data 解密 key = md5(ts + 这个常量)
        public const string DataSecret = "185Hcomic3PAPP7R";

        // 域名服务器(发布页)返回内容的解密 secret；注意它用空 ts，即 key = md5(secret)
        public const string DomainServerSecret = "diosfjckwpqpdfjkvnqQjsik";

        // 客户端版本号，跟随官方 App 变化；过旧可能被拒
        public const string AppVersion = "2.0.26";

        public static string Md5Hex(string input)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>生成请求头需要的 token 和 tokenparam。</summary>
        public static JmToken BuildToken(long ts, string version = AppVersion)
        {
            var token = Md5Hex(ts.ToString() + TokenSecret);
            var tokenParam = ts.ToString() + "," + version;
            return new JmToken(token, tokenParam);
        }

        /// <summary>
        /// 解密响应里的 data 字段（base64 → AES-ECB-256 解密 → 去 PKCS7 padding → UTF8 文本）。
        /// </summary>
        public static string DecodeResponseData(string base64Data, long ts) =>
            DecodeData(base64Data, ts.ToString(), DataSecret);

        /// <summary>
        /// 通用解密：key = md5(tsString + secret) 的 ASCII 字节(32 字节 => AES-256)。
        /// 普通响应 tsString 是时间戳；域名服务器内容 tsString 为空、secret 为域名服务器 secret。
        /// </summary>
        public static string DecodeData(string base64Data, string tsString, string secret)
        {
            var key = Encoding.UTF8.GetBytes(Md5Hex(tsString + secret));
            var cipher = Convert.FromBase64String(base64Data);
            if (cipher.Length == 0 || cipher.Length % 16 != 0)
                throw new CryptographicException("无效的 AES 密文长度");

            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None; // 手动去 padding，和 Python 实现保持一致
                using (var dec = aes.CreateDecryptor())
                {
                    var plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
                    int pad = plain[plain.Length - 1]; // PKCS7：最后一字节即填充长度
                    if (pad < 1 || pad > 16 || pad > plain.Length)
                        throw new CryptographicException("无效的 PKCS7 填充");
                    for (int i = plain.Length - pad; i < plain.Length; i++)
                        if (plain[i] != pad)
                            throw new CryptographicException("无效的 PKCS7 填充");
                    int len = plain.Length - pad;
                    return Encoding.UTF8.GetString(plain, 0, len);
                }
            }
        }
    }
}
