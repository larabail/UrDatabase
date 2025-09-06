using System.Diagnostics;
using System.IO;
using System.Windows;
using UrDatabase.Models;

namespace UrDatabase.Views
{
    public partial class MovieDetailsWindow : Window
    {
        public MovieDetailsVm Vm { get; }

        public MovieDetailsWindow(MovieDetailsVm vm)
        {
            Vm = vm;
            DataContext = Vm;
            InitializeComponent();

            // show small note about play path
            FileNote.Text = string.IsNullOrWhiteSpace(vm.FilePath)
                ? "No local file linked. Play will open nothing."
                : $"File: {Path.GetFileName(vm.FilePath)}";
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void PlayBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Vm.FilePath) || !File.Exists(Vm.FilePath))
            {
                MessageBox.Show("No playable file found for this title.", "UrDatabase",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Vm.FilePath,
                    UseShellExecute = true // open with default player
                };
                Process.Start(psi);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Could not launch file:\n{ex.Message}", "UrDatabase",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
