using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UrDatabase.Views
{
    /// <summary>
    /// Avalonia has no built-in MessageBox, so this stands in for WPF's <c>MessageBox.Show</c>.
    /// </summary>
    public partial class MessageBoxWindow : Window
    {
        public MessageBoxWindow()
        {
            InitializeComponent();
        }

        public static Task ShowAsync(Window? owner, string title, string message)
        {
            var dialog = new MessageBoxWindow { Title = title };
            dialog.MessageText.Text = message;

            if (owner is null)
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.Show();
                return Task.CompletedTask;
            }

            return dialog.ShowDialog(owner);
        }

        private void Ok_Click(object? sender, RoutedEventArgs e) => Close();
    }
}
