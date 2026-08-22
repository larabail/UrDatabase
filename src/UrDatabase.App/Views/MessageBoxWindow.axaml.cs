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
        private bool _confirmed;

        public MessageBoxWindow()
        {
            InitializeComponent();
        }

        public static Task ShowAsync(Window? owner, string title, string message)
        {
            var dialog = Build(title, message);

            if (owner is null)
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.Show();
                return Task.CompletedTask;
            }

            return dialog.ShowDialog(owner);
        }

        /// <summary>
        /// Asks a yes-or-no question and returns what was chosen. Closing the window counts as no,
        /// because the only caller asks before acting on a guess and an unanswered question is not
        /// consent.
        /// </summary>
        public static async Task<bool> ConfirmAsync(Window? owner, string title, string message, string confirmText = "OK")
        {
            var dialog = Build(title, message);
            dialog.OkButton.Content = confirmText;
            dialog.CancelButton.IsVisible = true;

            if (owner is null)
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.Show();
                return false;
            }

            await dialog.ShowDialog(owner);
            return dialog._confirmed;
        }

        private static MessageBoxWindow Build(string title, string message)
        {
            var dialog = new MessageBoxWindow { Title = title };
            dialog.MessageText.Text = message;
            return dialog;
        }

        private void Ok_Click(object? sender, RoutedEventArgs e)
        {
            _confirmed = true;
            Close();
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
    }
}
