using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UrDatabase.Models;
using UrDatabase.Services;

namespace UrDatabase.Views
{
    /// <summary>
    /// The drives dialog. It does no checking of its own: the window that opens it says how to
    /// check and how to forget, so the dialog and the banner can never disagree about a drive.
    /// </summary>
    public partial class DrivesWindow : Window
    {
        private readonly Func<Task<IReadOnlyList<DriveReport>?>>? _recheck;
        private readonly Func<DriveReport, Task<bool>>? _forget;

        public DrivesWindow()
        {
            InitializeComponent();
        }

        private DrivesWindow(
            IReadOnlyList<DriveReport> reports,
            Func<Task<IReadOnlyList<DriveReport>?>> recheck,
            Func<DriveReport, Task<bool>> forget)
            : this()
        {
            _recheck = recheck;
            _forget = forget;
            Show(reports);
        }

        /// <param name="owner">The window the dialog belongs to.</param>
        /// <param name="reports">What the check that opened it found.</param>
        /// <param name="recheck">Runs the check again; null when it failed.</param>
        /// <param name="forget">Forgets one disk; false when that failed.</param>
        public static Task ShowAsync(
            Window owner,
            IReadOnlyList<DriveReport> reports,
            Func<Task<IReadOnlyList<DriveReport>?>> recheck,
            Func<DriveReport, Task<bool>> forget)
        {
            var dialog = new DrivesWindow(reports, recheck, forget);
            return dialog.ShowDialog(owner);
        }

        private void Show(IReadOnlyList<DriveReport> reports)
        {
            DriveList.ItemsSource = DriveRow.From(reports);

            if (reports.Count == 0) Say(DriveStatusText.Empty);
            else Say(null);
        }

        private void Say(string? message)
        {
            StatusText.Text = message ?? "";
            StatusText.IsVisible = !string.IsNullOrEmpty(message);
        }

        private async Task RecheckAsync()
        {
            if (_recheck is null) return;

            RecheckButton.IsEnabled = false;
            try
            {
                var reports = await _recheck();
                if (reports is null) Say("The drives could not be checked. See drives.log for why.");
                else Show(reports);
            }
            finally
            {
                RecheckButton.IsEnabled = true;
            }
        }

        private async void Recheck_Click(object? sender, RoutedEventArgs e) => await RecheckAsync();

        private async void Forget_Click(object? sender, RoutedEventArgs e)
        {
            if (_forget is null || sender is not Button { Tag: DriveRow row }) return;

            var name = DriveStatusText.Name(row.Report);
            var confirmed = await MessageBoxWindow.ConfirmAsync(
                this,
                "Forget this drive?",
                $"UrDatabase will stop warning you that {name} is missing. " +
                "If it is plugged in again it will be remembered again.",
                "Forget");
            if (!confirmed) return;

            if (!await _forget(row.Report))
            {
                Say($"{name} could not be forgotten. See drives.log for why.");
                return;
            }

            await RecheckAsync();
        }

        private void Close_Click(object? sender, RoutedEventArgs e) => Close();
    }
}
