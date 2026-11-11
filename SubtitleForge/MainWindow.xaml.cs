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
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using SubtitleForge.Controls;
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

        private readonly ObservableCollection<TimelineTrack> tracks = new ObservableCollection<TimelineTrack>();
        private readonly UndoStack undoStack = new UndoStack();
        private readonly MicrophoneRecorder microphoneRecorder = new MicrophoneRecorder();

        private TimelineSegment? recordingSegment;
        private string? projectPath;
        private string lastRuntimeDescription = string.Empty;
        private string lastDetectedLanguage = string.Empty;
        private bool suppressTimelineBuild;
        private bool suppressCudaCheck;
        private short[]? recordingUndoSamples;
        private float[]? recordingUndoPeaks;
        private double recordingUndoDuration;
        private int recordingStartSample;
        private int recordingWritten;
        private int nextAudioTrackNumber = 1;
        private bool isRecording;
        private bool timelineInitialized;
        private double frameStepSeconds = 1.0 / 30.0;

        public MainWindow()
        {
            InitializeComponent();

            CueList.ItemsSource = cues;
            cues.CollectionChanged += OnCuesCollectionChanged;

            playbackTimer = new DispatcherTimer(DispatcherPriority.Render);
            playbackTimer.Interval = TimeSpan.FromMilliseconds(120);
            playbackTimer.Tick += OnPlaybackTimerTick;

            Title = "SubtitleForge — " + App.VersionDescription;

            TrackTimelineView.Tracks = tracks;
            undoStack.Changed += OnUndoStackChanged;
            microphoneRecorder.SamplesAvailable += OnMicrophoneSamples;

            LoadSettingsIntoUi();
            UpdateCueCount();
            UpdateCommandStates();
            DrawTimeline();
            UpdateTimelineDuration();
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

            languages.Add(new LanguageOption("auto", "Auto detect"));
            languages.Add(new LanguageOption("pl", "Polish"));
            languages.Add(new LanguageOption("en", "English"));
            languages.Add(new LanguageOption("de", "German"));
            languages.Add(new LanguageOption("es", "Spanish"));
            languages.Add(new LanguageOption("fr", "French"));
            languages.Add(new LanguageOption("it", "Italian"));
            languages.Add(new LanguageOption("nl", "Dutch"));
            languages.Add(new LanguageOption("pt", "Portuguese"));
            languages.Add(new LanguageOption("ru", "Russian"));
            languages.Add(new LanguageOption("uk", "Ukrainian"));
            languages.Add(new LanguageOption("cs", "Czech"));
            languages.Add(new LanguageOption("ja", "Japanese"));
            languages.Add(new LanguageOption("ko", "Korean"));
            languages.Add(new LanguageOption("zh", "Chinese"));
            languages.Add(new LanguageOption("tr", "Turkish"));
            languages.Add(new LanguageOption("sv", "Swedish"));

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

            suppressCudaCheck = true;
            GpuCheckBox.IsChecked = settings.UseGpu;
            suppressCudaCheck = false;
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

        private void ModelsClick(object sender, RoutedEventArgs e)
        {
            ModelManagerWindow dialog = new ModelManagerWindow();
            dialog.Owner = this;
            dialog.ShowDialog();
            SetStatus("Models folder: " + WhisperModelStore.ModelDirectory);
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
            if (isRecording)
            {
                StopRecording();
            }

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

            timelineInitialized = false;
            tracks.Clear();
            undoStack.Clear();
            nextAudioTrackNumber = 1;
            frameStepSeconds = 1.0 / 30.0;
            TrackTimelineView.PlayheadSeconds = 0.0;
            TrackTimelineView.TotalDuration = 1.0;
            UpdateTimelineDuration();
            UpdateCommandStates();

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
                    await EnsureTimelineAsync().ConfigureAwait(true);
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
            UpdateTimelineDuration();
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

                if (!timelineInitialized)
                {
                    _ = EnsureTimelineAsync();
                }
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
            TogglePlayPause();
        }

        private void TogglePlayPause()
        {
            if (string.IsNullOrEmpty(videoPath))
            {
                return;
            }

            if (isRecording)
            {
                SetStatus("Stop the recording before playing the video.");
                return;
            }

            if (isPlaying)
            {
                PausePlayback();
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

        private void PausePlayback()
        {
            try
            {
                VideoElement.Pause();
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "PausePlayback");
            }

            isPlaying = false;
            PlayPauseButton.Content = "▶ Play";
            playbackTimer.Stop();
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
                if (VideoElement.Source == null)
                {
                    return;
                }

                VideoElement.Position = position;

                // MediaElement does not repaint a frame for a Position change while it is
                // paused, so nudge it through a play/pause cycle to force the new frame out.
                if (!isPlaying)
                {
                    VideoElement.Play();
                    VideoElement.Pause();
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
            TrackTimelineView.PlayheadSeconds = positionSeconds;

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

                await WhisperModelStore
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

                SetStatus("Collecting audio clips…");
                List<TimelineSegment> audioSegments = AudioSegmentsForTranscription();
                if (audioSegments.Count == 0)
                {
                    // No timeline yet (or no audio lanes): fall back to the video's own audio.
                    TimelineSegment fallback = new TimelineSegment(TrackKind.Audio);
                    fallback.StartSeconds = 0.0;
                    fallback.SourceOffsetSeconds = 0.0;
                    fallback.DurationSeconds = durationSeconds;
                    fallback.SourcePath = videoPath;
                    audioSegments.Add(fallback);
                }

                SetProgress(0.10);
                DateTime startedUtc = DateTime.UtcNow;

                List<SubtitleCue> combined = await TranscribeSegmentsAsync(
                    audioSegments,
                    Path.GetDirectoryName(audioPath) ?? Path.GetTempPath(),
                    token).ConfigureAwait(true);

                TimeSpan elapsed = DateTime.UtcNow - startedUtc;
                SetStatus("Cleaning up cues…");
                CueSplitOptions options = new CueSplitOptions();
                options.MaximumCharactersPerCue = App.Settings.MaximumCharactersPerCue;
                options.MaximumCueDurationSeconds = App.Settings.MaximumCueDurationSeconds;
                options.MinimumCueDurationSeconds = App.Settings.MinimumCueDurationSeconds;
                options.MinimumGapSeconds = App.Settings.MinimumGapSeconds;

                List<SubtitleCue> normalized = CuePostProcessor.Normalize(combined, options);

                cues.Clear();
                foreach (SubtitleCue cue in normalized)
                {
                    cues.Add(cue);
                }

                CuePostProcessor.Renumber(cues);
                RuntimeText.Text = "Runtime: " + lastRuntimeDescription;
                SetProgress(1.0);
                DrawTimeline();

                string language = string.IsNullOrEmpty(lastDetectedLanguage) ? string.Empty : ", language " + lastDetectedLanguage;
                SetStatus(
                    "Done: " + cues.Count.ToString(CultureInfo.InvariantCulture) + " cues from "
                    + audioSegments.Count.ToString(CultureInfo.InvariantCulture) + " clip(s) in "
                    + elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture)
                    + " on " + lastRuntimeDescription + language + ".");

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

        // ── Multitrack timeline ────────────────────────────────────────────

        private async Task EnsureTimelineAsync()
        {
            if (suppressTimelineBuild || timelineInitialized || string.IsNullOrEmpty(videoPath) || durationSeconds <= 0.0)
            {
                return;
            }

            timelineInitialized = true;
            string path = videoPath;
            double total = durationSeconds;

            TimelineTrack videoTrack = new TimelineTrack(TrackKind.Video, "Video", TrackColors.VideoDefault);
            TimelineSegment videoSegment = new TimelineSegment(TrackKind.Video);
            videoSegment.StartSeconds = 0.0;
            videoSegment.SourceOffsetSeconds = 0.0;
            videoSegment.DurationSeconds = total;
            videoSegment.SourceDuration = total;
            videoSegment.SourcePath = path;
            videoSegment.IsSelected = true;
            videoTrack.Segments.Add(videoSegment);
            tracks.Add(videoTrack);

            TimelineTrack audioTrack = new TimelineTrack(
                TrackKind.Audio,
                "Audio " + nextAudioTrackNumber.ToString(CultureInfo.InvariantCulture),
                TrackColors.AudioDefault);
            nextAudioTrackNumber++;

            TimelineSegment audioSegment = new TimelineSegment(TrackKind.Audio);
            audioSegment.StartSeconds = 0.0;
            audioSegment.SourceOffsetSeconds = 0.0;
            audioSegment.DurationSeconds = total;
            audioSegment.SourceDuration = total;
            audioSegment.SourcePath = path;
            audioSegment.IsSelected = true;
            audioTrack.Segments.Add(audioSegment);
            tracks.Add(audioTrack);

            UpdateTimelineDuration();
            UpdateCommandStates();
            SetStatus("Building the timeline…");

            string? ffmpeg = FfmpegLocator.Resolve("ffmpeg", App.Settings.FfmpegDirectory);
            string? ffprobe = FfmpegLocator.Resolve("ffprobe", App.Settings.FfmpegDirectory);

            if (ffprobe != null)
            {
                try
                {
                    double rate = await MediaInspector.GetFrameRateAsync(ffprobe, path, CancellationToken.None).ConfigureAwait(true);
                    if (rate > 0.5)
                    {
                        frameStepSeconds = 1.0 / rate;
                    }
                }
                catch (Exception exception)
                {
                    CrashLog.Write(exception, "FrameRateProbe");
                }
            }

            if (ffmpeg == null)
            {
                SetStatus("ffmpeg was not found, so the timeline has no waveform and no preview frames.");
                return;
            }

            try
            {
                short[] samples = await AudioSampleLoader.LoadAsync(ffmpeg, path, CancellationToken.None).ConfigureAwait(true);
                if (!string.Equals(videoPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                audioSegment.Samples = samples;
                audioSegment.RebuildPeaks();
                TrackTimelineView.Refresh();
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "TimelineAudio");
                SetStatus("The audio waveform could not be built: " + exception.Message);
            }

            await LoadThumbnailsAsync(ffmpeg, videoSegment, total).ConfigureAwait(true);

            TrackTimelineView.Refresh();
            UpdateTimelineDuration();
            UpdateCommandStates();
            SetStatus("Timeline ready. Space plays, Ctrl+Shift+D splits, Ctrl+Z undoes.");
        }

        private async Task LoadThumbnailsAsync(string ffmpegPath, TimelineSegment segment, double sourceDuration)
        {
            try
            {
                double width = TrackTimelineView.ActualWidth > 240.0 ? TrackTimelineView.ActualWidth : 900.0;
                int thumbnailCount = (int)Math.Round(width / 72.0);
                if (thumbnailCount < 6)
                {
                    thumbnailCount = 6;
                }

                if (thumbnailCount > 48)
                {
                    thumbnailCount = 48;
                }

                string? sourcePath = segment.SourcePath;
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    return;
                }

                List<BitmapSource> extracted = await ThumbnailExtractor
                    .ExtractAsync(ffmpegPath, sourcePath, sourceDuration, thumbnailCount, 56, CancellationToken.None)
                    .ConfigureAwait(true);

                if (extracted.Count > 0)
                {
                    segment.Thumbnails = extracted.ToArray();
                }
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "TimelineThumbnails");
            }
        }

        private void UpdateTimelineDuration()
        {
            double total = durationSeconds;
            foreach (TimelineTrack track in tracks)
            {
                if (track.ContentEndSeconds > total)
                {
                    total = track.ContentEndSeconds;
                }
            }

            TrackTimelineView.TotalDuration = total > 0.0 ? total : 1.0;
            TrackTimelineView.Refresh();
        }

        private void AfterTimelineStructureChanged()
        {
            UpdateTimelineDuration();
            UpdateCommandStates();
            TrackTimelineView.Refresh();
        }

        // ── Selection ──────────────────────────────────────────────────────

        private List<KeyValuePair<TimelineTrack, TimelineSegment>> SelectedSegments()
        {
            List<KeyValuePair<TimelineTrack, TimelineSegment>> selected = new List<KeyValuePair<TimelineTrack, TimelineSegment>>();
            foreach (TimelineTrack track in tracks)
            {
                foreach (TimelineSegment segment in track.Segments)
                {
                    if (segment.IsSelected)
                    {
                        selected.Add(new KeyValuePair<TimelineTrack, TimelineSegment>(track, segment));
                    }
                }
            }

            return selected;
        }

        private List<TimelineTrack> SelectedTrackRows()
        {
            List<TimelineTrack> selected = new List<TimelineTrack>();
            foreach (TimelineTrack track in tracks)
            {
                if (track.IsSelected)
                {
                    selected.Add(track);
                }
            }

            return selected;
        }

        private void ClearSelection()
        {
            foreach (TimelineTrack track in tracks)
            {
                track.IsSelected = false;
                track.ClearSegmentSelection();
            }
        }

        private void SelectTrackRow(TimelineTrack track, bool additive)
        {
            if (!additive)
            {
                ClearSelection();
            }

            track.IsSelected = true;
            track.SelectAllSegments(true);
            UpdateCommandStates();
            TrackTimelineView.Refresh();
        }

        private void SelectSegment(TimelineTrack track, TimelineSegment segment, bool additive)
        {
            if (!additive)
            {
                ClearSelection();
            }

            if (additive)
            {
                segment.IsSelected = !segment.IsSelected;
            }
            else
            {
                segment.IsSelected = true;
            }

            track.IsSelected = false;
            UpdateCommandStates();
            TrackTimelineView.Refresh();
        }

        private TimelineSegment? FirstSelectedAudioSegment()
        {
            foreach (TimelineTrack track in tracks)
            {
                if (track.Kind != TrackKind.Audio)
                {
                    continue;
                }

                foreach (TimelineSegment segment in track.Segments)
                {
                    if (segment.IsSelected)
                    {
                        return segment;
                    }
                }
            }

            foreach (TimelineTrack track in tracks)
            {
                if (track.Kind == TrackKind.Audio)
                {
                    TimelineSegment? first = track.FirstSegment();
                    if (first != null)
                    {
                        return first;
                    }
                }
            }

            return null;
        }

        private int CountTracks(TrackKind kind)
        {
            int count = 0;
            foreach (TimelineTrack track in tracks)
            {
                if (track.Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }

        // ── Timeline snapshots (undo of every structural edit) ─────────────

        private TimelineSnapshot CaptureTimeline()
        {
            TimelineSnapshot snapshot = new TimelineSnapshot();
            foreach (TimelineTrack track in tracks)
            {
                TrackSnapshot trackSnapshot = new TrackSnapshot();
                trackSnapshot.Track = track;
                trackSnapshot.WasSelected = track.IsSelected;
                foreach (TimelineSegment segment in track.Segments)
                {
                    SegmentSnapshot segmentSnapshot = new SegmentSnapshot();
                    segmentSnapshot.Segment = segment;
                    segmentSnapshot.StartSeconds = segment.StartSeconds;
                    segmentSnapshot.WasSelected = segment.IsSelected;
                    trackSnapshot.Segments.Add(segmentSnapshot);
                }

                snapshot.Tracks.Add(trackSnapshot);
            }

            return snapshot;
        }

        private void RestoreTimeline(TimelineSnapshot snapshot)
        {
            tracks.Clear();
            foreach (TrackSnapshot trackSnapshot in snapshot.Tracks)
            {
                trackSnapshot.Track.Segments.Clear();
                foreach (SegmentSnapshot segmentSnapshot in trackSnapshot.Segments)
                {
                    segmentSnapshot.Segment.StartSeconds = segmentSnapshot.StartSeconds;
                    segmentSnapshot.Segment.IsSelected = segmentSnapshot.WasSelected;
                    trackSnapshot.Track.Segments.Add(segmentSnapshot.Segment);
                }

                trackSnapshot.Track.IsSelected = trackSnapshot.WasSelected;
                tracks.Add(trackSnapshot.Track);
            }

            AfterTimelineStructureChanged();
        }

        // ── Seeking ────────────────────────────────────────────────────────

        private void SeekToSeconds(double seconds)
        {
            double total = durationSeconds > 0.0 ? durationSeconds : EstimatedDuration();
            if (seconds < 0.0)
            {
                seconds = 0.0;
            }

            if (total > 0.0 && seconds > total)
            {
                seconds = total;
            }

            TimeSpan position = TimeSpan.FromSeconds(seconds);
            suppressSliderSeek = true;
            PositionSlider.Value = seconds;
            suppressSliderSeek = false;
            PositionText.Text = SubtitleCue.FormatTimestamp(position);
            SafeSeek(position);
            UpdateOverlay(position);
            UpdatePlayhead(seconds);
        }

        private void SeekBy(double deltaSeconds)
        {
            SeekToSeconds(CurrentPosition.TotalSeconds + deltaSeconds);
        }

        private void StepFrames(int frameCount)
        {
            SeekToSeconds(CurrentPosition.TotalSeconds + (frameCount * frameStepSeconds));
        }

        private void TrackTimelineSeek(object? sender, double seconds)
        {
            SeekToSeconds(seconds);
        }

        // ── Timeline mouse ─────────────────────────────────────────────────

        private void TrackTimelineMouseDown(object? sender, TrackMouseDownEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                BeginRename(e.Track);
                return;
            }

            SelectTrackRow(e.Track, e.ControlPressed);
        }

        private void TrackTimelineSegmentMouseDown(object? sender, SegmentMouseDownEventArgs e)
        {
            SelectSegment(e.Track, e.Segment, e.ControlPressed);
        }

        private void TrackTimelineReorder(object? sender, TrackReorderEventArgs e)
        {
            int from = e.FromIndex;
            if (from < 0 || from >= tracks.Count)
            {
                return;
            }

            int target = e.ToIndex > from ? e.ToIndex - 1 : e.ToIndex;
            if (target < 0)
            {
                target = 0;
            }

            if (target >= tracks.Count)
            {
                target = tracks.Count - 1;
            }

            if (target == from)
            {
                return;
            }

            TimelineSnapshot before = CaptureTimeline();
            TimelineTrack moved = tracks[from];
            tracks.RemoveAt(from);
            tracks.Insert(target, moved);

            UndoStackPush("reorder tracks", delegate
            {
                RestoreTimeline(before);
            });

            AfterTimelineStructureChanged();
            SetStatus("Moved " + moved.Name + " to position " + (target + 1).ToString(CultureInfo.InvariantCulture) + ".");
        }

        private void TrackTimelineSegmentDropped(object? sender, SegmentDroppedEventArgs e)
        {
            TimelineSegment segment = e.Segment;
            TimelineTrack sourceTrack = e.SourceTrack;

            TimelineSnapshot before = CaptureTimeline();

            TimelineTrack targetTrack;
            bool createdTrack = false;
            string createdName = string.Empty;

            if (e.DropKind == TrackDropKind.ExistingTrack && e.TargetTrack != null)
            {
                targetTrack = e.TargetTrack;
                if (!ReferenceEquals(targetTrack, sourceTrack))
                {
                    sourceTrack.Segments.Remove(segment);
                    targetTrack.Segments.Add(segment);
                }
            }
            else
            {
                createdName = NextTrackName(sourceTrack.Kind);
                targetTrack = new TimelineTrack(sourceTrack.Kind, createdName, NextTrackColor(sourceTrack.Kind));
                createdTrack = true;
                sourceTrack.Segments.Remove(segment);

                int insertIndex = e.TargetRowIndex;
                if (insertIndex < 0)
                {
                    insertIndex = 0;
                }

                if (insertIndex > tracks.Count)
                {
                    insertIndex = tracks.Count;
                }

                tracks.Insert(insertIndex, targetTrack);
                targetTrack.Segments.Add(segment);
            }

            segment.StartSeconds = Math.Max(0.0, e.NewStartSeconds);
            targetTrack.PushAside(segment);

            string label = createdTrack ? "move clip to new track " + createdName : "move clip";
            UndoStackPush(label, delegate
            {
                RestoreTimeline(before);
            });

            AfterTimelineStructureChanged();

            string where = createdTrack
                ? "a new track (" + createdName + ")"
                : targetTrack.Name;
            SetStatus("Moved clip to " + where + " at "
                + SubtitleCue.FormatTimestamp(TimeSpan.FromSeconds(segment.StartSeconds))
                + ". Ctrl+Z undoes it.");
        }

        private string NextTrackName(TrackKind kind)
        {
            if (kind == TrackKind.Video)
            {
                return "Video " + (CountTracks(TrackKind.Video) + 1).ToString(CultureInfo.InvariantCulture);
            }

            string candidate = "Audio " + nextAudioTrackNumber.ToString(CultureInfo.InvariantCulture);
            nextAudioTrackNumber++;
            return candidate;
        }

        private Color NextTrackColor(TrackKind kind)
        {
            if (kind == TrackKind.Video)
            {
                return TrackColors.VideoDefault;
            }

            return TrackColors.NextAudioColor(CountTracks(TrackKind.Audio));
        }

        private void TrackTimelineContextMenu(object? sender, TimelineTrack track)
        {
            SelectTrackRow(track, false);

            ContextMenu menu = new ContextMenu();

            MenuItem renameItem = new MenuItem();
            renameItem.Header = "Rename…";
            renameItem.Click += delegate { BeginRename(track); };
            menu.Items.Add(renameItem);

            MenuItem colorItem = new MenuItem();
            colorItem.Header = "Background colour";
            AddColorEntries(colorItem, track);
            menu.Items.Add(colorItem);

            MenuItem splitItem = new MenuItem();
            splitItem.Header = "Split at playhead (Ctrl+Shift+D)";
            splitItem.Click += delegate { SplitSelectedSegments(); };
            menu.Items.Add(splitItem);

            MenuItem deleteItem = new MenuItem();
            deleteItem.Header = "Delete track (Del)";
            deleteItem.Click += delegate { DeleteSelection(); };

            menu.Items.Add(new Separator());
            menu.Items.Add(deleteItem);

            menu.PlacementTarget = TrackTimelineView;
            menu.IsOpen = true;
        }

        private void AddColorEntries(ItemCollection target, TimelineTrack track)
        {
            for (int index = 0; index < TrackColors.Count; index++)
            {
                Color paletteColor = TrackColors.GetColor(index);
                MenuItem entry = new MenuItem();
                entry.Header = TrackColors.GetName(index);

                Rectangle swatch = new Rectangle();
                swatch.Width = 14.0;
                swatch.Height = 14.0;
                swatch.RadiusX = 2.0;
                swatch.RadiusY = 2.0;
                swatch.Fill = new SolidColorBrush(paletteColor);
                entry.Icon = swatch;

                TimelineTrack track2 = track;
                Color chosen = paletteColor;
                entry.Click += delegate { ApplyTrackColor(track2, chosen); };
                target.Add(entry);
            }
        }

        // ── Track commands ─────────────────────────────────────────────────

        private void AddAudioTrackClick(object sender, RoutedEventArgs e)
        {
            TimelineSnapshot before = CaptureTimeline();
            string name = NextTrackName(TrackKind.Audio);

            TimelineTrack track = new TimelineTrack(TrackKind.Audio, name, NextTrackColor(TrackKind.Audio));
            tracks.Add(track);
            SelectTrackRow(track, false);

            UndoStackPush("add " + name, delegate
            {
                RestoreTimeline(before);
            });

            AfterTimelineStructureChanged();
            SetStatus("Added " + name + " — press Record to fill it.");
        }

        private void RenameTrackClick(object sender, RoutedEventArgs e)
        {
            List<TimelineTrack> selection = SelectedTrackRows();
            if (selection.Count == 0)
            {
                SetStatus("Select a track (click its name) to rename it.");
                return;
            }

            BeginRename(selection[0]);
        }

        private void BeginRename(TimelineTrack track)
        {
            TextInputDialog dialog = new TextInputDialog("Rename track", "Track name", track.Name);
            dialog.Owner = this;
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            string updated = dialog.ResponseText;
            if (updated.Length == 0 || string.Equals(updated, track.Name, StringComparison.Ordinal))
            {
                return;
            }

            string previous = track.Name;
            track.Name = updated;

            UndoStackPush("rename track", delegate
            {
                track.Name = previous;
                TrackTimelineView.Refresh();
            });

            SetStatus("Renamed to " + updated + ".");
        }

        private void TrackColorClick(object sender, RoutedEventArgs e)
        {
            List<TimelineTrack> selection = SelectedTrackRows();
            if (selection.Count == 0)
            {
                SetStatus("Select a track (click its name) to recolour it.");
                return;
            }

            ContextMenu menu = new ContextMenu();
            AddColorEntries(menu, selection[0]);
            menu.PlacementTarget = TrackColorButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void ApplyTrackColor(TimelineTrack track, Color color)
        {
            Color previous = track.BackgroundColor;
            if (previous == color)
            {
                return;
            }

            track.BackgroundColor = color;

            UndoStackPush("track colour", delegate
            {
                track.BackgroundColor = previous;
                TrackTimelineView.Refresh();
            });

            TrackTimelineView.Refresh();
            SetStatus("Colour changed for " + track.Name + " (the waveform colour follows automatically).");
        }

        private void SplitTrackClick(object sender, RoutedEventArgs e)
        {
            SplitSelectedSegments();
        }

        private void SplitSelectedSegments()
        {
            List<KeyValuePair<TimelineTrack, TimelineSegment>> selection = SelectedSegments();
            if (selection.Count == 0)
            {
                SetStatus("Select a clip (or clips) to split at the playhead.");
                return;
            }

            double position = CurrentPosition.TotalSeconds;
            TimelineSnapshot before = CaptureTimeline();

            int splitCount = 0;
            foreach (KeyValuePair<TimelineTrack, TimelineSegment> entry in selection)
            {
                TimelineTrack track = entry.Key;
                TimelineSegment segment = entry.Value;
                if (position <= segment.StartSeconds || position >= segment.EndSeconds)
                {
                    continue;
                }

                TimelineSegment? tail = segment.SplitAt(position);
                if (tail == null)
                {
                    continue;
                }

                // The second half keeps its own row: a new track of the same kind right below.
                string name = NextTrackName(track.Kind);
                TimelineTrack tailTrack = new TimelineTrack(track.Kind, name, track.BackgroundColor);
                tailTrack.Segments.Add(tail);
                tail.IsSelected = true;

                int insertIndex = tracks.IndexOf(track);
                if (insertIndex < 0)
                {
                    insertIndex = tracks.Count;
                }

                tracks.Insert(insertIndex + 1, tailTrack);
                segment.IsSelected = false;
                splitCount++;
            }

            if (splitCount == 0)
            {
                SetStatus("The playhead is not inside any selected clip — move it first.");
                return;
            }

            UndoStackPush("split clip", delegate
            {
                RestoreTimeline(before);
            });

            AfterTimelineStructureChanged();
            SetStatus("Split " + splitCount.ToString(CultureInfo.InvariantCulture) + " clip(s) at "
                + SubtitleCue.FormatTimestamp(TimeSpan.FromSeconds(position)) + ".");
        }

        private void DeleteTrackClick(object sender, RoutedEventArgs e)
        {
            DeleteSelection();
        }

        private void DeleteSelection()
        {
            List<TimelineTrack> selectedRows = SelectedTrackRows();
            List<KeyValuePair<TimelineTrack, TimelineSegment>> selectedSegments = SelectedSegments();

            if (selectedRows.Count == 0 && selectedSegments.Count == 0)
            {
                SetStatus("Select a track or a clip first.");
                return;
            }

            TimelineSnapshot before = CaptureTimeline();

            int removedTracks = 0;
            int removedSegments = 0;

            if (selectedRows.Count > 0)
            {
                foreach (TimelineTrack track in selectedRows)
                {
                    tracks.Remove(track);
                    removedTracks++;
                }
            }
            else
            {
                foreach (KeyValuePair<TimelineTrack, TimelineSegment> entry in selectedSegments)
                {
                    entry.Key.Segments.Remove(entry.Value);
                    removedSegments++;
                }

                List<TimelineTrack> emptied = new List<TimelineTrack>();
                foreach (TimelineTrack track in tracks)
                {
                    if (track.Segments.Count == 0)
                    {
                        emptied.Add(track);
                    }
                }

                foreach (TimelineTrack track in emptied)
                {
                    tracks.Remove(track);
                }
            }

            UndoStackPush("delete", delegate
            {
                RestoreTimeline(before);
            });

            AfterTimelineStructureChanged();

            if (removedTracks > 0)
            {
                SetStatus("Deleted " + removedTracks.ToString(CultureInfo.InvariantCulture) + " track(s). Ctrl+Z brings them back.");
                return;
            }

            SetStatus("Deleted " + removedSegments.ToString(CultureInfo.InvariantCulture) + " clip(s). Ctrl+Z brings them back.");
        }

        // ── Microphone recording ───────────────────────────────────────────

        private void RecordClick(object sender, RoutedEventArgs e)
        {
            if (isRecording)
            {
                StopRecording();
                return;
            }

            StartRecording();
        }

        private void StartRecording()
        {
            TimelineSegment? segment = FirstSelectedAudioSegment();
            if (segment == null)
            {
                SetStatus("Add an audio track first — the recording overwrites the selected clip.");
                return;
            }

            if (isPlaying)
            {
                PausePlayback();
            }

            segment.PrepareForWrite();
            recordingUndoSamples = (short[])segment.Samples.Clone();
            recordingUndoPeaks = (float[])segment.Peaks.Clone();
            recordingUndoDuration = segment.DurationSeconds;

            recordingStartSample = segment.TimelineToSampleIndex(CurrentPosition.TotalSeconds);
            if (recordingStartSample < 0)
            {
                recordingStartSample = 0;
            }

            recordingWritten = 0;
            recordingSegment = segment;

            try
            {
                microphoneRecorder.Start();
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "RecordStart");
                recordingSegment = null;
                recordingUndoSamples = null;
                recordingUndoPeaks = null;
                SetStatus("The microphone could not be opened: " + exception.Message);
                return;
            }

            isRecording = true;
            RecordButton.Content = "■ Stop recording";
            VideoElement.IsMuted = true;
            UpdateCommandStates();
            SetStatus("Recording at " + SubtitleCue.FormatTimestamp(CurrentPosition)
                + " — press Record again to stop.");
        }

        private void StopRecording()
        {
            microphoneRecorder.Stop();

            isRecording = false;
            RecordButton.Content = "● Record";
            VideoElement.IsMuted = false;

            TimelineSegment? segment = recordingSegment;
            recordingSegment = null;

            int written = recordingWritten;
            short[]? undoSamples = recordingUndoSamples;
            float[]? undoPeaks = recordingUndoPeaks;
            double undoDuration = recordingUndoDuration;
            recordingUndoSamples = null;
            recordingUndoPeaks = null;
            recordingWritten = 0;

            if (segment != null && written > 0)
            {
                segment.HasRecordedAudio = true;
                segment.ClipFile = null;
            }

            if (segment != null && written > 0 && undoSamples != null && undoPeaks != null)
            {
                TimelineSegment target = segment;
                short[] samplesSnapshot = undoSamples;
                float[] peaksSnapshot = undoPeaks;

                UndoStackPush("record", delegate
                {
                    target.Samples = samplesSnapshot;
                    target.Peaks = peaksSnapshot;
                    target.SharesSamples = false;
                    target.DurationSeconds = undoDuration;
                    AfterTimelineStructureChanged();
                });
            }

            UpdateTimelineDuration();
            UpdateCommandStates();

            if (segment != null)
            {
                double seconds = written / (double)TimelineSegment.SampleRate;
                SetStatus("Recorded " + seconds.ToString("0.0", CultureInfo.InvariantCulture)
                    + " s. Ctrl+Z undoes the whole take (it will be saved as a clip with the project).");
            }
        }

        private void OnMicrophoneSamples(object? sender, MicrophoneSamplesEventArgs e)
        {
            short[] buffer = e.Samples;
            Dispatcher.BeginInvoke(new Action(delegate { AppendRecordingSamples(buffer); }));
        }

        private void AppendRecordingSamples(short[] incoming)
        {
            TimelineSegment? segment = recordingSegment;
            if (segment == null || incoming.Length == 0)
            {
                return;
            }

            int writeIndex = recordingStartSample + recordingWritten;
            segment.EnsureCapacity(writeIndex + incoming.Length);
            Array.Copy(incoming, 0, segment.Samples, writeIndex, incoming.Length);
            recordingWritten += incoming.Length;
            segment.UpdatePeaks(writeIndex, incoming.Length);

            double endSeconds = segment.SampleIndexToTimeline(writeIndex + incoming.Length);
            if (endSeconds > segment.EndSeconds)
            {
                segment.DurationSeconds = endSeconds - segment.StartSeconds;
            }

            TrackTimelineView.Refresh();
        }

        // ── Undo ───────────────────────────────────────────────────────────

        private void UndoClick(object sender, RoutedEventArgs e)
        {
            UndoLastAction();
        }

        private void UndoLastAction()
        {
            string description;
            if (!undoStack.TryUndo(out description))
            {
                SetStatus("Nothing to undo.");
                return;
            }

            TrackTimelineView.Refresh();
            UpdateTimelineDuration();
            UpdateCommandStates();
            SetStatus("Undid: " + description + ".");
        }

        private void UndoStackPush(string description, Action undo)
        {
            undoStack.Push(new DelegateUndoAction(description, undo));
        }

        private void OnUndoStackChanged(object? sender, EventArgs e)
        {
            UndoTrackButton.IsEnabled = undoStack.CanUndo;
        }

        // ── Project files ──────────────────────────────────────────────────

        private void SaveProjectClick(object sender, RoutedEventArgs e)
        {
            SaveProject(false);
        }

        private void SaveProjectAsClick(object sender, RoutedEventArgs e)
        {
            SaveProject(true);
        }

        private void SaveProject(bool forcePrompt)
        {
            string? target = projectPath;
            if (forcePrompt || string.IsNullOrEmpty(target))
            {
                SaveFileDialog dialog = new SaveFileDialog();
                dialog.Filter = ProjectFile.FilterText;
                dialog.DefaultExt = ProjectFile.Extension;
                dialog.AddExtension = true;
                dialog.FileName = string.IsNullOrEmpty(videoPath)
                    ? "project" + ProjectFile.Extension
                    : Path.GetFileNameWithoutExtension(videoPath) + ProjectFile.Extension;

                if (dialog.ShowDialog(this) != true)
                {
                    return;
                }

                target = dialog.FileName;
            }

            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            try
            {
                string folder = Path.GetDirectoryName(target) ?? ".";
                ProjectFileData data = new ProjectFileData();
                data.VideoPath = videoPath ?? string.Empty;
                data.DurationSeconds = durationSeconds;
                data.FrameStepSeconds = frameStepSeconds;

                int trackIndex = 0;
                foreach (TimelineTrack track in tracks)
                {
                    ProjectTrackData trackData = new ProjectTrackData();
                    trackData.Kind = track.Kind.ToString();
                    trackData.Name = track.Name;
                    trackData.BackgroundColor = track.BackgroundColor.ToString(CultureInfo.InvariantCulture);

                    int segmentIndex = 0;
                    foreach (TimelineSegment segment in track.Segments)
                    {
                        ProjectSegmentData segmentData = new ProjectSegmentData();
                        segmentData.StartSeconds = segment.StartSeconds;
                        segmentData.SourceOffsetSeconds = segment.SourceOffsetSeconds;
                        segmentData.DurationSeconds = segment.DurationSeconds;
                        segmentData.SourceDuration = segment.EffectiveSourceDuration;
                        segmentData.SourcePath = segment.SourcePath;
                        segmentData.HasRecordedAudio = segment.HasRecordedAudio;

                        if (segment.Kind == TrackKind.Audio && segment.HasRecordedAudio && segment.Samples.Length > 0)
                        {
                            string clipName = ProjectFile.BuildClipFileName(target, trackIndex, segmentIndex);
                            string clipPath = Path.Combine(folder, clipName);
                            int startSample = segment.TimelineToSampleIndex(segment.StartSeconds);
                            int sampleCount = (int)Math.Round(segment.DurationSeconds * TimelineSegment.SampleRate);

                            WavFile.Write(clipPath, segment.Samples, startSample, sampleCount);
                            segment.ClipFile = clipName;
                            segmentData.ClipFile = clipName;
                        }

                        trackData.Segments.Add(segmentData);
                        segmentIndex++;
                    }

                    data.Tracks.Add(trackData);
                    trackIndex++;
                }

                foreach (SubtitleCue cue in cues)
                {
                    ProjectCueData cueData = new ProjectCueData();
                    cueData.StartSeconds = Math.Round(cue.Start.TotalSeconds, 3);
                    cueData.EndSeconds = Math.Round(cue.End.TotalSeconds, 3);
                    cueData.Text = cue.Text;
                    cueData.Confidence = cue.Confidence;
                    data.Cues.Add(cueData);
                }

                ProjectFile.Save(target, data);
                projectPath = target;
                SetStatus("Project saved to " + Path.GetFileName(target) + ".");
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "SaveProject");
                SetStatus("The project could not be saved: " + exception.Message);
                MessageBox.Show(
                    this,
                    exception.Message + "\n\nDetails were written to:\n" + CrashLog.LogPath,
                    "SubtitleForge — saving failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void OpenProjectClick(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = ProjectFile.FilterText;
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            await LoadProjectAsync(dialog.FileName).ConfigureAwait(true);
        }

        private async Task LoadProjectAsync(string path)
        {
            if (isBusy)
            {
                SetStatus("Wait for the running operation to finish first.");
                return;
            }

            suppressTimelineBuild = true;
            try
            {
                ProjectFileData data = ProjectFile.Load(path);

                string videoRef = ProjectFile.ResolveRelativePath(path, data.VideoPath);
                if (!string.IsNullOrEmpty(videoRef) && File.Exists(videoRef))
                {
                    LoadVideo(videoRef);
                }
                else if (!string.IsNullOrEmpty(videoRef))
                {
                    SetStatus("The project's video is missing: " + videoRef);
                }

                if (data.DurationSeconds > 0.0)
                {
                    durationSeconds = data.DurationSeconds;
                }

                if (data.FrameStepSeconds > 0.0)
                {
                    frameStepSeconds = data.FrameStepSeconds;
                }

                ApplyDurationToUi();

                tracks.Clear();
                undoStack.Clear();
                nextAudioTrackNumber = 1;
                int anonymousAudioIndex = 1;

                foreach (ProjectTrackData trackData in data.Tracks)
                {
                    TrackKind kind = string.Equals(trackData.Kind, "Video", StringComparison.OrdinalIgnoreCase)
                        ? TrackKind.Video
                        : TrackKind.Audio;

                    string name = trackData.Name;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        if (kind == TrackKind.Video)
                        {
                            name = "Video";
                        }
                        else
                        {
                            name = "Audio " + anonymousAudioIndex.ToString(CultureInfo.InvariantCulture);
                            anonymousAudioIndex++;
                        }
                    }

                    TimelineTrack track = new TimelineTrack(kind, name, ParseColor(trackData.BackgroundColor, kind));
                    if (kind == TrackKind.Audio)
                    {
                        nextAudioTrackNumber++;
                    }

                    foreach (ProjectSegmentData segmentData in trackData.Segments)
                    {
                        TimelineSegment segment = new TimelineSegment(kind);
                        segment.StartSeconds = segmentData.StartSeconds;
                        segment.SourceOffsetSeconds = segmentData.SourceOffsetSeconds;
                        segment.DurationSeconds = segmentData.DurationSeconds;
                        segment.SourceDuration = segmentData.SourceDuration;
                        segment.HasRecordedAudio = segmentData.HasRecordedAudio;
                        segment.ClipFile = segmentData.ClipFile;

                        string sourceRef = ProjectFile.ResolveRelativePath(path, segmentData.SourcePath);
                        if (!string.IsNullOrEmpty(sourceRef))
                        {
                            segment.SourcePath = sourceRef;
                        }
                        else if (kind == TrackKind.Video && !string.IsNullOrEmpty(videoRef))
                        {
                            segment.SourcePath = videoRef;
                        }

                        track.Segments.Add(segment);
                    }

                    tracks.Add(track);
                }

                string? ffmpeg = FfmpegLocator.Resolve("ffmpeg", App.Settings.FfmpegDirectory);

                foreach (TimelineTrack track in tracks)
                {
                    if (track.Kind != TrackKind.Audio)
                    {
                        continue;
                    }

                    foreach (TimelineSegment segment in track.Segments)
                    {
                        await LoadSegmentAudioAsync(segment, ffmpeg, path).ConfigureAwait(true);
                    }
                }

                foreach (TimelineTrack track in tracks)
                {
                    if (track.Kind != TrackKind.Video || ffmpeg == null)
                    {
                        continue;
                    }

                    foreach (TimelineSegment segment in track.Segments)
                    {
                        await LoadThumbnailsAsync(ffmpeg, segment, segment.EffectiveSourceDuration).ConfigureAwait(true);
                    }
                }

                cues.Clear();
                foreach (ProjectCueData cueData in data.Cues)
                {
                    cues.Add(new SubtitleCue(
                        TimeSpan.FromSeconds(cueData.StartSeconds),
                        TimeSpan.FromSeconds(cueData.EndSeconds),
                        cueData.Text,
                        cueData.Confidence));
                }

                CuePostProcessor.Renumber(cues);
                UpdateCueCount();
                DrawTimeline();
                timelineInitialized = true;
                projectPath = path;
                AfterTimelineStructureChanged();
                SetStatus("Project loaded: " + Path.GetFileName(path) + " — " + tracks.Count.ToString(CultureInfo.InvariantCulture) + " tracks, "
                    + cues.Count.ToString(CultureInfo.InvariantCulture) + " cues.");
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "LoadProject");
                SetStatus("The project could not be loaded: " + exception.Message);
                MessageBox.Show(
                    this,
                    exception.Message + "\n\nDetails were written to:\n" + CrashLog.LogPath,
                    "SubtitleForge — opening failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                suppressTimelineBuild = false;
            }
        }

        private async Task LoadSegmentAudioAsync(TimelineSegment segment, string? ffmpegPath, string projectFilePath)
        {
            try
            {
                if (!string.IsNullOrEmpty(segment.ClipFile))
                {
                    string clipPath = ProjectFile.ResolveRelativePath(projectFilePath, segment.ClipFile);
                    if (File.Exists(clipPath))
                    {
                        segment.Samples = WavFile.Read(clipPath);
                        segment.RebuildPeaks();
                        segment.HasRecordedAudio = true;
                        return;
                    }
                }

                if (string.IsNullOrEmpty(segment.SourcePath) || !File.Exists(segment.SourcePath) || ffmpegPath == null)
                {
                    return;
                }

                short[] samples = await AudioSampleLoader.LoadAsync(ffmpegPath, segment.SourcePath, CancellationToken.None).ConfigureAwait(true);
                segment.Samples = samples;
                segment.RebuildPeaks();
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "LoadSegmentAudio");
            }
        }

        private static Color ParseColor(string text, TrackKind kind)
        {
            Color fallback = kind == TrackKind.Video ? TrackColors.VideoDefault : TrackColors.AudioDefault;
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            try
            {
                object? parsed = ColorConverter.ConvertFromString(text);
                if (parsed is Color color)
                {
                    return color;
                }
            }
            catch (Exception)
            {
                // Unreadable colour in the project — keep the default.
            }

            return fallback;
        }

        // ── Transcription sources ──────────────────────────────────────────

        /// <summary>
        /// Every audio clip on the timeline, in playback order — subtitles are built from all
        /// of them and merged, instead of only from the video's own audio track.
        /// </summary>
        private List<TimelineSegment> AudioSegmentsForTranscription()
        {
            List<TimelineSegment> ordered = new List<TimelineSegment>();
            foreach (TimelineTrack track in tracks)
            {
                if (track.Kind != TrackKind.Audio)
                {
                    continue;
                }

                foreach (TimelineSegment segment in track.Segments)
                {
                    if (segment.DurationSeconds > 0.05)
                    {
                        ordered.Add(segment);
                    }
                }
            }

            ordered.Sort(delegate (TimelineSegment left, TimelineSegment right)
            {
                return left.StartSeconds.CompareTo(right.StartSeconds);
            });

            return ordered;
        }

        private async Task<List<SubtitleCue>> TranscribeSegmentsAsync(
            List<TimelineSegment> segments,
            string wavFolder,
            CancellationToken cancellationToken)
        {
            List<SubtitleCue> combined = new List<SubtitleCue>();
            string? runtimeDescription = null;
            string detectedLanguage = string.Empty;

            int index = 0;
            foreach (TimelineSegment segment in segments)
            {
                index++;
                cancellationToken.ThrowIfCancellationRequested();

                SetStatus("Transcribing audio " + index.ToString(CultureInfo.InvariantCulture)
                    + " of " + segments.Count.ToString(CultureInfo.InvariantCulture) + "…");

                string wavPath = Path.Combine(
                    wavFolder,
                    "audio-" + Guid.NewGuid().ToString("N") + ".wav");

                bool hasSource = false;

                if (segment.HasRecordedAudio && segment.Samples.Length > 0)
                {
                    int startSample = segment.TimelineToSampleIndex(segment.StartSeconds);
                    int sampleCount = (int)Math.Round(segment.DurationSeconds * TimelineSegment.SampleRate);
                    WavFile.Write(wavPath, segment.Samples, startSample, sampleCount);
                    hasSource = true;
                }
                else if (!string.IsNullOrEmpty(segment.SourcePath) && File.Exists(segment.SourcePath))
                {
                    string? ffmpeg = FfmpegLocator.Resolve("ffmpeg", App.Settings.FfmpegDirectory);
                    if (ffmpeg != null)
                    {
                        await AudioTrackExtractor
                            .ExtractSegmentAsync(
                                ffmpeg,
                                segment.SourcePath,
                                wavPath,
                                segment.SourceOffsetSeconds,
                                segment.DurationSeconds,
                                null,
                                cancellationToken)
                            .ConfigureAwait(true);
                        hasSource = true;
                    }
                }

                if (!hasSource)
                {
                    continue;
                }

                try
                {
                    TranscriptionPiece piece = await TranscribeOneFileAsync(wavPath, cancellationToken).ConfigureAwait(true);
                    runtimeDescription = piece.RuntimeDescription;
                    if (!string.IsNullOrEmpty(piece.DetectedLanguage))
                    {
                        detectedLanguage = piece.DetectedLanguage;
                    }

                    double offsetSeconds = segment.StartSeconds;
                    foreach (SubtitleCue cue in piece.Cues)
                    {
                        combined.Add(new SubtitleCue(
                            cue.Start + TimeSpan.FromSeconds(offsetSeconds),
                            cue.End + TimeSpan.FromSeconds(offsetSeconds),
                            cue.Text,
                            cue.Confidence));
                    }
                }
                finally
                {
                    TryDeleteFile(wavPath);
                }
            }

            combined.Sort(delegate (SubtitleCue left, SubtitleCue right)
            {
                return left.Start.CompareTo(right.Start);
            });

            lastRuntimeDescription = runtimeDescription ?? "unknown runtime";
            lastDetectedLanguage = detectedLanguage;
            return combined;
        }

        private async Task<TranscriptionPiece> TranscribeOneFileAsync(string wavPath, CancellationToken cancellationToken)
        {
            WhisperModelInfo? model = ModelComboBox.SelectedItem as WhisperModelInfo;
            if (model == null)
            {
                throw new InvalidOperationException("Choose a Whisper model first.");
            }

            IProgress<string> modelStatus = new Progress<string>(SetStatus);
            IProgress<double> downloadProgress = new Progress<double>(delegate (double fraction)
            {
                SetProgress(fraction * 0.10);
            });

            string modelPath = await WhisperModelStore
                .EnsureModelAsync(model.FileName, modelStatus, downloadProgress, cancellationToken)
                .ConfigureAwait(true);

            TranscriptionRequest request = new TranscriptionRequest();
            request.WavPath = wavPath;
            request.ModelPath = modelPath;
            request.Language = App.Settings.Language;
            request.TranslateToEnglish = App.Settings.TranslateToEnglish;
            request.UseGpu = App.Settings.UseGpu;
            request.SkipNonSpeech = App.Settings.SkipNonSpeech;
            request.Threads = App.Settings.Threads;

            IProgress<double> progress = new Progress<double>(delegate (double fraction)
            {
                SetProgress(0.25 + (fraction * 0.70));
            });

            TranscriptionResult result = await transcriber
                .TranscribeAsync(request, progress, new Progress<string>(SetStatus), cancellationToken)
                .ConfigureAwait(true);

            TranscriptionPiece piece = new TranscriptionPiece();
            piece.Cues = result.Cues;
            piece.RuntimeDescription = result.RuntimeDescription;
            piece.DetectedLanguage = result.DetectedLanguage;
            return piece;
        }

        // ── Keyboard shortcuts ─────────────────────────────────────────────

        private bool IsTextInputFocused()
        {
            DependencyObject? focused = Keyboard.FocusedElement as DependencyObject;
            if (focused == null)
            {
                return false;
            }

            if (focused is TextBox)
            {
                return true;
            }

            if (focused is PasswordBox)
            {
                return true;
            }

            return false;
        }

        private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (IsTextInputFocused())
            {
                if (control && !shift && e.Key == Key.S)
                {
                    SaveProject(false);
                    e.Handled = true;
                }

                return;
            }

            if (control && shift && e.Key == Key.D)
            {
                SplitSelectedSegments();
                e.Handled = true;
                return;
            }

            if (control && !shift && e.Key == Key.S)
            {
                SaveProject(false);
                e.Handled = true;
                return;
            }

            if (control && !shift && e.Key == Key.Z)
            {
                UndoLastAction();
                e.Handled = true;
                return;
            }

            if (control)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Space:
                    if (!shift)
                    {
                        TogglePlayPause();
                        e.Handled = true;
                    }

                    break;
                case Key.Delete:
                    DeleteSelection();
                    e.Handled = true;
                    break;
                case Key.OemComma:
                    StepFrames(-1);
                    e.Handled = true;
                    break;
                case Key.OemPeriod:
                    StepFrames(1);
                    e.Handled = true;
                    break;
                case Key.Left:
                    SeekBy(shift ? -10.0 : -1.0);
                    e.Handled = true;
                    break;
                case Key.Right:
                    SeekBy(shift ? 10.0 : 1.0);
                    e.Handled = true;
                    break;
            }
        }

        // ── Nested state types ─────────────────────────────────────────────

        private class SegmentSnapshot
        {
            public TimelineSegment Segment = null!;

            public double StartSeconds;

            public bool WasSelected;
        }

        private class TrackSnapshot
        {
            public TimelineTrack Track = null!;

            public bool WasSelected;

            public List<SegmentSnapshot> Segments = new List<SegmentSnapshot>();
        }

        private class TimelineSnapshot
        {
            public List<TrackSnapshot> Tracks = new List<TrackSnapshot>();
        }

        private class TranscriptionPiece
        {
            public List<SubtitleCue> Cues = new List<SubtitleCue>();

            public string RuntimeDescription = string.Empty;

            public string DetectedLanguage = string.Empty;
        }

        // ── GPU / CUDA availability ────────────────────────────────────────

        private void GpuCheckedChanged(object sender, RoutedEventArgs e)
        {
            if (suppressCudaCheck || !(sender is CheckBox))
            {
                return;
            }

            if (GpuCheckBox.IsChecked != true)
            {
                return;
            }

            if (!CudaDetector.IsDriverPresent() && !CudaDetector.IsToolkitInstalled(out string driverDetails))
            {
                string message = "GPU transcription needs an NVIDIA driver and the CUDA 12 toolkit.\n\n"
                    + driverDetails
                    + "\n\nOpen the CUDA download page now?";

                MessageBoxResult answer = MessageBox.Show(
                    this,
                    message,
                    "SubtitleForge — CUDA not found",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (answer == MessageBoxResult.Yes)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = CudaDetector.DownloadUrl,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception exception)
                    {
                        CrashLog.Write(exception, "CudaDownloadPage");
                    }
                }

                suppressCudaCheck = true;
                GpuCheckBox.IsChecked = false;
                suppressCudaCheck = false;
                App.Settings.UseGpu = false;
                SetStatus("GPU transcription stays off until the CUDA 12 toolkit is installed.");
                return;
            }

            if (!CudaDetector.IsToolkitInstalled(out string toolkitDetails))
            {
                string warning = "The NVIDIA driver is present but no CUDA 12 toolkit was found.\n\n"
                    + toolkitDetails
                    + "\n\nTranscription will fall back to the CPU when CUDA cannot be initialised.\n"
                    + "Open the CUDA download page?";

                MessageBoxResult answer = MessageBox.Show(
                    this,
                    warning,
                    "SubtitleForge — CUDA toolkit missing",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (answer == MessageBoxResult.Yes)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = CudaDetector.DownloadUrl,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception exception)
                    {
                        CrashLog.Write(exception, "CudaDownloadPage");
                    }
                }
            }

            SetStatus("GPU transcription enabled — it falls back to the CPU automatically if CUDA cannot start.");
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
            ModelsButton.IsEnabled = !isBusy;
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

            bool hasSelection = SelectedSegments().Count > 0;
            bool hasRowSelection = SelectedTrackRows().Count > 0;
            AddAudioTrackButton.IsEnabled = !isBusy && hasVideo;
            RecordButton.IsEnabled = !isBusy && hasVideo;
            SplitTrackButton.IsEnabled = !isBusy && hasSelection;
            DeleteTrackButton.IsEnabled = !isBusy && (hasSelection || hasRowSelection);
            RenameTrackButton.IsEnabled = !isBusy && hasRowSelection;
            TrackColorButton.IsEnabled = !isBusy && hasRowSelection;
            UndoTrackButton.IsEnabled = !isBusy && undoStack.CanUndo;
            SaveProjectButton.IsEnabled = !isBusy;
            SaveProjectAsButton.IsEnabled = !isBusy;
            OpenProjectButton.IsEnabled = !isBusy;
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

            if (isRecording)
            {
                StopRecording();
            }

            microphoneRecorder.Dispose();

            ApplyUiToSettings();
            App.Settings.Save();
            playbackTimer.Stop();
        }
    }
}
