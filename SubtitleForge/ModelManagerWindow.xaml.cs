using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SubtitleForge.Models;
using SubtitleForge.Services;

namespace SubtitleForge
{
    /// <summary>
    /// Lists the Whisper models on disk and lets the user delete one or all of them.
    /// </summary>
    public partial class ModelManagerWindow : Window
    {
        private readonly ObservableCollection<WhisperModelRow> rows = new ObservableCollection<WhisperModelRow>();

        public ModelManagerWindow()
        {
            InitializeComponent();
            ModelList.ItemsSource = rows;
            PathText.Text = WhisperModelStore.ModelDirectory;
            Reload();
        }

        private void Reload()
        {
            rows.Clear();
            foreach (WhisperModelInfo model in WhisperModelStore.AvailableModels)
            {
                WhisperModelRow row = new WhisperModelRow(model.FileName, model.DisplayName, model.ApproximateMegabytes);
                row.Refresh();
                rows.Add(row);
            }

            UpdateSummary();
        }

        private void UpdateSummary()
        {
            int downloaded = 0;
            long totalBytes = 0L;

            foreach (WhisperModelRow row in rows)
            {
                if (row.IsDownloaded)
                {
                    downloaded++;
                    totalBytes += row.SizeBytes;
                }
            }

            SummaryText.Text = downloaded == 0
                ? "No models on disk yet — the first transcription downloads the one you pick."
                : downloaded.ToString(CultureInfo.InvariantCulture)
                    + (downloaded == 1 ? " model" : " models")
                    + " on disk, "
                    + WhisperModelRow.FormatSize(totalBytes)
                    + " total.";

            DeleteAllButton.IsEnabled = downloaded > 0;
        }

        private void DeleteModelClick(object sender, RoutedEventArgs e)
        {
            Button? button = sender as Button;
            if (button == null)
            {
                return;
            }

            WhisperModelRow? row = button.DataContext as WhisperModelRow;
            if (row == null || !row.IsDownloaded)
            {
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                this,
                "Delete " + row.DisplayName + " (" + row.SizeText + ")?\n\n"
                + "It will be downloaded again the next time you transcribe with it.",
                "Delete model",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            string error;
            if (!WhisperModelStore.DeleteModel(row.FileName, out error))
            {
                MessageBox.Show(this, error, "Could not delete the model", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            row.Refresh();
            UpdateSummary();
        }

        private void DeleteAllClick(object sender, RoutedEventArgs e)
        {
            int downloaded = 0;
            foreach (WhisperModelRow row in rows)
            {
                if (row.IsDownloaded)
                {
                    downloaded++;
                }
            }

            if (downloaded == 0)
            {
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                this,
                "Delete all " + downloaded.ToString(CultureInfo.InvariantCulture) + " downloaded model"
                + (downloaded == 1 ? "?" : "s?")
                + "\n\nThey will be downloaded again the next time you transcribe with them.",
                "Delete all models",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            List<string> failures;
            int deleted = WhisperModelStore.DeleteAllModels(out failures);

            Reload();

            if (failures.Count > 0)
            {
                MessageBox.Show(
                    this,
                    "Deleted " + deleted.ToString(CultureInfo.InvariantCulture) + " model(s), but these failed:\n\n"
                    + string.Join("\n", failures),
                    "Delete all models",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void OpenFolderClick(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = WhisperModelStore.ModelDirectory;
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = folder;
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "Could not open the folder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshClick(object sender, RoutedEventArgs e)
        {
            Reload();
        }

        private void CloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
