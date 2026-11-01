using System.IO;

namespace Jm.Core
{
    /// <summary>图片 CDN 域名与图片地址构造。图片在独立的 image 域名上，和 API 域名不同。</summary>
    public static class JmImageUrl
    {
        // 图片 CDN 域名（同样会轮换，对齐 jmcomic 上游 DOMAIN_IMAGE_LIST）
        public static readonly string[] DefaultImageDomains =
        {
            "cdn-msp.jmapiproxy1.cc",
            "cdn-msp.jmapiproxy2.cc",
            "cdn-msp2.jmapiproxy2.cc",
            "cdn-msp3.jmapiproxy2.cc",
            "cdn-msp.jmapinodeudzn.net",
            "cdn-msp3.jmapinodeudzn.net",
        };

        /// <summary>
        /// 构造单张图片地址：https://{domain}/media/photos/{photoId}/{filename}
        /// filename 形如 "00001.webp"（含扩展名）。
        /// </summary>
        public static string Build(string domain, string photoId, string filename) =>
            "https://" + domain + "/media/photos/" + photoId + "/" + filename;

        /// <summary>专辑封面地址：https://{domain}/media/albums/{albumId}_3x4.jpg（封面不乱序）。</summary>
        public static string Cover(string domain, string albumId) =>
            "https://" + domain + "/media/albums/" + albumId + "_3x4.jpg";

        /// <summary>用默认图片域名拼封面地址，方便直接绑定到 Image.Source。</summary>
        public static string CoverDefault(string albumId) =>
            string.IsNullOrEmpty(albumId) ? null : Cover(DefaultImageDomains[0], albumId);

        /// <summary>去掉扩展名，乱序还原算 num 时用的是不含扩展名的文件名。</summary>
        public static string NameWithoutExt(string filename)
        {
            if (string.IsNullOrEmpty(filename))
                return filename;
            var name = Path.GetFileNameWithoutExtension(filename);
            return string.IsNullOrEmpty(name) ? filename : name;
        }
    }
}
