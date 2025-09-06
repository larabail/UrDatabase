using System.Diagnostics;
using System.IO;
using System.Windows;
using UrDatabase.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

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

        private void LinkFile_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = "Choose movie file",
                Filter = "Video files|*.mkv;*.mp4;*.avi;*.mov;*.wmv;*.m4v;*.mpg;*.mpeg;*.ts;*.webm|All files|*.*"
            };
            if (ofd.ShowDialog(this) == true)
            {
                // Persist link in DB if your schema supports it (files.movie_id), else just set in VM
                Vm.FilePath = ofd.FileName;
                FileNote.Text = $"File: {System.IO.Path.GetFileName(Vm.FilePath)}";

                try
                {
                    // If you have files table and want to associate, try matching by path:
                    // using var conn = Database.Open(<your db path>);  <-- you need to pass db path or inject updater
                    // await conn.ExecuteAsync("UPDATE files SET movie_id=@mid WHERE file_path=@p", new { mid = Vm.LocalId, p = Vm.FilePath });
                }
                catch { /* ignore for now; schema may not support linking yet */ }
            }
        }
    }
}
