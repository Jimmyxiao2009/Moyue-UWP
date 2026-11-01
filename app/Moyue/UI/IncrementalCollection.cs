using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI.Xaml.Data;

namespace Moyue
{
    /// <summary>
    /// Lets ListView fetch the next page by itself when the user scrolls near the end,
    /// the standard Windows 10 Mobile list paging pattern.
    /// </summary>
    public sealed class IncrementalCollection<T> : ObservableCollection<T>, ISupportIncrementalLoading
    {
        public Func<bool> CanLoadMore;
        public Func<Task> LoadMore;

        public bool HasMoreItems
        {
            get { return CanLoadMore != null && LoadMore != null && CanLoadMore(); }
        }

        public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
        {
            return AsyncInfo.Run(async cancel =>
            {
                int before = Count;
                await LoadMore();
                return new LoadMoreItemsResult { Count = (uint)Math.Max(0, Count - before) };
            });
        }
    }
}
