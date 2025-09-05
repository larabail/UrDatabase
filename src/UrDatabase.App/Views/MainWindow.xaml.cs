using System.Windows;

namespace UrDatabase.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
