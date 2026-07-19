using System.Windows;
using System.Windows.Controls;
using StarSensing.Dashboard.Services;

namespace StarSensing.Dashboard.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Global click sound for every button in the app.
            AddHandler(Button.ClickEvent, new RoutedEventHandler(OnAnyButtonClick), true);
        }

        private void OnAnyButtonClick(object sender, RoutedEventArgs e) => SoundService.Click();
    }
}
