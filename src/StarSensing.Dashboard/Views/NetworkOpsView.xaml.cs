using System.Windows;
using System.Windows.Controls;
using StarSensing.Dashboard.ViewModels;

namespace StarSensing.Dashboard.Views
{
    public partial class NetworkOpsView : UserControl
    {
        public NetworkOpsView()
        {
            InitializeComponent();
        }

        // PasswordBox.Password can't be bound directly; push it to the VM on change.
        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is NetworkOpsViewModel vm && sender is PasswordBox box)
                vm.Password = box.Password;
        }
    }
}
