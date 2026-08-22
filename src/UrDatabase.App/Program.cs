using System;
using Avalonia;

namespace UrDatabase
{
    internal static class Program
    {
        // Avalonia needs an explicit entry point; WPF generated one from App.xaml.
        // STAThread is required by the Windows backend and harmless elsewhere.
        [STAThread]
        public static void Main(string[] args) => BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);

        // Referenced by name by the Avalonia XAML previewer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
