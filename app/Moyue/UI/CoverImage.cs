using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Moyue
{
    /// <summary>
    /// 2:3 cover that loads through ImageService, cancels when its container is recycled
    /// and keeps a small decoded cache so scrolling back does not decode again.
    /// </summary>
    public sealed class CoverImage : Panel
    {
        const int DecodeWidth = 300;
        const int CacheLimit = 48;
        static readonly Dictionary<string, BitmapImage> cache = new Dictionary<string, BitmapImage>();
        static readonly LinkedList<string> order = new LinkedList<string>();

        readonly Image image = new Image { Stretch = Stretch.UniformToFill };
        CancellationTokenSource loading;

        public static readonly DependencyProperty BookProperty = DependencyProperty.Register(
            "Book",
            typeof(Book),
            typeof(CoverImage),
            new PropertyMetadata(null, (d, e) => ((CoverImage)d).Load())
        );

        public Book Book
        {
            get { return (Book)GetValue(BookProperty); }
            set { SetValue(BookProperty, value); }
        }

        public CoverImage()
        {
            Background = Ui.Resource("CoverPlaceholderBrush");
            Children.Add(image);
            Unloaded += (s, e) => Cancel();
            Loaded += (s, e) =>
            {
                if (image.Source == null)
                    Load();
            };
        }

        protected override Size MeasureOverride(Size available)
        {
            double width = !double.IsNaN(Width) ? Width
                : double.IsInfinity(available.Width) ? 120
                : available.Width;
            var size = new Size(width, width * 1.5);
            image.Measure(size);
            return size;
        }

        protected override Size ArrangeOverride(Size final)
        {
            image.Arrange(new Rect(0, 0, final.Width, final.Height));
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, final.Width, final.Height) };
            return final;
        }

        void Cancel()
        {
            if (loading != null)
            {
                loading.Cancel();
                loading = null;
            }
        }

        async void Load()
        {
            Cancel();
            image.Source = null;
            var book = Book;
            if (book == null || string.IsNullOrEmpty(book.Cover) || App.Images == null)
                return;
            BitmapImage cached;
            if (cache.TryGetValue(book.Key, out cached))
            {
                Touch(book.Key);
                image.Source = cached;
                return;
            }
            var cts = new CancellationTokenSource();
            loading = cts;
            try
            {
                var file = await App.Images.Page(
                    book,
                    new Chapter { Id = "cover" },
                    new PageInfo { Url = book.Cover },
                    0,
                    false,
                    cts.Token
                );
                var bitmap = new BitmapImage { DecodePixelWidth = DecodeWidth };
                using (var stream = await file.OpenReadAsync())
                    await bitmap.SetSourceAsync(stream);
                if (cts.IsCancellationRequested || Book != book)
                    return;
                Remember(book.Key, bitmap);
                image.Source = bitmap;
            }
            catch (Exception)
            {
                // The placeholder already communicates a missing cover; lists stay usable.
            }
        }

        static void Touch(string key)
        {
            order.Remove(key);
            order.AddFirst(key);
        }

        static void Remember(string key, BitmapImage bitmap)
        {
            cache[key] = bitmap;
            Touch(key);
            while (order.Count > CacheLimit)
            {
                cache.Remove(order.Last.Value);
                order.RemoveLast();
            }
        }

        public static void ClearCache()
        {
            cache.Clear();
            order.Clear();
        }
    }
}
