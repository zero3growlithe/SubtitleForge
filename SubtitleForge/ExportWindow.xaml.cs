using System;
using System.Windows;
using System.Windows.Controls;
using SubtitleForge.Services;

namespace SubtitleForge
{
    /// <summary>Collects the export toggles: video, audio, subtitle sidecar and burn-in.</summary>
    public partial class ExportWindow : Window
    {
        private readonly MediaExportOptions options = new MediaExportOptions();

        public ExportWindow()
        {
            InitializeComponent();
            UpdateOptionState();
        }

        public MediaExportOptions Options
        {
            get
            {
                return options;
            }
        }

        private void OptionChanged(object sender, RoutedEventArgs e)
        {
            UpdateOptionState();
        }

        private void BackgroundColorChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateOptionState();
        }

        private void QualityChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateOptionState();
        }

        private void UpdateOptionState()
        {
            // The combo boxes raise SelectionChanged while the XAML is still being parsed.
            if (IncludeVideoCheck == null || IncludeAudioCheck == null || ExportButton == null
                || SubtitleFormatCombo == null || BackgroundColorCombo == null
                || CustomColorBox == null || HintText == null || BurnSubtitlesCheck == null
                || IncludeSubtitlesCheck == null || ResolutionCombo == null || AudioQualityCombo == null)
            {
                return;
            }

            bool includeVideo = IncludeVideoCheck.IsChecked == true;
            bool includeAudio = IncludeAudioCheck.IsChecked == true;
            bool includeSubtitles = IncludeSubtitlesCheck.IsChecked == true;
            bool burnSubtitles = BurnSubtitlesCheck.IsChecked == true;

            SubtitleFormatCombo.IsEnabled = includeSubtitles;

            // Resolution only means something when there is a picture to scale, and audio quality
            // (or a straight copy) only when audio is kept.
            ResolutionCombo.IsEnabled = includeVideo || burnSubtitles;
            AudioQualityCombo.IsEnabled = includeAudio;
            BackgroundColorCombo.IsEnabled = burnSubtitles && !includeVideo;
            CustomColorBox.IsEnabled = BackgroundColorCombo.IsEnabled && IsCustomColor();

            bool wantsMedia = includeVideo || includeAudio || burnSubtitles;
            ExportButton.IsEnabled = wantsMedia || includeSubtitles;

            bool resizes = ResolutionCombo.IsEnabled && SelectedResolutionHeight() > 0;
            bool reEncodesAudio = includeAudio
                && !string.Equals(SelectedAudioBitrate(), "copy", StringComparison.OrdinalIgnoreCase);
            bool reEncodes = burnSubtitles || resizes || reEncodesAudio;

            if (burnSubtitles && !includeVideo)
            {
                HintText.Text = "Without video the subtitles are rendered onto a solid colour backdrop ("
                    + SelectedColor()
                    + "), which makes the result easy to key out in another program. Audio is kept when "
                    + "\"Include audio\" is ticked.";
            }
            else if (burnSubtitles)
            {
                HintText.Text = "The subtitles are burned into the video picture (re-encoded). "
                    + "The original file is never modified.";
            }
            else if (wantsMedia && !reEncodes)
            {
                HintText.Text = "Video and audio are copied without re-encoding, so this is quick.";
            }
            else if (wantsMedia)
            {
                HintText.Text = "Re-encoded to match the chosen quality. "
                    + "Pick \"Same as source\" for the audio (and for the resolution) to keep it "
                    + "byte-for-byte instead. The original file is never modified.";
            }
            else
            {
                HintText.Text = "Only the subtitle file is written.";
            }
        }

        /// <summary>0 keeps the source size; anything else is the height to scale to.</summary>
        private int SelectedResolutionHeight()
        {
            ComboBoxItem? item = ResolutionCombo.SelectedItem as ComboBoxItem;
            string? tag = item == null ? null : item.Tag as string;
            int height;
            if (!string.IsNullOrEmpty(tag) && int.TryParse(tag, out height))
            {
                return height;
            }

            return 0;
        }

        private string SelectedAudioBitrate()
        {
            ComboBoxItem? item = AudioQualityCombo.SelectedItem as ComboBoxItem;
            string? tag = item == null ? null : item.Tag as string;
            return string.IsNullOrWhiteSpace(tag) ? "320k" : tag;
        }

        private bool IsCustomColor()
        {
            ComboBoxItem? item = BackgroundColorCombo.SelectedItem as ComboBoxItem;
            if (item == null)
            {
                return false;
            }

            return string.Equals(item.Tag as string, "custom", StringComparison.Ordinal);
        }

        private string SelectedColor()
        {
            if (IsCustomColor())
            {
                return NormaliseColor(CustomColorBox.Text);
            }

            ComboBoxItem? item = BackgroundColorCombo.SelectedItem as ComboBoxItem;
            if (item == null)
            {
                return "0x00FF00";
            }

            string? tag = item.Tag as string;
            return string.IsNullOrWhiteSpace(tag) ? "0x00FF00" : tag;
        }

        private static string NormaliseColor(string raw)
        {
            string value = (raw ?? string.Empty).Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(2);
            }

            if (value.StartsWith("#", StringComparison.Ordinal))
            {
                value = value.Substring(1);
            }

            if (value.Length != 6)
            {
                return "0x00FF00";
            }

            for (int index = 0; index < value.Length; index++)
            {
                if (!Uri.IsHexDigit(value[index]))
                {
                    return "0x00FF00";
                }
            }

            return "0x" + value.ToUpperInvariant();
        }

        private void ExportClick(object sender, RoutedEventArgs e)
        {
            options.IncludeVideo = IncludeVideoCheck.IsChecked == true;
            options.IncludeAudio = IncludeAudioCheck.IsChecked == true;
            options.IncludeSubtitles = IncludeSubtitlesCheck.IsChecked == true;
            options.BurnSubtitles = BurnSubtitlesCheck.IsChecked == true;

            if (!options.NeedsMediaFile && !options.IncludeSubtitles)
            {
                MessageBox.Show(
                    this,
                    "Pick at least one thing to export.",
                    "SubtitleForge",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (options.BurnSubtitles && !options.IncludeVideo)
            {
                options.BackgroundColor = SelectedColor();
            }

            options.OutputHeight = SelectedResolutionHeight();
            options.AudioBitrate = SelectedAudioBitrate();

            ComboBoxItem? formatItem = SubtitleFormatCombo.SelectedItem as ComboBoxItem;
            string? format = formatItem == null ? null : formatItem.Tag as string;
            options.SubtitleFormat = string.IsNullOrWhiteSpace(format) ? "srt" : format;

            DialogResult = true;
            Close();
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
