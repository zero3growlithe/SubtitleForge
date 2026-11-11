using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SubtitleForge.Services;

namespace SubtitleForge
{
    public partial class App : Application
    {
        public static AppSettings Settings { get; private set; } = new AppSettings();

        public static string VersionDescription
        {
            get { return "build " + BuildInfo.ShortSha; }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Settings = AppSettings.Load();

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Settings.Save();
            base.OnExit(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            CrashLog.Write(e.Exception, "DispatcherUnhandledException");
            e.Handled = true;
            ShowCrashMessage(e.Exception);
        }

        private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception exception)
            {
                CrashLog.Write(exception, "AppDomain.UnhandledException");
            }
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            CrashLog.Write(e.Exception, "UnobservedTaskException");
            e.SetObserved();
        }

        private static void ShowCrashMessage(Exception exception)
        {
            string message = exception.Message
                + Environment.NewLine
                + Environment.NewLine
                + "A crash report was written to:"
                + Environment.NewLine
                + CrashLog.LogPath;

            MessageBox.Show(message, "SubtitleForge — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
