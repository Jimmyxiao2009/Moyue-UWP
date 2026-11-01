using System.Collections.Generic;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Moyue
{
    public sealed partial class AdvancedSearchPage : Page
    {
        public AdvancedSearchPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Show(SearchState.Options);
        }

        void Show(SearchOptions o)
        {
            UseJm.IsChecked = o.Sources.Contains("jm");
            UseBoylove.IsChecked = o.Sources.Contains("boylove");
            UseManwa.IsChecked = o.Sources.Contains("manwa");
            Keywords.Text = o.Query ?? "";
            MatchAny.IsChecked = o.Any;
            MatchAll.IsChecked = !o.Any;
            Exclude.Text = o.Exclude ?? "";
            Author.Text = o.Author ?? "";
            Tags.Text = o.Tags ?? "";
            State.SelectedItem = o.State;
            if (State.SelectedIndex < 0)
                State.SelectedIndex = 0;
            Sort.SelectedItem = o.Sort;
            if (Sort.SelectedIndex < 0)
                Sort.SelectedIndex = 0;
            SourceError.Visibility = Visibility.Collapsed;
            ApplyError.Visibility = Visibility.Collapsed;
        }

        void ApplyClick(object sender, RoutedEventArgs e)
        {
            var sources = new List<string>();
            if (UseJm.IsChecked == true)
                sources.Add("jm");
            if (UseBoylove.IsChecked == true)
                sources.Add("boylove");
            if (UseManwa.IsChecked == true)
                sources.Add("manwa");
            if (sources.Count == 0)
            {
                SourceError.Visibility = Visibility.Visible;
                return;
            }
            if (
                SearchOptions.Words(Keywords.Text).Length == 0
                && SearchOptions.Words(Tags.Text).Length == 0
            )
            {
                ApplyError.Visibility = Visibility.Visible;
                return;
            }
            var o = SearchState.Options;
            o.Sources = sources;
            o.Query = Keywords.Text.Trim();
            o.Any = MatchAny.IsChecked == true;
            o.Exclude = Exclude.Text.Trim();
            o.Author = Author.Text.Trim();
            o.Tags = Tags.Text.Trim();
            o.State = (string)State.SelectedItem ?? "全部";
            o.Sort = (string)Sort.SelectedItem ?? "相关度";
            SearchState.RunPending = true;
            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        void ResetClick(object sender, RoutedEventArgs e)
        {
            var local = SearchState.Options.Local;
            SearchState.Options = new SearchOptions { Local = local };
            Show(SearchState.Options);
        }
    }
}
