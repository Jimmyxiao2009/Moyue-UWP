using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Moyue
{
    public sealed class WindowsImageCodec : IImageCodec
    {
        public Task<byte[]> ToJpeg(
            byte[] bytes,
            PageInfo p,
            Settings settings,
            CancellationToken ct
        )
        {
            return Task.Run(() => Convert(bytes, p, settings, ct), ct);
        }

        async Task<byte[]> Convert(
            byte[] bytes,
            PageInfo p,
            Settings settings,
            CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();
            if (p.Encrypted)
                bytes = PixelTransforms.DecryptManwa(bytes);
            byte[] pixels;
            int width,
                height;
            if (WebpDecode.IsWebp(bytes))
            {
                // WebP 走原生组件；单独记这一步的异常，区分"下载失败"和"解码失败"。
                try
                {
                    var native = new Universal.WebP.WebPDecoder();
                    var size = await native.GetSizeAsync(bytes);
                    width = (int)size.Width;
                    height = (int)size.Height;
                    PixelTransforms.CheckBudget(
                        width,
                        height,
                        bytes.Length,
                        p.Strips > 1,
                        settings.OldDevice
                    );
                    ct.ThrowIfCancellationRequested();
                    pixels = (await native.DecodeBgraAsync(bytes)).ToArray();
                }
                catch (Exception ex)
                {
                    Log.E("codec", "WebP 原生解码失败 " + bytes.Length + "B", ex);
                    throw;
                }
            }
            else
                using (var memory = new InMemoryRandomAccessStream())
                {
                    await memory.WriteAsync(bytes.AsBuffer());
                    memory.Seek(0);
                    var decoder = await BitmapDecoder.CreateAsync(memory);
                    width = (int)decoder.PixelWidth;
                    height = (int)decoder.PixelHeight;
                    PixelTransforms.CheckBudget(
                        width,
                        height,
                        bytes.Length,
                        p.Strips > 1,
                        settings.OldDevice
                    );
                    ct.ThrowIfCancellationRequested();
                    pixels = (
                        await decoder.GetPixelDataAsync(
                            BitmapPixelFormat.Bgra8,
                            BitmapAlphaMode.Ignore,
                            new BitmapTransform(),
                            ExifOrientationMode.IgnoreExifOrientation,
                            ColorManagementMode.DoNotColorManage
                        )
                    ).DetachPixelData();
                }
            pixels = PixelTransforms.Reorder(pixels, width, height, p.Strips, p.Horizontal, ct);
            using (var output = new InMemoryRandomAccessStream())
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Ignore,
                    (uint)width,
                    (uint)height,
                    96,
                    96,
                    pixels
                );
                await encoder.FlushAsync();
                ct.ThrowIfCancellationRequested();
                var result = new byte[(int)output.Size];
                output.Seek(0);
                await output.ReadAsync(
                    result.AsBuffer(),
                    (uint)result.Length,
                    InputStreamOptions.None
                );
                return result;
            }
        }
    }
}
