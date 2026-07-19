using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using StarSensing.Dashboard.Models;
using StarSensing.Dashboard.ViewModels;

namespace StarSensing.Dashboard.Views
{
    public partial class SignalMonitorView : UserControl
    {
        private SignalMonitorViewModel? _viewModel;
        private ICollectionView? _liveView;
        private ICollectionView? _historyView;

        public SignalMonitorView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;

            _viewModel = e.NewValue as SignalMonitorViewModel;

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;

                // Live view: only online/unique entries (pruned every 2s by the VM).
                _liveView = new CollectionViewSource { Source = _viewModel.LiveNetworks }.View;
                _liveView.Filter = item => item is SelectableNetwork n && _viewModel.MatchesSearch(n);

                // History view: every identified network, most-recently-seen first.
                var historyCvs = new CollectionViewSource { Source = _viewModel.Networks };
                _historyView = historyCvs.View;
                _historyView.SortDescriptions.Add(
                    new SortDescription(nameof(SelectableNetwork.LastSeen), ListSortDirection.Descending));
                _historyView.Filter = item => item is SelectableNetwork n && _viewModel.MatchesSearch(n);

                LiveListView.ItemsSource = _liveView;
                HistoryDataGrid.ItemsSource = _historyView;
                _ = _viewModel.LoadHistoryAsync();
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SignalMonitorViewModel.SearchText)
                or nameof(SignalMonitorViewModel.ShowActiveOnly))
            {
                _liveView?.Refresh();
                _historyView?.Refresh();
            }
        }

        private void OnRate50Clicked(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_viewModel != null) _viewModel.SampleIntervalMs = 50;
        }

        private void OnRate100Clicked(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_viewModel != null) _viewModel.SampleIntervalMs = 100;
        }
    }
}
