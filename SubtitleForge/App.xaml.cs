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

        /// <summary>Named style / idle / pop-in profiles shared by the window and the editors.</summary>
        public static ProfileStore Profiles { get; private set; } = new ProfileStore();

        public static string VersionDescription
        {
            get { return "build " + BuildInfo.ShortSha; }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Has to happen before the first whisper.net call: it decides whether CUDA exists by
            // loading cudart64_12 by name, which only finds the toolkit if its bin folder is on the
            // DLL search path.
            CudaSearchPath.Apply();

            Settings = AppSettings.Load();
            Profiles = ProfileStore.Load();

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Settings.Save();
            Profiles.Save();
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
