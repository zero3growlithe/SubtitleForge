using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using SubtitleForge.Models;
using Path = System.IO.Path;
using SubtitleForge.Services;

namespace SubtitleForge
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<SubtitleCue> cues = new ObservableCollection<SubtitleCue>();
        private readonly SpeechTranscriber transcriber = new SpeechTranscriber();
        private readonly DispatcherTimer playbackTimer;
        private readonly List<LanguageOption> languages = new List<LanguageOption>();

        private CancellationTokenSource? operationCancellation;
        private Line? playheadLine;
        private string? videoPath;
        private double durationSeconds;
        private bool isPlaying;
        private bool isBusy;
        private bool isUpdatingCueEditor;
        private bool suppressSliderSeek;

        public MainWindow()
        {
            InitializeComponent();

            CueList.ItemsSource = cues;
            cues.CollectionChanged += OnCuesCollectionChanged;

            playbackTimer = new DispatcherTimer(DispatcherPriority.Render);
            playbackTimer.Interval = TimeSpan.FromMilliseconds(120);
            playbackTimer.Tick += OnPlaybackTimerTick;

            Title = "SubtitleForge — " + App.VersionDescription;

            LoadSettingsIntoUi();
            UpdateCueCount();
            UpdateCommandStates();
            DrawTimeline();
        }

        private SubtitleCue? SelectedCue
        {
            get { return CueList.SelectedItem as SubtitleCue; }
        }

        // ── Settings <-> UI ────────────────────────────────────────────────

        private void LoadSettingsIntoUi()
        {
            AppSettings settings = App.Settings;

            foreach (WhisperModelInfo model in WhisperModelStore.AvailableModels)
            {
                ModelComboBox.Items.Add(model);
            }

            ModelComboBox.DisplayMemberPath = "DisplayText";
            foreach (WhisperModelInfo model in WhisperModelStore.AvailableModels)
            {
                if (string.Equals(model.FileName, settings.ModelFileName, StringComparison.OrdinalIgnoreCase))
                {
                    ModelComboBox.SelectedItem = model;
                    break;
                }
            }

            if (ModelComboBox.SelectedItem == null && ModelComboBox.Items.Count > 0)
            {
                ModelComboBox.SelectedIndex = 0;
            }

            languages.Add(new LanguageOption("auto", "auto (detect)"));
            languages.Add(new LanguageOption("pl", "Polish — pl"));
            languages.Add(new LanguageOption("en", "English — en"));
            languages.Add(new LanguageOption("de", "German — de"));
            languages.Add(new LanguageOption("es", "Spanish — es"));
            languages.Add(new LanguageOption("fr", "French — fr"));
            languages.Add(new LanguageOption("it", "Italian — it"));
            languages.Add(new LanguageOption("nl", "Dutch — nl"));
            languages.Add(new LanguageOption("pt", "Portuguese — pt"));
            languages.Add(new LanguageOption("ru", "Russian — ru"));
            languages.Add(new LanguageOption("uk", "Ukrainian — uk"));
            languages.Add(new LanguageOption("cs", "Czech — cs"));
            languages.Add(new LanguageOption("ja", "Japanese — ja"));
            languages.Add(new LanguageOption("ko", "Korean — ko"));
            languages.Add(new LanguageOption("zh", "Chinese — zh"));
            languages.Add(new LanguageOption("tr", "Turkish — tr"));
            languages.Add(new LanguageOption("sv", "Swedish — sv"));

            LanguageComboBox.DisplayMemberPath = "Display";
            foreach (LanguageOption option in languages)
            {
                LanguageComboBox.Items.Add(option);
            }

            LanguageComboBox.SelectedItem = languages[0];
            foreach (LanguageOption option in languages)
            {
                if (string.Equals(option.Code, settings.Language, StringComparison.OrdinalIgnoreCase))
                {
                    LanguageComboBox.SelectedItem = option;
                    break;
                }
            }

            GpuCheckBox.IsChecked = settings.UseGpu;
            TranslateCheckBox.IsChecked = settings.TranslateToEnglish;

            MaxCharactersTextBox.Text = settings.MaximumCharactersPerCue.ToString(CultureInfo.InvariantCulture);
            MaxDurationTextBox.Text = settings.MaximumCueDurationSeconds.ToString("0.#", CultureInfo.InvariantCulture);
            FontSizeTextBox.Text = settings.FontSize.ToString(CultureInfo.InvariantCulture);
            MarginTextBox.Text = settings.MarginVertical.ToString(CultureInfo.InvariantCulture);

            RuntimeText.Text = "Runtime: unknown";
            SubtitleOverlayText.FontSize = settings.FontSize;
            SubtitleOverlayBorder.Margin = new Thickness(16, 0, 16, settings.MarginVertical);
        }

        private void ApplyUiToSettings()
        {
            AppSettings settings = App.Settings;

            WhisperModelInfo? model = ModelComboBox.SelectedItem as WhisperModelInfo;
            if (model != null)
            {
                settings.ModelFileName = model.FileName;
            }

            LanguageOption? language = LanguageComboBox.SelectedItem as LanguageOption;
            if (language != null)
            {
                settings.Language = language.Code;
            }

            settings.UseGpu = GpuCheckBox.IsChecked == true;
            settings.TranslateToEnglish = TranslateCheckBox.IsChecked == true;

            int characters;
            if (int.TryParse(MaxCharactersTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out characters))
            {
                settings.MaximumCharactersPerCue = characters;
            }

            double seconds;
            if (double.TryParse(MaxDurationTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            {
                settings.MaximumCueDurationSeconds = seconds;
            }

            int fontSize;
            if (int.TryParse(FontSizeTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out fontSize))
            {
                settings.FontSize = fontSize;
            }

            int margin;
            if (int.TryParse(MarginTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out margin))
            {
                settings.MarginVertical = margin;
            }

            SubtitleOverlayText.FontSize = settings.FontSize;
            SubtitleOverlayBorder.Margin = new Thickness(16, 0, 16, settings.MarginVertical);
            DrawTimeline();
        }

        private void ModelSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            WhisperModelInfo? model = ModelComboBox.SelectedItem as WhisperModelInfo;
            if (model == null)
            {
                return;
            }

            App.Settings.ModelFileName = model.FileName;

            bool downloaded = WhisperModelStore.IsDownloaded(model.FileName);
            SetStatus(downloaded
                ? "Model: " + model.DisplayName + " (already downloaded)."
                : "Model: " + model.DisplayName + " — it will be downloaded on the first transcription.");
        }

        private void LanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LanguageOption? option = LanguageComboBox.SelectedItem as LanguageOption;
            if (option == null)
            {
                return;
            }

            App.Settings.Language = option.Code;
        }

        // ── Open media ─────────────────────────────────────────────────────

        private void OpenVideoClick(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Title = "Open a video or audio file";
            dialog.Filter = "Media files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.wmv;*.ts;*.mts;*.mp3;*.wav;*.m4a;*.flac;*.ogg|All files|*.*";
            dialog.CheckFileExists = true;
            ApplyStartDirectory(dialog);

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            LoadVideo(dialog.FileName);
        }

        private void LoadVideo(string path)
        {
            videoPath = path;
            RememberDirectory(path);
            durationSeconds = 0.0;
            EmptyHintText.Visibility = Visibility.Collapsed;
            Title = "SubtitleForge — " + Path.GetFileName(path) + " — " + App.VersionDescription;

            try
            {
                VideoElement.Source = new Uri(path, UriKind.Absolute);
                VideoElement.Pause();
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "LoadVideo");
                SetStatus("Could not open the preview: " + exception.Message);
            }

            PositionSlider.Value = 0.0;
            PositionText.Text = SubtitleCue.FormatTimestamp(TimeSpan.Zero);
            DurationText.Text = SubtitleCue.FormatTimestamp(TimeSpan.Zero);
            PlayPauseButton.Content = "▶ Play";
            isPlaying = false;
            playbackTimer.Stop();
            SetStatus("Loaded " + Path.GetFileName(path) + " — probing duration…");
            UpdateCommandStates();
            UpdateOverlay(TimeSpan.Zero);

            _ = ProbeDurationAsync(path);
        }

        private async Task ProbeDurationAsync(string path)
        {
            try
            {
                string? ffprobe = FfmpegLocator.Resolve("ffprobe", App.Settings.FfmpegDirectory);
                if (ffprobe == null)
                {
                    SetStatus("Loaded. ffprobe was not found, so the timeline stays empty until the preview reports a duration.");
                    return;
                }

                double seconds = await MediaInspector.GetDurationSecondsAsync(ffprobe, path, CancellationToken.None).ConfigureAwait(true);
                if (seconds > 0.0)
                {
                    durationSeconds = seconds;
                    ApplyDurationToUi();
                    SetStatus("Loaded " + Path.GetFileName(path) + " (" + SubtitleCue.FormatTimestamp(TimeSpan.FromSeconds(seconds)) + ").");
                }
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "ProbeDuration");
            }
        }

        private void ApplyDurationToUi()
        {
            if (durationSeconds <= 0.0)
            {
                return;
            }

            PositionSlider.Maximum = durationSeconds;
            DurationText.Text = SubtitleCue.FormatTimestamp(TimeSpan.FromSeconds(durationSeconds));
            DrawTimeline();
        }

        private void VideoMediaOpened(object sender, RoutedEventArgs e)
        {
            try
            {
                if (VideoElement.NaturalDuration.HasTimeSpan && VideoElement.NaturalDuration.TimeSpan.TotalSeconds > 0.0
                    && durationSeconds <= 0.0)
                {
                    durationSeconds = VideoElement.NaturalDuration.TimeSpan.TotalSeconds;
                    ApplyDurationToUi();
                }

                VideoElement.Position = TimeSpan.Zero;
                VideoElement.Pause();
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "VideoMediaOpened");
            }
        }

        private void VideoMediaEnded(object sender, RoutedEventArgs e)
        {
            isPlaying = false;
            PlayPauseButton.Content = "▶ Play";
            playbackTimer.Stop();
            PositionSlider.Value = durationSeconds;
            PositionText.Text = SubtitleCue.FormatTimestamp(TimeSpan.FromSeconds(durationSeconds));
        }

        private void VideoMediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            string message = e.ErrorException != null ? e.ErrorException.Message : "unknown error";
            SetStatus("The preview engine cannot play this file (" + message + "). Transcription and burn-in still work.");
            CrashLog.WriteNote("MediaFailed: " + message);
        }

        // ── Playback ───────────────────────────────────────────────────────

        private void PlayPauseClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(videoPath))
            {
                return;
            }

            if (isPlaying)
            {
                VideoElement.Pause();
                isPlaying = false;
                PlayPauseButton.Content = "▶ Play";
                playbackTimer.Stop();
                return;
            }

            try
            {
                VideoElement.Play();
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "PlayPauseClick");
                SetStatus("The preview cannot start: " + exception.Message);
                return;
            }

            isPlaying = true;
            PlayPauseButton.Content = "❚❚ Pause";
            playbackTimer.Start();
        }

        private void StopClick(object sender, RoutedEventArgs e)
        {
            try
            {
                VideoElement.Stop();
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "StopClick");
            }

            isPlaying = false;
            PlayPauseButton.Content = "▶ Play";
            playbackTimer.Stop();
            PositionSlider.Value = 0.0;
            PositionText.Text = SubtitleCue.FormatTimestamp(TimeSpan.Zero);
            UpdateOverlay(TimeSpan.Zero);
        }

        private void OnPlaybackTimerTick(object? sender, EventArgs e)
        {
            if (!isPlaying)
            {
                playbackTimer.Stop();
                return;
            }

            TimeSpan position = VideoElement.Position;
            suppressSliderSeek = true;
            PositionSlider.Value = position.TotalSeconds;
            suppressSliderSeek = false;
            PositionText.Text = SubtitleCue.FormatTimestamp(position);
            UpdateOverlay(position);
            UpdatePlayhead(position.TotalSeconds);
        }

        private void PositionSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (suppressSliderSeek)
            {
                return;
            }

            TimeSpan position = TimeSpan.FromSeconds(PositionSlider.Value);
            PositionText.Text = SubtitleCue.FormatTimestamp(position);
            SafeSeek(position);
            UpdateOverlay(position);
            UpdatePlayhead(position.TotalSeconds);
        }

        private void SafeSeek(TimeSpan position)
        {
            try
            {
                if (VideoElement.Source != null)
                {
                    VideoElement.Position = position;
                }
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "SafeSeek");
            }
        }

        private TimeSpan CurrentPosition
        {
            get
            {
                try
                {
                    if (VideoElement.Source != null)
                    {
                        return VideoElement.Position;
                    }
                }
                catch (Exception)
                {
                    // Not opened yet.
                }

                return TimeSpan.FromSeconds(PositionSlider.Value);
            }
        }

        private void UpdateOverlay(TimeSpan position)
        {
            SubtitleCue? active = null;
            foreach (SubtitleCue cue in cues)
            {
                if (cue.Start <= position && position <= cue.End)
                {
                    active = cue;
                }
                else if (cue.Start > position)
                {
                    break;
                }
            }

            if (active == null)
            {
                SubtitleOverlayBorder.Visibility = Visibility.Collapsed;
                SubtitleOverlayText.Text = string.Empty;
                return;
            }

            SubtitleOverlayText.Text = active.Text;
            SubtitleOverlayBorder.Visibility = Visibility.Visible;
        }

        // ── Timeline strip ─────────────────────────────────────────────────

        private void DrawTimeline()
        {
            TimelineCanvas.Children.Clear();
            playheadLine = null;

            double width = TimelineCanvas.ActualWidth;
            double height = TimelineCanvas.ActualHeight;
            if (width <= 8.0 || height <= 8.0)
            {
                return;
            }

            double total = durationSeconds > 0.0 ? durationSeconds : EstimatedDuration();
            if (total <= 0.0)
            {
                return;
            }

            Brush subtle = (Brush)FindResource("SubtleTextBrush");
            Brush accent = (Brush)FindResource("AccentBrush");

            Line baseline = new Line();
            baseline.X1 = 0.0;
            baseline.X2 = width;
            baseline.Y1 = height / 2.0;
            baseline.Y2 = height / 2.0;
            baseline.Stroke = subtle;
            baseline.StrokeThickness = 1.0;
            baseline.Opacity = 0.35;
            TimelineCanvas.Children.Add(baseline);

            SubtitleCue? selected = SelectedCue;
            foreach (SubtitleCue cue in cues)
            {
                double left = (cue.Start.TotalSeconds / total) * width;
                double blockWidth = (cue.Duration.TotalSeconds / total) * width;
                if (blockWidth < 2.0)
                {
                    blockWidth = 2.0;
                }

                if (left + blockWidth > width)
                {
                    left = Math.Max(0.0, width - blockWidth);
                }

                Rectangle block = new Rectangle();
                block.Width = blockWidth;
                block.Height = 26.0;
                block.Fill = accent;
                block.Opacity = cue == selected ? 1.0 : 0.45;
                block.RadiusX = 2.0;
                block.RadiusY = 2.0;
                Canvas.SetLeft(block, left);
                Canvas.SetTop(block, (height - 26.0) / 2.0);
                TimelineCanvas.Children.Add(block);
            }

            playheadLine = new Line();
            playheadLine.Y1 = 0.0;
            playheadLine.Y2 = height;
            playheadLine.Stroke = subtle;
            playheadLine.StrokeThickness = 2.0;
            playheadLine.Opacity = 0.9;
            TimelineCanvas.Children.Add(playheadLine);
            UpdatePlayhead(CurrentPosition.TotalSeconds);
        }

        private double EstimatedDuration()
        {
            double maximum = 0.0;
            foreach (SubtitleCue cue in cues)
            {
                if (cue.End.TotalSeconds > maximum)
                {
                    maximum = cue.End.TotalSeconds;
                }
            }

            return maximum;
        }

        private void UpdatePlayhead(double positionSeconds)
        {
            if (playheadLine == null)
            {
                return;
            }

            double total = durationSeconds > 0.0 ? durationSeconds : EstimatedDuration();
            if (total <= 0.0)
            {
                return;
            }

            double width = TimelineCanvas.ActualWidth;
            double x = (positionSeconds / total) * width;
            if (x < 0.0)
            {
                x = 0.0;
            }

            if (x > width)
            {
                x = width;
            }

            playheadLine.X1 = x;
            playheadLine.X2 = x;
        }

        private void TimelineCanvasSizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawTimeline();
        }

        private void TimelineCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            double total = durationSeconds > 0.0 ? durationSeconds : EstimatedDuration();
            double width = TimelineCanvas.ActualWidth;
            if (total <= 0.0 || width <= 0.0)
            {
                return;
            }

            Point position = e.GetPosition(TimelineCanvas);
            double seconds = (position.X / width) * total;
            if (seconds < 0.0)
            {
                seconds = 0.0;
            }

            if (seconds > total)
            {
                seconds = total;
            }

            suppressSliderSeek = true;
            PositionSlider.Value = seconds;
            suppressSliderSeek = false;
            PositionText.Text = SubtitleCue.FormatTimestamp(TimeSpan.FromSeconds(seconds));
            SafeSeek(TimeSpan.FromSeconds(seconds));
            UpdateOverlay(TimeSpan.FromSeconds(seconds));
            UpdatePlayhead(seconds);
        }

        // ── Cue list and editor ────────────────────────────────────────────

        private void OnCuesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateCueCount();
            UpdateCommandStates();
            DrawTimeline();
        }

        private void UpdateCueCount()
        {
            CueCountText.Text = cues.Count.ToString(CultureInfo.InvariantCulture)
                + (cues.Count == 1 ? " cue" : " cues");
        }

        private void CueListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SubtitleCue? cue = SelectedCue;
            isUpdatingCueEditor = true;
            try
            {
                if (cue == null)
                {
                    StartEditor.Text = string.Empty;
                    EndEditor.Text = string.Empty;
                    CueTextEditor.Text = string.Empty;
                }
                else
                {
                    StartEditor.Text = SubtitleCue.FormatTimestamp(cue.Start);
                    EndEditor.Text = SubtitleCue.FormatTimestamp(cue.End);
                    CueTextEditor.Text = cue.Text;
                }
            }
            finally
            {
                isUpdatingCueEditor = false;
            }

            UpdateCommandStates();
            DrawTimeline();
        }

        private void RefreshCueEditor()
        {
            SubtitleCue? cue = SelectedCue;
            isUpdatingCueEditor = true;
            try
            {
                if (cue == null)
                {
                    StartEditor.Text = string.Empty;
                    EndEditor.Text = string.Empty;
                    CueTextEditor.Text = string.Empty;
                }
                else
                {
                    StartEditor.Text = SubtitleCue.FormatTimestamp(cue.Start);
                    EndEditor.Text = SubtitleCue.FormatTimestamp(cue.End);
                    if (!string.Equals(CueTextEditor.Text, cue.Text, StringComparison.Ordinal))
                    {
                        CueTextEditor.Text = cue.Text;
                    }
                }
            }
            finally
            {
                isUpdatingCueEditor = false;
            }
        }

        private void CueListMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            SubtitleCue? cue = SelectedCue;
            if (cue == null)
            {
                return;
            }

            suppressSliderSeek = true;
            PositionSlider.Value = cue.Start.TotalSeconds;
            suppressSliderSeek = false;
            PositionText.Text = SubtitleCue.FormatTimestamp(cue.Start);
            SafeSeek(cue.Start);
            UpdateOverlay(cue.Start);
            UpdatePlayhead(cue.Start.TotalSeconds);
        }

        private void CueTextEditorTextChanged(object sender, TextChangedEventArgs e)
        {
            if (isUpdatingCueEditor)
            {
                return;
            }

            SubtitleCue? cue = SelectedCue;
            if (cue == null)
            {
                return;
            }

            cue.Text = CueTextEditor.Text;
            DrawTimeline();
        }

        private void StartEditorLostFocus(object sender, RoutedEventArgs e)
        {
            CommitTimeEditors();
        }

        private void EndEditorLostFocus(object sender, RoutedEventArgs e)
        {
            CommitTimeEditors();
        }

        private void TimeEditorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            CommitTimeEditors();
            e.Handled = true;
        }

        private void CommitTimeEditors()
        {
            SubtitleCue? cue = SelectedCue;
            if (cue == null || isUpdatingCueEditor)
            {
                return;
            }

            TimeSpan start;
            if (SubtitleCue.TryParseTimestamp(StartEditor.Text, out start))
            {
                cue.Start = start;
            }

            TimeSpan end;
            if (SubtitleCue.TryParseTimestamp(EndEditor.Text, out end))
            {
                cue.End = end;
            }

            if (cue.End <= cue.Start)
            {
                cue.End = cue.Start + TimeSpan.FromMilliseconds(200);
            }

            CuePostProcessor.Renumber(cues);
            RefreshCueEditor();
            DrawTimeline();
            UpdateOverlay(CurrentPosition);
        }

        private void SetStartClick(object sender, RoutedEventArgs e)
        {
            SubtitleCue? cue = SelectedCue;
            if (cue == null)
            {
                return;
            }

            cue.Start = CurrentPosition;
            if (cue.End <= cue.Start)
            {
                cue.End = cue.Start + TimeSpan.FromMilliseconds(400);
            }

            RefreshCueEditor();
            DrawTimeline();
            UpdateOverlay(CurrentPosition);
        }

        private void SetEndClick(object sender, RoutedEventArgs e)
        {
            SubtitleCue? cue = SelectedCue;
            if (cue == null)
            {
                return;
            }

            cue.End = CurrentPosition;
            if (cue.End <= cue.Start)
            {
                cue.Start = cue.End - TimeSpan.FromMilliseconds(400);
                if (cue.Start < TimeSpan.Zero)
                {
                    cue.Start = TimeSpan.Zero;
                }
            }

            RefreshCueEditor();
            DrawTimeline();
            UpdateOverlay(CurrentPosition);
        }

        private void NudgeBackClick(object sender, RoutedEventArgs e)
        {
            NudgeSelected(TimeSpan.FromMilliseconds(-100));
        }

        private void NudgeForwardClick(object sender, RoutedEventArgs e)
        {
            NudgeSelected(TimeSpan.FromMilliseconds(100));
        }

        private void NudgeSelected(TimeSpan delta)
        {
            SubtitleCue? cue = SelectedCue;
            if (cue == null)
            {
                return;
            }

            CuePostProcessor.ShiftCue(cue, delta);
            RefreshCueEditor();
            DrawTimeline();
            UpdateOverlay(CurrentPosition);
        }

        private void SplitCueClick(object sender, RoutedEventArgs e)
        {
            SubtitleCue? cue = SelectedCue;
            if (cue == null)
            {
                return;
            }

            int index = cues.IndexOf(cue);
            List<SubtitleCue> parts = CuePostProcessor.SplitCue(cue);
            cues.RemoveAt(index);
            for (int partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                cues.Insert(index + partIndex, parts[partIndex]);
            }

            CuePostProcessor.Renumber(cues);
            CueList.SelectedIndex = index;
            RefreshCueEditor();
            DrawTimeline();
        }

        private void MergeCueClick(object sender, RoutedEventArgs e)
        {
            SubtitleCue? cue = SelectedCue;
            if (cue == null)
            {
                return;
            }

            int index = cues.IndexOf(cue);
            if (index < 0 || index + 1 >= cues.Count)
            {
                SetStatus("There is no following cue to merge with.");
                return;
            }

            SubtitleCue merged = CuePostProcessor.Merge(cue, cues[index + 1]);
            cues.RemoveAt(index);
            cues.RemoveAt(index);
            cues.Insert(index, merged);
            CuePostProcessor.Renumber(cues);
            CueList.SelectedIndex = index;
            RefreshCueEditor();
            DrawTimeline();
        }

        private void AddCueClick(object sender, RoutedEventArgs e)
        {
            TimeSpan start = CurrentPosition;
            SubtitleCue cue = new SubtitleCue(start, start + TimeSpan.FromSeconds(2.0), "New cue");
            int index = cues.Count;
            for (int cueIndex = 0; cueIndex < cues.Count; cueIndex++)
            {
                if (cues[cueIndex].Start > start)
                {
                    index = cueIndex;
                    break;
                }
            }

            cues.Insert(index, cue);
            CuePostProcessor.Renumber(cues);
            CueList.SelectedIndex = index;
            RefreshCueEditor();
            CueTextEditor.Focus();
            CueTextEditor.SelectAll();
            DrawTimeline();
        }

        private void DeleteCueClick(object sender, RoutedEventArgs e)
        {
            SubtitleCue? cue = SelectedCue;
            if (cue == null)
            {
                return;
            }

            int index = cues.IndexOf(cue);
            cues.Remove(cue);
            CuePostProcessor.Renumber(cues);
            if (cues.Count > 0)
            {
                CueList.SelectedIndex = Math.Min(index, cues.Count - 1);
            }

            RefreshCueEditor();
            UpdateOverlay(CurrentPosition);
            DrawTimeline();
        }

        // ── Transcription ──────────────────────────────────────────────────

        private async void TranscribeClick(object sender, RoutedEventArgs e)
        {
            if (isBusy)
            {
                return;
            }

            if (string.IsNullOrEmpty(videoPath))
            {
                SetStatus("Open a video file first.");
                return;
            }

            ApplyUiToSettings();

            string? ffmpeg = FfmpegLocator.Resolve("ffmpeg", App.Settings.FfmpegDirectory);
            if (ffmpeg == null)
            {
                string message = "ffmpeg.exe was not found.\n\nInstall ffmpeg and either put it on PATH or set \"FfmpegDirectory\" in:\n"
                    + AppSettings.SettingsPath;
                SetStatus("ffmpeg was not found.");
                MessageBox.Show(this, message, "SubtitleForge", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (cues.Count > 0)
            {
                MessageBoxResult answer = MessageBox.Show(
                    this,
                    "Replace the current " + cues.Count.ToString(CultureInfo.InvariantCulture) + " cues with a fresh transcription?",
                    "SubtitleForge",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            WhisperModelInfo? model = ModelComboBox.SelectedItem as WhisperModelInfo;
            if (model == null)
            {
                SetStatus("Choose a Whisper model first.");
                return;
            }

            string audioPath = Path.Combine(
                Path.GetTempPath(),
                "SubtitleForge",
                "audio-" + Guid.NewGuid().ToString("N") + ".wav");

            operationCancellation = new CancellationTokenSource();
            CancellationToken token = operationCancellation.Token;
            SetBusy(true);
            SetProgress(0.0);

            try
            {
                string? folder = Path.GetDirectoryName(audioPath);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                SetStatus("Preparing " + model.DisplayName + "…");
                IProgress<string> modelStatus = new Progress<string>(SetStatus);
                IProgress<double> downloadProgress = new Progress<double>(delegate (double fraction)
                {
                    SetProgress(fraction * 0.10);
                });

                string modelPath = await WhisperModelStore
                    .EnsureModelAsync(model.FileName, modelStatus, downloadProgress, token)
                    .ConfigureAwait(true);

                if (durationSeconds <= 0.0)
                {
                    string? ffprobe = FfmpegLocator.Resolve("ffprobe", App.Settings.FfmpegDirectory);
                    if (ffprobe != null)
                    {
                        double probed = await MediaInspector
                            .GetDurationSecondsAsync(ffprobe, videoPath, token)
                            .ConfigureAwait(true);
                        if (probed > 0.0)
                        {
                            durationSeconds = probed;
                            ApplyDurationToUi();
                        }
                    }
                }

                SetStatus("Extracting audio…");
                IProgress<double> extractProgress = new Progress<double>(delegate (double fraction)
                {
                    SetProgress(0.10 + (fraction * 0.15));
                });

                await AudioTrackExtractor
                    .ExtractAsync(ffmpeg, videoPath, audioPath, durationSeconds, extractProgress, token)
                    .ConfigureAwait(true);

                SetStatus("Transcribing… (first run also loads the model, this can take a moment)");
                TranscriptionRequest request = new TranscriptionRequest();
                request.WavPath = audioPath;
                request.ModelPath = modelPath;
                request.Language = App.Settings.Language;
                request.TranslateToEnglish = App.Settings.TranslateToEnglish;
                request.UseGpu = App.Settings.UseGpu;
                request.SkipNonSpeech = App.Settings.SkipNonSpeech;
                request.Threads = App.Settings.Threads;

                IProgress<double> transcriptionProgress = new Progress<double>(delegate (double fraction)
                {
                    SetProgress(0.25 + (fraction * 0.75));
                });

                TranscriptionResult result = await transcriber
                    .TranscribeAsync(request, transcriptionProgress, new Progress<string>(SetStatus), token)
                    .ConfigureAwait(true);

                SetStatus("Cleaning up cues…");
                CueSplitOptions options = new CueSplitOptions();
                options.MaximumCharactersPerCue = App.Settings.MaximumCharactersPerCue;
                options.MaximumCueDurationSeconds = App.Settings.MaximumCueDurationSeconds;
                options.MinimumCueDurationSeconds = App.Settings.MinimumCueDurationSeconds;
                options.MinimumGapSeconds = App.Settings.MinimumGapSeconds;

                List<SubtitleCue> normalized = CuePostProcessor.Normalize(result.Cues, options);

                cues.Clear();
                foreach (SubtitleCue cue in normalized)
                {
                    cues.Add(cue);
                }

                CuePostProcessor.Renumber(cues);
                RuntimeText.Text = "Runtime: " + result.RuntimeDescription;
                SetProgress(1.0);
                DrawTimeline();

                string language = string.IsNullOrEmpty(result.DetectedLanguage) ? string.Empty : ", language " + result.DetectedLanguage;
                SetStatus(
                    "Done: " + cues.Count.ToString(CultureInfo.InvariantCulture) + " cues in "
                    + result.Elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture)
                    + " on " + result.RuntimeDescription + language + ".");

                if (cues.Count > 0)
                {
                    CueList.SelectedIndex = 0;
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus("Transcription cancelled.");
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "Transcribe");
                SetStatus("Transcription failed: " + exception.Message);
                MessageBox.Show(
                    this,
                    exception.Message + "\n\nDetails were written to:\n" + CrashLog.LogPath,
                    "SubtitleForge — transcription failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
                if (operationCancellation != null)
                {
                    operationCancellation.Dispose();
                    operationCancellation = null;
                }

                TryDeleteFile(audioPath);
            }
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            if (operationCancellation == null)
            {
                return;
            }

            SetStatus("Cancelling…");
            operationCancellation.Cancel();
        }

        // ── Export ─────────────────────────────────────────────────────────

        private void SaveSrtClick(object sender, RoutedEventArgs e)
        {
            SaveSubtitles("SRT subtitles|*.srt", ".srt");
        }

        private void SaveVttClick(object sender, RoutedEventArgs e)
        {
            SaveSubtitles("WebVTT subtitles|*.vtt", ".vtt");
        }

        private void SaveAssClick(object sender, RoutedEventArgs e)
        {
            SaveSubtitles("Advanced SubStation Alpha|*.ass", ".ass");
        }

        private void SaveSubtitles(string filter, string extension)
        {
            if (!EnsureCuesAvailable())
            {
                return;
            }

            ApplyUiToSettings();

            SaveFileDialog dialog = new SaveFileDialog();
            dialog.Title = "Save subtitles";
            dialog.Filter = filter;
            dialog.DefaultExt = extension;
            dialog.FileName = BuildDefaultName(extension);
            dialog.AddExtension = true;
            dialog.OverwritePrompt = true;
            ApplyStartDirectory(dialog);

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                RememberDirectory(dialog.FileName);
                if (extension == ".srt")
                {
                    SubtitleWriter.SaveSrt(dialog.FileName, cues);
                }
                else if (extension == ".vtt")
                {
                    SubtitleWriter.SaveVtt(dialog.FileName, cues);
                }
                else
                {
                    SubtitleWriter.SaveAss(dialog.FileName, cues, BuildAssStyle());
                }

                SetStatus("Saved " + Path.GetFileName(dialog.FileName) + " (" + cues.Count.ToString(CultureInfo.InvariantCulture) + " cues).");
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "SaveSubtitles");
                SetStatus("Could not save: " + exception.Message);
                MessageBox.Show(this, exception.Message, "SubtitleForge — save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BurnClick(object sender, RoutedEventArgs e)
        {
            if (isBusy || !EnsureCuesAvailable())
            {
                return;
            }

            ApplyUiToSettings();

            string? ffmpeg = FfmpegLocator.Resolve("ffmpeg", App.Settings.FfmpegDirectory);
            if (ffmpeg == null)
            {
                SetStatus("ffmpeg was not found — burn-in needs it.");
                MessageBox.Show(
                    this,
                    "ffmpeg.exe was not found.\n\nInstall ffmpeg and either put it on PATH or set \"FfmpegDirectory\" in:\n"
                    + AppSettings.SettingsPath,
                    "SubtitleForge",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(videoPath))
            {
                SetStatus("Open the video you want to burn into first.");
                return;
            }

            SaveFileDialog dialog = new SaveFileDialog();
            dialog.Title = "Save video with burned-in subtitles";
            dialog.Filter = "MP4 video|*.mp4";
            dialog.DefaultExt = ".mp4";
            dialog.FileName = Path.GetFileNameWithoutExtension(videoPath) + ".subtitled.mp4";
            dialog.AddExtension = true;
            dialog.OverwritePrompt = true;
            ApplyStartDirectory(dialog);

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            string outputPath = dialog.FileName;
            RememberDirectory(outputPath);

            operationCancellation = new CancellationTokenSource();
            CancellationToken token = operationCancellation.Token;
            SetBusy(true);
            SetProgress(0.0);

            try
            {
                SetStatus("Burning " + cues.Count.ToString(CultureInfo.InvariantCulture) + " cues into "
                    + Path.GetFileName(outputPath) + "… (re-encoding video, this takes a while)");

                IProgress<double> progress = new Progress<double>(SetProgress);
                await SubtitleBurner.BurnAsync(
                    ffmpeg,
                    videoPath,
                    outputPath,
                    cues,
                    BuildAssStyle(),
                    App.Settings.BurnPreset,
                    App.Settings.BurnCrf,
                    durationSeconds,
                    progress,
                    token).ConfigureAwait(true);

                SetProgress(1.0);
                SetStatus("Burned subtitles into " + Path.GetFileName(outputPath) + ".");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Burn-in cancelled.");
                TryDeleteFile(outputPath);
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "BurnIn");
                SetStatus("Burn-in failed: " + exception.Message);
                MessageBox.Show(
                    this,
                    exception.Message + "\n\nDetails were written to:\n" + CrashLog.LogPath,
                    "SubtitleForge — burn-in failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
                if (operationCancellation != null)
                {
                    operationCancellation.Dispose();
                    operationCancellation = null;
                }
            }
        }

        private void ImportSubtitlesClick(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Title = "Open existing subtitles";
            dialog.Filter = "Subtitle files|*.srt;*.vtt;*.txt|All files|*.*";
            dialog.CheckFileExists = true;
            ApplyStartDirectory(dialog);

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                List<SubtitleCue> loaded = SubtitleReader.ReadFile(dialog.FileName);
                if (loaded.Count == 0)
                {
                    SetStatus("No cues were found in " + Path.GetFileName(dialog.FileName) + ".");
                    return;
                }

                if (cues.Count > 0)
                {
                    MessageBoxResult answer = MessageBox.Show(
                        this,
                        "Replace the current " + cues.Count.ToString(CultureInfo.InvariantCulture) + " cues?",
                        "SubtitleForge",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (answer != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }

                RememberDirectory(dialog.FileName);
                cues.Clear();
                foreach (SubtitleCue cue in loaded)
                {
                    cues.Add(cue);
                }

                CuePostProcessor.Renumber(cues);
                DrawTimeline();
                UpdateOverlay(CurrentPosition);
                SetStatus("Loaded " + cues.Count.ToString(CultureInfo.InvariantCulture) + " cues from " + Path.GetFileName(dialog.FileName) + ".");

                if (cues.Count > 0)
                {
                    CueList.SelectedIndex = 0;
                }
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "ImportSubtitles");
                SetStatus("Could not read the subtitle file: " + exception.Message);
            }
        }

        private AssStyleOptions BuildAssStyle()
        {
            AssStyleOptions style = new AssStyleOptions();
            style.FontName = App.Settings.FontName;
            style.FontSize = App.Settings.FontSize;
            style.OutlineThickness = App.Settings.OutlineThickness;
            style.MarginVertical = App.Settings.MarginVertical;
            return style;
        }

        // ── Shared helpers ─────────────────────────────────────────────────

        private bool EnsureCuesAvailable()
        {
            if (cues.Count > 0)
            {
                return true;
            }

            SetStatus("There are no cues to export yet — transcribe or import subtitles first.");
            return false;
        }

        private string BuildDefaultName(string extension)
        {
            string baseName = string.IsNullOrEmpty(videoPath)
                ? "subtitles"
                : Path.GetFileNameWithoutExtension(videoPath);
            return baseName + extension;
        }

        private void ApplyStartDirectory(FileDialog dialog)
        {
            string? directory = App.Settings.LastDirectory;
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                dialog.InitialDirectory = directory;
            }
        }

        private void RememberDirectory(string filePath)
        {
            try
            {
                App.Settings.LastDirectory = Path.GetDirectoryName(filePath);
            }
            catch (Exception)
            {
                // Non critical.
            }
        }

        private void SetStatus(string message)
        {
            StatusText.Text = message;
        }

        private void SetProgress(double value)
        {
            if (value < 0.0)
            {
                value = 0.0;
            }

            if (value > 1.0)
            {
                value = 1.0;
            }

            ProgressIndicator.Value = value;
        }

        private void SetBusy(bool busy)
        {
            isBusy = busy;
            CancelButton.IsEnabled = busy;
            UpdateCommandStates();
        }

        private void UpdateCommandStates()
        {
            bool hasCues = cues.Count > 0;
            bool hasVideo = !string.IsNullOrEmpty(videoPath);

            TranscribeButton.IsEnabled = !isBusy && hasVideo;
            OpenVideoButton.IsEnabled = !isBusy;
            ImportSubtitlesButton.IsEnabled = !isBusy;
            ModelComboBox.IsEnabled = !isBusy;
            LanguageComboBox.IsEnabled = !isBusy;
            GpuCheckBox.IsEnabled = !isBusy;
            TranslateCheckBox.IsEnabled = !isBusy;

            SaveSrtButton.IsEnabled = !isBusy && hasCues;
            SaveVttButton.IsEnabled = !isBusy && hasCues;
            SaveAssButton.IsEnabled = !isBusy && hasCues;
            BurnButton.IsEnabled = !isBusy && hasCues && hasVideo;

            SplitCueButton.IsEnabled = !isBusy && CueList.SelectedItem != null;
            MergeCueButton.IsEnabled = !isBusy && CueList.SelectedItem != null;
            DeleteCueButton.IsEnabled = !isBusy && CueList.SelectedItem != null;
            AddCueButton.IsEnabled = !isBusy;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Temp cleanup is best effort.
            }
        }

        private void WindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (isBusy && operationCancellation != null)
            {
                MessageBoxResult answer = MessageBox.Show(
                    this,
                    "An operation is still running. Cancel it and close?",
                    "SubtitleForge",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }

                operationCancellation.Cancel();
            }

            ApplyUiToSettings();
            App.Settings.Save();
            playbackTimer.Stop();
        }

        /// <summary>
        /// Whisper language code plus the label shown in the combo box.
        /// </summary>
        private class LanguageOption
        {
            public LanguageOption(string code, string display)
            {
                Code = code;
                Display = display;
            }

            public string Code { get; }

            public string Display { get; }
        }
    }
}
