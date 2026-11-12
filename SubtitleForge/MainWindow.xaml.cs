using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
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

        // Timeline playback: the preview follows the tracks instead of one fixed file.
        private readonly Dictionary<TimelineSegment, AudioPlaybackSlot> audioPlayers = new Dictionary<TimelineSegment, AudioPlaybackSlot>();
        private readonly Dictionary<string, string> sourceAudioFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// One audio lane's player plus the state needed to leave it alone while it is playing.
        /// </summary>
        private sealed class AudioPlaybackSlot
        {
            public MediaPlayer Player { get; set; } = null!;

            public bool PositionApplied { get; set; }

            public bool Playing { get; set; }
        }
        private readonly Dictionary<TimelineSegment, string> recordedClipFiles = new Dictionary<TimelineSegment, string>();
        private readonly Stopwatch playbackClock = new Stopwatch();
        private double playbackAnchorSeconds;
        private TimelineSegment? currentVideoSegment;
        private TimelineSegment? fallbackVideoSegment;
        private VideoMetrics videoMetrics;
        private bool projectDirty;

        // Proxy bookkeeping is per source file, so moving clips around, splitting them or
        // importing another file never re-encodes anything that is already cached.
        private readonly Dictionary<string, string> sourceProxies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> sourceReadyFractions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<KeyValuePair<string, double>> pendingProxyJobs = new Queue<KeyValuePair<string, double>>();
        private CancellationTokenSource? proxyCancellation;
        private bool proxyBuildRunning;
        private bool videoPlayRequested;
        private string? currentVideoPath;
        private double? pendingVideoSeekSeconds;
        private bool pendingVideoPlay;
        private bool videoElementReady;
        private bool isBusy;
        private bool isUpdatingCueEditor;

        private bool startMixed;
        private bool endMixed;
        private bool cueTextMixed;
        private bool suppressSliderSeek;

        private readonly ObservableCollection<TimelineTrack> tracks = new ObservableCollection<TimelineTrack>();
        private readonly Stopwatch overlayClock = Stopwatch.StartNew();
        private readonly DispatcherTimer overlayTimer;
        private double idleSeconds;
        private bool suppressCueAppearanceEvents;
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

            // The ruler paints proxy progress; ask us per clip source.
            TrackTimelineView.SourceReadyFractionProvider = SourceReadyFraction;

            playbackTimer = new DispatcherTimer(DispatcherPriority.Render);
            playbackTimer.Interval = TimeSpan.FromMilliseconds(120);
            playbackTimer.Tick += OnPlaybackTimerTick;

            // The idle animation needs its own clock so it keeps moving while playback is paused.
            overlayTimer = new DispatcherTimer(DispatcherPriority.Render);
            overlayTimer.Interval = TimeSpan.FromMilliseconds(16);
            overlayTimer.Tick += OnOverlayTimerTick;
            overlayTimer.Start();

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

        /// <summary>Unity-style placeholder for a field whose value differs across the selection.</summary>
        private const string MixedValueText = "\u2014";

        private List<SubtitleCue> SelectedCueList()
        {
            List<SubtitleCue> selection = new List<SubtitleCue>();
            if (CueList != null)
            {
                foreach (object item in CueList.SelectedItems)
                {
                    SubtitleCue? cue = item as SubtitleCue;
                    if (cue != null && !selection.Contains(cue))
                    {
                        selection.Add(cue);
                    }
                }
            }

            if (selection.Count == 0 && SelectedCue != null)
            {
                selection.Add(SelectedCue);
            }

            return selection;
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
            RefreshCueAppearanceCombos();
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

            // A brand-new video starts a brand-new project: Ctrl+S must ask for a file again.
            projectPath = null;
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

            if (proxyCancellation != null)
            {
                proxyCancellation.Cancel();
                proxyCancellation = null;
            }

            pendingProxyJobs.Clear();
            sourceProxies.Clear();
            sourceReadyFractions.Clear();
            sourceAudioFiles.Clear();
            videoMetrics = new VideoMetrics();
            projectDirty = false;
            UpdateTitleState();

            StopTimelinePlayback();
            DisposeAudioPlayers();
            recordedClipFiles.Clear();
            currentVideoSegment = null;
            currentVideoPath = path;

            TimelineSegment standIn = new TimelineSegment(TrackKind.Video);
            standIn.StartSeconds = 0.0;
            standIn.SourceOffsetSeconds = 0.0;
            standIn.DurationSeconds = 0.0;
            standIn.SourceDuration = 0.0;
            standIn.SourcePath = path;
            fallbackVideoSegment = standIn;
            pendingVideoSeekSeconds = null;
            pendingVideoPlay = false;
            videoElementReady = false;

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

            projectDirty = false;
            UpdateTitleState();

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

                videoElementReady = true;

                double openedSeconds = pendingVideoSeekSeconds.HasValue ? pendingVideoSeekSeconds.Value : 0.0;
                VideoElement.Position = TimeSpan.FromSeconds(Math.Max(0.0, openedSeconds));

                if (pendingVideoPlay)
                {
                    VideoElement.Play();
                    videoPlayRequested = true;
                }
                else
                {
                    VideoElement.Pause();
                    videoPlayRequested = false;
                }

                pendingVideoSeekSeconds = null;
                pendingVideoPlay = false;

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
            if (!isPlaying)
            {
                return;
            }

            double timelineSeconds = playbackAnchorSeconds + playbackClock.Elapsed.TotalSeconds;
            TimelineSegment? next = ActiveVideoSegment(timelineSeconds);
            if (next != null && !ReferenceEquals(next, currentVideoSegment))
            {
                CueVideoSegment(next, timelineSeconds, true);
                return;
            }

            if (next == null && ActiveAudioSegments(timelineSeconds).Count > 0)
            {
                // Nothing left to show but audio keeps going — stay black and keep playing.
                CueVideoSegment(null, timelineSeconds, false);
                return;
            }

            if (durationSeconds <= 0.0 || timelineSeconds >= durationSeconds - 0.08)
            {
                StopTimelinePlayback();
                suppressSliderSeek = true;
                PositionSlider.Value = durationSeconds;
                suppressSliderSeek = false;
                PositionText.Text = SubtitleCue.FormatTimestamp(TimeSpan.FromSeconds(durationSeconds));
                TrackTimelineView.PlayheadSeconds = durationSeconds;
            }
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

            StartTimelinePlayback();
        }

        private void PausePlayback()
        {
            StopTimelinePlayback();
        }

        private void StopClick(object sender, RoutedEventArgs e)
        {
            StopTimelinePlayback();
            playbackAnchorSeconds = 0.0;
            playbackClock.Reset();
            SeekToSeconds(0.0);
        }

        private void OnPlaybackTimerTick(object? sender, EventArgs e)
        {
            if (!isPlaying)
            {
                playbackTimer.Stop();
                return;
            }

            double timelineSeconds = playbackAnchorSeconds + playbackClock.Elapsed.TotalSeconds;

            if (durationSeconds > 0.0 && timelineSeconds >= durationSeconds)
            {
                timelineSeconds = durationSeconds;
                suppressSliderSeek = true;
                PositionSlider.Value = timelineSeconds;
                suppressSliderSeek = false;
                PositionText.Text = SubtitleCue.FormatTimestamp(TimeSpan.FromSeconds(timelineSeconds));
                UpdateOverlay(TimeSpan.FromSeconds(timelineSeconds));
                UpdatePlayhead(timelineSeconds);
                StopTimelinePlayback();
                return;
            }

            FollowTimeline(timelineSeconds, true);

            TimeSpan position = TimeSpan.FromSeconds(timelineSeconds);
            suppressSliderSeek = true;
            PositionSlider.Value = timelineSeconds;
            suppressSliderSeek = false;
            PositionText.Text = SubtitleCue.FormatTimestamp(position);
            UpdateOverlay(position);
            UpdatePlayhead(timelineSeconds);
        }

        private void PositionSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (suppressSliderSeek)
            {
                return;
            }

            if (isPlaying)
            {
                PausePlayback();
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
                // The preview shows whichever video clip sits under the playhead. MediaElement
                // repaints the paused frame by itself because ScrubbingEnabled is on — no
                // play/pause nudge, which used to leave the element in a state where the next
                // Play() rendered a single frame and stopped.
                double seconds = position.TotalSeconds;
                CueVideoSegment(ActiveVideoSegment(seconds), seconds, isPlaying);
                CueAudioSegments(seconds, isPlaying, true);
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
                if (isPlaying)
                {
                    return TimeSpan.FromSeconds(playbackAnchorSeconds + playbackClock.Elapsed.TotalSeconds);
                }

                return TimeSpan.FromSeconds(TrackTimelineView.PlayheadSeconds);
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

            SubtitleOverlayView.PlayheadSeconds = position.TotalSeconds;
            SubtitleOverlayView.IdleSeconds = idleSeconds;

            if (isPlaying && active != null)
            {
                SelectCueFollowingPlayback(active);
            }

            if (active == null)
            {
                if (SubtitleOverlayView.HasContent || SubtitleOverlayView.CueText.Length > 0)
                {
                    SubtitleOverlayView.CueText = string.Empty;
                    SubtitleOverlayView.InvalidateLayout();
                }

                return;
            }

            ApplyCueAppearance(active);
            SubtitleOverlayView.Refresh();
        }

        /// <summary>
        /// Keeps the cue list on the cue that is being spoken, so the editor always shows the line
        /// that is on screen. Skipped while the user is multi-selecting or typing in the editor.
        /// </summary>
        private void SelectCueFollowingPlayback(SubtitleCue active)
        {
            if (CueList.SelectedItems.Count > 1)
            {
                return;
            }

            if (ReferenceEquals(CueList.SelectedItem, active))
            {
                return;
            }

            if (StartEditor.IsKeyboardFocusWithin
                || EndEditor.IsKeyboardFocusWithin
                || CueTextEditor.IsKeyboardFocusWithin)
            {
                return;
            }

            CueList.SelectedItem = active;
            CueList.ScrollIntoView(active);
        }

        /// <summary>Points the overlay at the cue's style / idle / pop-in profiles and placement.</summary>
        private void ApplyCueAppearance(SubtitleCue cue)
        {
            ProfileStore store = App.Profiles;
            StyleProfile style = store.ResolveStyle(cue.StyleProfileId ?? App.Settings.DefaultStyleProfileId);
            IdleAnimationProfile idle = store.ResolveIdle(cue.IdleProfileId ?? App.Settings.DefaultIdleProfileId);
            PopInProfile popIn = store.ResolvePopIn(cue.PopInProfileId ?? App.Settings.DefaultPopInProfileId);

            bool profileChanged = !ReferenceEquals(SubtitleOverlayView.TextStyle, style)
                || !ReferenceEquals(SubtitleOverlayView.Idle, idle)
                || !ReferenceEquals(SubtitleOverlayView.PopIn, popIn)
                || Math.Abs(SubtitleOverlayView.CueStartSeconds - cue.Start.TotalSeconds) > 0.0005
                || Math.Abs(SubtitleOverlayView.CueEndSeconds - cue.End.TotalSeconds) > 0.0005;

            SubtitleOverlayView.TextStyle = style;
            SubtitleOverlayView.Idle = idle;
            SubtitleOverlayView.PopIn = popIn;
            SubtitleOverlayView.CueStartSeconds = cue.Start.TotalSeconds;
            SubtitleOverlayView.CueEndSeconds = cue.End.TotalSeconds;
            SubtitleOverlayView.PositionX = cue.PositionX ?? App.Settings.OverlayPositionX;
            SubtitleOverlayView.PositionY = cue.PositionY ?? App.Settings.OverlayPositionY;
            SubtitleOverlayView.Scale = cue.Scale ?? App.Settings.OverlayScale;

            string text = cue.Text ?? string.Empty;
            bool textChanged = !string.Equals(SubtitleOverlayView.CueText, text, StringComparison.Ordinal);
            if (textChanged)
            {
                SubtitleOverlayView.CueText = text;
            }

            if (profileChanged || textChanged)
            {
                SubtitleOverlayView.InvalidateLayout();
            }
        }

        // ── Cue appearance overrides ───────────────────────────────────────

        private sealed class ProfileChoice
        {
            public const string MixedId = "\u0001mixed";

            public static readonly ProfileChoice Mixed = new ProfileChoice(MixedId, MixedValueText);

            public ProfileChoice(string? id, string name)
            {
                Id = id;
                Name = string.IsNullOrEmpty(name) ? "(unnamed)" : name;
            }

            public string? Id { get; private set; }

            public string Name { get; private set; }

            /// <summary>True when the field shows the "several different values" placeholder.</summary>
            public bool IsMixed
            {
                get { return string.Equals(Id, MixedId, StringComparison.Ordinal); }
            }

            public override string ToString()
            {
                return Name;
            }
        }

        private void RefreshCueAppearanceCombos()
        {
            suppressCueAppearanceEvents = true;
            try
            {
                CueStyleCombo.Items.Clear();
                CueStyleCombo.Items.Add(new ProfileChoice(null, "(default style)"));
                foreach (StyleProfile style in App.Profiles.Styles)
                {
                    CueStyleCombo.Items.Add(new ProfileChoice(style.Id, style.Name));
                }

                CueStyleCombo.Items.Add(ProfileChoice.Mixed);

                CueIdleCombo.Items.Clear();
                CueIdleCombo.Items.Add(new ProfileChoice(null, "(default idle)"));
                foreach (IdleAnimationProfile idle in App.Profiles.Idle)
                {
                    CueIdleCombo.Items.Add(new ProfileChoice(idle.Id, idle.Name));
                }

                CueIdleCombo.Items.Add(ProfileChoice.Mixed);

                CuePopInCombo.Items.Clear();
                CuePopInCombo.Items.Add(new ProfileChoice(null, "(default pop-in)"));
                foreach (PopInProfile popIn in App.Profiles.PopIns)
                {
                    CuePopInCombo.Items.Add(new ProfileChoice(popIn.Id, popIn.Name));
                }

                CuePopInCombo.Items.Add(ProfileChoice.Mixed);
            }
            finally
            {
                suppressCueAppearanceEvents = false;
            }

            UpdateCueAppearanceFromSelection();
        }

        private void UpdateCueAppearanceFromSelection()
        {
            List<SubtitleCue> selection = SelectedCueList();
            suppressCueAppearanceEvents = true;
            try
            {
                bool enabled = selection.Count > 0;
                CueStyleCombo.IsEnabled = enabled;
                CueIdleCombo.IsEnabled = enabled;
                CuePopInCombo.IsEnabled = enabled;
                CuePositionXBox.IsEnabled = enabled;
                CuePositionYBox.IsEnabled = enabled;
                CueScaleBox.IsEnabled = enabled;
                CuePlacementResetButton.IsEnabled = enabled;

                if (!enabled)
                {
                    SelectChoice(CueStyleCombo, null);
                    SelectChoice(CueIdleCombo, null);
                    SelectChoice(CuePopInCombo, null);
                    CuePositionXBox.ClearSilently();
                    CuePositionYBox.ClearSilently();
                    CueScaleBox.ClearSilently();
                    return;
                }

                SelectSharedChoice(CueStyleCombo, selection, cue => cue.StyleProfileId);
                SelectSharedChoice(CueIdleCombo, selection, cue => cue.IdleProfileId);
                SelectSharedChoice(CuePopInCombo, selection, cue => cue.PopInProfileId);

                ApplySharedNumber(CuePositionXBox, selection, cue => cue.PositionX);
                ApplySharedNumber(CuePositionYBox, selection, cue => cue.PositionY);
                ApplySharedNumber(CueScaleBox, selection, cue => cue.Scale);
            }
            finally
            {
                suppressCueAppearanceEvents = false;
            }
        }

        /// <summary>
        /// Selects the shared value of a field, or the placeholder when the selected cues disagree.
        /// </summary>
        private static void SelectSharedChoice(
            ComboBox combo,
            List<SubtitleCue> selection,
            Func<SubtitleCue, string?> selector)
        {
            string? first = selector(selection[0]);
            for (int index = 1; index < selection.Count; index++)
            {
                if (!string.Equals(first, selector(selection[index]), StringComparison.Ordinal))
                {
                    SelectChoice(combo, ProfileChoice.MixedId);
                    return;
                }
            }

            SelectChoice(combo, first);
        }

        private static void ApplySharedNumber(
            NumericDragBox box,
            List<SubtitleCue> selection,
            Func<SubtitleCue, double?> selector)
        {
            double? first = selector(selection[0]);
            for (int index = 1; index < selection.Count; index++)
            {
                if (!SameOptionalNumber(first, selector(selection[index])))
                {
                    box.SetMixed();
                    return;
                }
            }

            if (first.HasValue)
            {
                box.SetValueSilently(first.Value);
            }
            else
            {
                box.ClearSilently();
            }
        }

        private static bool SameOptionalNumber(double? left, double? right)
        {
            if (!left.HasValue && !right.HasValue)
            {
                return true;
            }

            if (!left.HasValue || !right.HasValue)
            {
                return false;
            }

            return Math.Abs(left.Value - right.Value) < 0.0005;
        }

        private static void SelectChoice(ComboBox combo, string? id)
        {
            for (int index = 0; index < combo.Items.Count; index++)
            {
                ProfileChoice? choice = combo.Items[index] as ProfileChoice;
                if (choice != null && string.Equals(choice.Id, id, StringComparison.Ordinal))
                {
                    combo.SelectedIndex = index;
                    return;
                }
            }

            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
        }

        private static string? SelectedChoiceId(ComboBox combo)
        {
            ProfileChoice? choice = combo.SelectedItem as ProfileChoice;
            return choice == null ? null : choice.Id;
        }

        private void CueStyleSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressCueAppearanceEvents)
            {
                return;
            }

            List<SubtitleCue> selection = SelectedCueList();
            if (selection.Count == 0)
            {
                return;
            }

            ProfileChoice? choice = CueStyleCombo.SelectedItem as ProfileChoice;
            if (choice == null || choice.IsMixed)
            {
                // Re-selecting the placeholder means "leave every cue alone".
                return;
            }

            List<SubtitleCue> before = CaptureCueSnapshot();
            foreach (SubtitleCue cue in selection)
            {
                cue.StyleProfileId = choice.Id;
            }

            PushCueUndoIfChanged("cue style", before);
            MarkProjectDirty();
            UpdateOverlay(CurrentPosition);
        }

        private void CueIdleSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressCueAppearanceEvents)
            {
                return;
            }

            List<SubtitleCue> selection = SelectedCueList();
            if (selection.Count == 0)
            {
                return;
            }

            ProfileChoice? choice = CueIdleCombo.SelectedItem as ProfileChoice;
            if (choice == null || choice.IsMixed)
            {
                // Re-selecting the placeholder means "leave every cue alone".
                return;
            }

            List<SubtitleCue> before = CaptureCueSnapshot();
            foreach (SubtitleCue cue in selection)
            {
                cue.IdleProfileId = choice.Id;
            }

            PushCueUndoIfChanged("cue idle animation", before);
            MarkProjectDirty();
            UpdateOverlay(CurrentPosition);
        }

        private void CuePopInSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressCueAppearanceEvents)
            {
                return;
            }

            List<SubtitleCue> selection = SelectedCueList();
            if (selection.Count == 0)
            {
                return;
            }

            ProfileChoice? choice = CuePopInCombo.SelectedItem as ProfileChoice;
            if (choice == null || choice.IsMixed)
            {
                // Re-selecting the placeholder means "leave every cue alone".
                return;
            }

            List<SubtitleCue> before = CaptureCueSnapshot();
            foreach (SubtitleCue cue in selection)
            {
                cue.PopInProfileId = choice.Id;
            }

            PushCueUndoIfChanged("cue pop-in animation", before);
            MarkProjectDirty();
            UpdateOverlay(CurrentPosition);
        }

        private void CuePlacementChanged(object? sender, EventArgs e)
        {
            if (suppressCueAppearanceEvents)
            {
                return;
            }

            List<SubtitleCue> selection = SelectedCueList();
            if (selection.Count == 0)
            {
                return;
            }

            // Scrubbing a field fires on every pixel, so the undo entry is deferred to the moment
            // the field loses focus; otherwise one drag would fill the whole history.
            if (pendingPlacementSnapshot == null)
            {
                pendingPlacementSnapshot = CaptureCueSnapshot();
            }

            foreach (SubtitleCue cue in selection)
            {
                // A field still showing the mixed placeholder must not overwrite the cues that
                // already had their own value.
                if (!CuePositionXBox.IsMixed)
                {
                    cue.PositionX = CuePositionXBox.IsEmpty ? null : (double?)CuePositionXBox.Value;
                }

                if (!CuePositionYBox.IsMixed)
                {
                    cue.PositionY = CuePositionYBox.IsEmpty ? null : (double?)CuePositionYBox.Value;
                }

                if (!CueScaleBox.IsMixed)
                {
                    cue.Scale = CueScaleBox.IsEmpty ? null : (double?)CueScaleBox.Value;
                }
            }
            MarkProjectDirty();
            UpdateOverlay(CurrentPosition);
        }

        private void CuePlacementCommit(object sender, RoutedEventArgs e)
        {
            List<SubtitleCue>? before = pendingPlacementSnapshot;
            pendingPlacementSnapshot = null;
            if (before == null)
            {
                return;
            }

            PushCueUndoIfChanged("cue placement", before);
        }

        private void CuePlacementResetClick(object sender, RoutedEventArgs e)
        {
            List<SubtitleCue> selection = SelectedCueList();
            if (selection.Count == 0)
            {
                return;
            }

            List<SubtitleCue> before = CaptureCueSnapshot();
            foreach (SubtitleCue cue in selection)
            {
                cue.PositionX = null;
                cue.PositionY = null;
                cue.Scale = null;
            }

            PushCueUndoIfChanged("reset cue placement", before);
            UpdateCueAppearanceFromSelection();
            MarkProjectDirty();
            UpdateOverlay(CurrentPosition);
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
            Brush idleCueBrush = (Brush)FindResource("CueMarkIdleBrush");

            double markerHeight = Math.Max(4.0, Math.Min(8.0, height - 8.0));

            Line baseline = new Line();
            baseline.X1 = 0.0;
            baseline.X2 = width;
            baseline.Y1 = height / 2.0;
            baseline.Y2 = height / 2.0;
            baseline.Stroke = subtle;
            baseline.StrokeThickness = 1.0;
            baseline.Opacity = 0.35;
            TimelineCanvas.Children.Add(baseline);

            List<SubtitleCue> selectedCues = SelectedCueList();
            HashSet<SubtitleCue> selectedLookup = new HashSet<SubtitleCue>(selectedCues);
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
                block.Height = markerHeight;
                // Fully opaque by request: the bars have to read clearly against the slider.
                block.Fill = selectedLookup.Contains(cue) ? accent : idleCueBrush;
                block.Opacity = 1.0;
                block.RadiusX = 2.0;
                block.RadiusY = 2.0;
                Canvas.SetLeft(block, left);
                Canvas.SetTop(block, (height - markerHeight) / 2.0);
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
            MarkProjectDirty();
        }

        private void MarkProjectDirty()
        {
            if (projectDirty)
            {
                return;
            }

            projectDirty = true;
            UpdateTitleState();
        }

        private void UpdateTitleState()
        {
            string name = string.IsNullOrEmpty(videoPath) ? "no media" : Path.GetFileName(videoPath);
            string title = "SubtitleForge — " + name + " — " + App.VersionDescription;
            Title = projectDirty ? title + " *" : title;
        }

        private void CueListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCueEditorFields();
            UpdateCueAppearanceFromSelection();
            UpdateCommandStates();
            DrawTimeline();
        }

        private void RefreshCueEditor()
        {
            UpdateCueEditorFields();
        }

        /// <summary>
        /// Fills the text/timing editors from the selection, showing the mixed placeholder wherever
        /// the selected cues disagree — the same behaviour Unity uses for multi-object editing.
        /// </summary>
        private void UpdateCueEditorFields()
        {
            List<SubtitleCue> selection = SelectedCueList();
            isUpdatingCueEditor = true;
            try
            {
                string startText = string.Empty;
                string endText = string.Empty;
                string cueText = string.Empty;

                if (selection.Count > 0)
                {
                    startText = SharedText(selection, cue => SubtitleCue.FormatTimestamp(cue.Start));
                    endText = SharedText(selection, cue => SubtitleCue.FormatTimestamp(cue.End));
                    cueText = SharedText(selection, cue => cue.Text ?? string.Empty);
                }

                startMixed = string.Equals(startText, MixedValueText, StringComparison.Ordinal);
                endMixed = string.Equals(endText, MixedValueText, StringComparison.Ordinal);
                cueTextMixed = string.Equals(cueText, MixedValueText, StringComparison.Ordinal);

                if (!string.Equals(StartEditor.Text, startText, StringComparison.Ordinal))
                {
                    StartEditor.Text = startText;
                }

                if (!string.Equals(EndEditor.Text, endText, StringComparison.Ordinal))
                {
                    EndEditor.Text = endText;
                }

                if (!string.Equals(CueTextEditor.Text, cueText, StringComparison.Ordinal))
                {
                    CueTextEditor.Text = cueText;
                }

                ApplyMixedLook(StartEditor, startMixed);
                ApplyMixedLook(EndEditor, endMixed);
                ApplyMixedLook(CueTextEditor, cueTextMixed);
            }
            finally
            {
                isUpdatingCueEditor = false;
            }
        }

        private static string SharedText(List<SubtitleCue> selection, Func<SubtitleCue, string> selector)
        {
            string first = selector(selection[0]) ?? string.Empty;
            for (int index = 1; index < selection.Count; index++)
            {
                string value = selector(selection[index]) ?? string.Empty;
                if (!string.Equals(first, value, StringComparison.Ordinal))
                {
                    return MixedValueText;
                }
            }

            return first;
        }

        private void ApplyMixedLook(TextBox box, bool mixed)
        {
            if (mixed)
            {
                box.Foreground = (Brush)FindResource("SubtleTextBrush");
            }
            else
            {
                box.ClearValue(Control.ForegroundProperty);
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

        private void CueTextEditorTextChangedCore(object sender, TextChangedEventArgs e)
        {
            if (isUpdatingCueEditor)
            {
                return;
            }

            List<SubtitleCue> selection = SelectedCueList();
            if (selection.Count == 0)
            {
                return;
            }

            string value = CueTextEditor.Text;
            if (cueTextMixed)
            {
                if (string.Equals(value, MixedValueText, StringComparison.Ordinal))
                {
                    // The placeholder is still on screen: nothing was typed yet.
                    return;
                }

                cueTextMixed = false;
                ApplyMixedLook(CueTextEditor, false);
            }

            foreach (SubtitleCue cue in selection)
            {
                cue.Text = value;
            }

            DrawTimeline();
            UpdateOverlay(CurrentPosition);
        }

        private void CueTextEditorGotFocusCore(object sender, RoutedEventArgs e)
        {
            if (!cueTextMixed)
            {
                return;
            }

            // Typing has to replace the placeholder instead of appending to it.
            CueTextEditor.SelectAll();
        }

        private void StartEditorLostFocusCore(object sender, RoutedEventArgs e)
        {
            CommitTimeEditors();
        }

        private void EndEditorLostFocusCore(object sender, RoutedEventArgs e)
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
            List<SubtitleCue> selection = SelectedCueList();
            if (selection.Count == 0 || isUpdatingCueEditor)
            {
                return;
            }

            // Definite-assignment: with the short-circuit `&&` the out parameter is not guaranteed
            // to be written, so both are seeded first.
            TimeSpan start = TimeSpan.Zero;
            bool hasStart = !startMixed;
            if (hasStart)
            {
                hasStart = SubtitleCue.TryParseTimestamp(StartEditor.Text, out start);
            }

            TimeSpan end = TimeSpan.Zero;
            bool hasEnd = !endMixed;
            if (hasEnd)
            {
                hasEnd = SubtitleCue.TryParseTimestamp(EndEditor.Text, out end);
            }

            if (!hasStart && !hasEnd)
            {
                // A mixed field that was never retyped keeps whatever each cue already had.
                RefreshCueEditor();
                return;
            }

            List<SubtitleCue> before = CaptureCueSnapshot();
            bool multiple = selection.Count > 1;

            foreach (SubtitleCue cue in selection)
            {
                TimeSpan length = cue.End - cue.Start;

                if (hasStart)
                {
                    cue.Start = start;
                    if (multiple && length > TimeSpan.Zero)
                    {
                        // Across several cues the start moves them, it does not flatten them onto
                        // one instant, so every cue keeps its own length.
                        cue.End = start + length;
                    }
                }

                if (hasEnd)
                {
                    cue.End = end;
                }

                if (cue.End <= cue.Start)
                {
                    cue.End = cue.Start + TimeSpan.FromMilliseconds(200);
                }
            }

            CuePostProcessor.Renumber(cues);
            PushCueUndoIfChanged("cue timing", before);
            RefreshCueEditor();
            DrawTimeline();
            UpdateOverlay(CurrentPosition);
        }

        private void SetStartClickCore(object sender, RoutedEventArgs e)
        {
            List<SubtitleCue> selection = SelectedCueList();
            if (selection.Count == 0)
            {
                return;
            }

            foreach (SubtitleCue cue in selection)
            {
                cue.Start = CurrentPosition;
                if (cue.End <= cue.Start)
                {
                    cue.End = cue.Start + TimeSpan.FromMilliseconds(400);
                }
            }

            RefreshCueEditor();
            DrawTimeline();
            UpdateOverlay(CurrentPosition);
        }

        private void SetEndClickCore(object sender, RoutedEventArgs e)
        {
            List<SubtitleCue> selection = SelectedCueList();
            if (selection.Count == 0)
            {
                return;
            }

            foreach (SubtitleCue cue in selection)
            {
                cue.End = CurrentPosition;
                if (cue.End <= cue.Start)
                {
                    cue.Start = cue.End - TimeSpan.FromMilliseconds(400);
                    if (cue.Start < TimeSpan.Zero)
                    {
                        cue.Start = TimeSpan.Zero;
                    }
                }
            }

            RefreshCueEditor();
            DrawTimeline();
            UpdateOverlay(CurrentPosition);
        }

        private void NudgeBackClickCore(object sender, RoutedEventArgs e)
        {
            NudgeSelected(TimeSpan.FromMilliseconds(-100));
        }

        private void NudgeForwardClickCore(object sender, RoutedEventArgs e)
        {
            NudgeSelected(TimeSpan.FromMilliseconds(100));
        }

        /// <summary>
        /// Keyboard nudge: moves the selected cue and drags the playhead along with it, so the
        /// preview keeps showing the cue that is being moved.
        /// </summary>
        private void NudgeSelectedAndPlayhead(TimeSpan delta)
        {
            if (SelectedCueList().Count == 0)
            {
                SetStatus("Select a cue to nudge it.");
                return;
            }

            List<SubtitleCue> before = CaptureCueSnapshot();
            NudgeSelected(delta);
            PushCueUndoIfChanged("nudge cue", before);
            SeekToSeconds(CurrentPosition.TotalSeconds + delta.TotalSeconds);

            string sign = delta.TotalMilliseconds >= 0.0 ? "+" : "-";
            SetStatus("Cue and playhead moved by "
                + sign
                + Math.Abs(delta.TotalMilliseconds).ToString("0", CultureInfo.InvariantCulture)
                + " ms.");
        }

        private void NudgeSelected(TimeSpan delta)
        {
            List<SubtitleCue> selection = SelectedCueList();
            if (selection.Count == 0)
            {
                return;
            }

            foreach (SubtitleCue cue in selection)
            {
                CuePostProcessor.ShiftCue(cue, delta);
            }

            RefreshCueEditor();
            DrawTimeline();
            UpdateOverlay(CurrentPosition);
        }

        private void SplitCueClickCore(object sender, RoutedEventArgs e)
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

        private void MergeCueClickCore(object sender, RoutedEventArgs e)
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

        private void AddCueClickCore(object sender, RoutedEventArgs e)
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

        private void DeleteCueClickCore(object sender, RoutedEventArgs e)
        {
            List<SubtitleCue> selection = SelectedCueList();
            if (selection.Count == 0)
            {
                return;
            }

            List<SubtitleCue> before = CaptureCueSnapshot();
            int index = cues.IndexOf(selection[0]);
            foreach (SubtitleCue selected in selection)
            {
                cues.Remove(selected);
            }

            PushCueUndoIfChanged("delete cues", before);
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
                transcriptionTask = BeginBackgroundTask("Transcription — " + model.DisplayName);
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
                UpdateBackgroundTask(transcriptionTask, 10.0, "Transcribing the audio clips…");
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
                FinishBackgroundTask(transcriptionTask);
                transcriptionTask = null;
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

            // Burned-in exports follow the default text style profile so the rendered video looks
            // like the preview. Profile sizes are pixels on a 1080-tall design canvas; the ASS
            // script uses PlayResY 480, hence the 480 / 1080 factor.
            StyleProfile? profile = App.Profiles.ResolveStyle(App.Settings.DefaultStyleProfileId);
            if (profile != null)
            {
                double factor = 480.0 / SubtitleOverlay.DesignHeight;

                style.FontName = profile.FontFamily;
                style.FontSize = Math.Max(8, (int)Math.Round(profile.FontSize * factor));
                style.Bold = profile.Bold;
                style.Italic = profile.Italic;
                style.PrimaryColour = StyleBrushes.ToAssColor(profile.Fill, profile.Opacity);

                OutlineLayer? outline = null;
                foreach (OutlineLayer candidate in profile.Outlines)
                {
                    if (candidate.Enabled && string.Equals(candidate.Target, EffectTarget.Font, StringComparison.OrdinalIgnoreCase))
                    {
                        outline = candidate;
                        break;
                    }
                }

                if (outline != null)
                {
                    style.OutlineThickness = (int)Math.Max(1.0, Math.Round(outline.Thickness * factor));
                    style.OutlineColour = StyleBrushes.ToAssColor(outline.Color, 1.0);
                }
                else
                {
                    style.OutlineThickness = 0;
                }

                ShadowLayer? shadow = null;
                foreach (ShadowLayer candidate in profile.Shadows)
                {
                    if (candidate.Enabled && string.Equals(candidate.Target, EffectTarget.Font, StringComparison.OrdinalIgnoreCase))
                    {
                        shadow = candidate;
                        break;
                    }
                }

                if (shadow != null)
                {
                    double depth = Math.Max(Math.Abs(shadow.OffsetX), Math.Abs(shadow.OffsetY)) * factor;
                    style.ShadowDepth = Math.Max(0.5, Math.Round(depth, 1));
                    style.BackColour = StyleBrushes.ToAssColor(shadow.Color, shadow.Opacity);
                }
            }

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

            // The waveform and the preview frames take a while on a long file, so the build has to
            // show up in the background-task list like every other long job.
            timelineBuildTask = BeginBackgroundTask("Timeline — " + Path.GetFileName(path));
            UpdateBackgroundTask(timelineBuildTask, -1.0, "Reading the audio waveform and the preview frames…");

            string? ffmpeg = FfmpegLocator.Resolve("ffmpeg", App.Settings.FfmpegDirectory);
            string? ffprobe = FfmpegLocator.Resolve("ffprobe", App.Settings.FfmpegDirectory);

            if (ffprobe != null)
            {
                try
                {
                    // One probe call answers everything the timeline needs: pixel size for the
                    // proxy decision, frame rate for the , and . stepping.
                    VideoMetrics probed = await MediaInspector
                        .GetVideoMetricsAsync(ffprobe, path, CancellationToken.None)
                        .ConfigureAwait(true);
                    videoMetrics = probed;

                    if (probed.FrameRate > 0.5)
                    {
                        frameStepSeconds = 1.0 / probed.FrameRate;
                    }
                }
                catch (Exception exception)
                {
                    CrashLog.Write(exception, "VideoMetricsProbe");
                }
            }

            if (ffmpeg == null)
            {
                UpdateBackgroundTask(timelineBuildTask, 100.0, "ffmpeg was not found.");
                FinishBackgroundTask(timelineBuildTask);
                timelineBuildTask = null;
                SetStatus("ffmpeg was not found, so the timeline has no waveform and no preview frames.");
                return;
            }

            // Waveform and thumbnails used to run one after the other, which is why the timeline
            // took so long. They are independent ffmpeg jobs, so run them together.
            Task waveformTask = BuildWaveformAsync(ffmpeg, path, audioSegment);
            Task thumbnailTask = LoadThumbnailsAsync(ffmpeg, videoSegment, total);
            await Task.WhenAll(waveformTask, thumbnailTask).ConfigureAwait(true);

            UpdateBackgroundTask(timelineBuildTask, 100.0, "Done.");
            FinishBackgroundTask(timelineBuildTask);
            timelineBuildTask = null;

            TrackTimelineView.Refresh();
            UpdateTimelineDuration();
            UpdateCommandStates();
            SetStatus("Timeline ready. Space plays, Ctrl+Shift+D splits, Ctrl+Z undoes.");

            // Decoding 1080p+ frames is what makes scrubbing stutter: give the preview a cached
            // 540p proxy in the background and switch to it when it is ready.
            QueueProxyBuild(path, total);
        }

        private async Task BuildWaveformAsync(string ffmpegPath, string path, TimelineSegment segment)
        {
            try
            {
                short[] samples = await AudioSampleLoader
                    .LoadAsync(ffmpegPath, path, CancellationToken.None)
                    .ConfigureAwait(true);

                if (!string.Equals(videoPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                segment.Samples = samples;
                segment.RebuildPeaks();
                TrackTimelineView.Refresh();
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "TimelineAudio");
                SetStatus("The audio waveform could not be built: " + exception.Message);
            }
        }

        /// <summary>
        /// Queues a preview proxy for one source file. Proxies are keyed by the file itself, so a
        /// clip that was moved, split or re-imported reuses the one already on disk.
        /// </summary>
        private void QueueProxyBuild(string? sourcePath, double durationSeconds)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                return;
            }

            if (sourceProxies.ContainsKey(sourcePath))
            {
                return;
            }

            if (sourceReadyFractions.ContainsKey(sourcePath))
            {
                // Already building, already done, or already known to need nothing.
                return;
            }

            foreach (KeyValuePair<string, double> queued in pendingProxyJobs)
            {
                if (string.Equals(queued.Key, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            pendingProxyJobs.Enqueue(new KeyValuePair<string, double>(sourcePath, durationSeconds));
            SetSourceReady(sourcePath, 0.0, null);
            _ = PumpProxyQueueAsync();
        }

        private async Task PumpProxyQueueAsync()
        {
            if (proxyBuildRunning)
            {
                return;
            }

            proxyBuildRunning = true;

            try
            {
                while (pendingProxyJobs.Count > 0)
                {
                    KeyValuePair<string, double> job = pendingProxyJobs.Dequeue();
                    string sourcePath = job.Key;
                    double duration = job.Value;

                    if (sourceProxies.ContainsKey(sourcePath))
                    {
                        continue;
                    }

                    FinishBackgroundTask(proxyTask);
                    proxyTask = BeginBackgroundTask("Preview proxy — " + Path.GetFileName(sourcePath));
                    UpdateBackgroundTask(proxyTask, -1.0, "Preparing the audio cache for preview…");

                    string? ffmpeg = FfmpegLocator.Resolve("ffmpeg", App.Settings.FfmpegDirectory);
                    string? ffprobe = FfmpegLocator.Resolve("ffprobe", App.Settings.FfmpegDirectory);
                    if (ffmpeg == null || ffprobe == null)
                    {
                        SetSourceReady(sourcePath, 1.0, null);
                        continue;
                    }

                    try
                    {
                        if (duration <= 0.0)
                        {
                            duration = await MediaInspector
                                .GetDurationSecondsAsync(ffprobe, sourcePath, CancellationToken.None)
                                .ConfigureAwait(false);
                        }

                        VideoMetrics metrics = await MediaInspector
                            .GetVideoMetricsAsync(ffprobe, sourcePath, CancellationToken.None)
                            .ConfigureAwait(false);

                        CancellationTokenSource cancellation = new CancellationTokenSource();
                        proxyCancellation = cancellation;
                        CancellationToken token = cancellation.Token;

                        try
                        {
                            // The audio-only cache is what the audio lanes play, so it is built
                            // even when the file is small enough to skip the video proxy.
                            string? audioFile = await AudioPlaybackBuilder
                                .BuildAsync(ffmpeg, sourcePath, token)
                                .ConfigureAwait(false);

                            if (!string.IsNullOrEmpty(audioFile))
                            {
                                SetSourceAudio(sourcePath, audioFile);
                            }

                            if (!metrics.HasVideo || metrics.Height <= ProxyMediaBuilder.FullHdHeight)
                            {
                                // Shrinking this would gain nothing: it is already cheap to seek.
                                SetSourceReady(sourcePath, 1.0, null);
                                continue;
                            }

                            string? proxy = await ProxyMediaBuilder
                                .BuildAsync(
                                    ffmpeg,
                                    sourcePath,
                                    metrics,
                                    duration,
                                    App.Settings.UseGpu,
                                    delegate (double fraction)
                                    {
                                        ReportProxyProgress(sourcePath, fraction);
                                    },
                                    token)
                                .ConfigureAwait(false);

                            if (string.IsNullOrEmpty(proxy))
                            {
                                // Encoding failed or was cancelled: the original still plays, so
                                // do not leave a permanently grey bar behind.
                                SetSourceReady(sourcePath, 1.0, null);
                                continue;
                            }

                            SetSourceReady(sourcePath, 1.0, proxy);
                        }
                        finally
                        {
                            proxyCancellation = null;
                            cancellation.Dispose();
                        }
                    }
                    catch (Exception exception)
                    {
                        CrashLog.Write(exception, "ProxyQueue");
                        SetSourceReady(sourcePath, 1.0, null);
                    }
                }
            }
            finally
            {
                FinishBackgroundTask(proxyTask);
                proxyTask = null;
                proxyBuildRunning = false;
            }
        }

        private double SourceReadyFraction(string? sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath))
            {
                return 1.0;
            }

            double fraction;
            if (sourceReadyFractions.TryGetValue(sourcePath, out fraction))
            {
                return fraction;
            }

            // Not tracked: no proxy needed, or one is already cached from an earlier session.
            return 1.0;
        }

        private void ReportProxyProgress(string sourcePath, double fraction)
        {
            RunOnUi(delegate
            {
                double previous;
                bool known = sourceReadyFractions.TryGetValue(sourcePath, out previous);
                if (known && (int)Math.Round(previous * 100.0) == (int)Math.Round(fraction * 100.0))
                {
                    return;
                }

                sourceReadyFractions[sourcePath] = fraction < 0.0 ? 0.0 : (fraction > 1.0 ? 1.0 : fraction);
                TrackTimelineView.Refresh();

                UpdateBackgroundTask(
                    proxyTask,
                    fraction * 100.0,
                    "Encoding the preview proxy for " + Path.GetFileName(sourcePath)
                        + " — " + Math.Round(fraction * 100.0).ToString(CultureInfo.InvariantCulture) + "%");

                int percent = (int)Math.Round(fraction * 100.0);
                if (percent < 100)
                {
                    SetStatus("Building the preview proxy for " + Path.GetFileName(sourcePath) + " — "
                        + percent.ToString(CultureInfo.InvariantCulture)
                        + "%. The blue bar on the ruler shows how much already plays smoothly; until then the original is used.");
                }
            });
        }

        private void SetSourceReady(string sourcePath, double fraction, string? proxyPath)
        {
            RunOnUi(delegate
            {
                if (!string.IsNullOrEmpty(proxyPath))
                {
                    sourceProxies[sourcePath] = proxyPath;
                }

                sourceReadyFractions[sourcePath] = fraction;
                TrackTimelineView.Refresh();

                if (string.IsNullOrEmpty(proxyPath))
                {
                    return;
                }

                SetStatus("Preview proxy ready for " + Path.GetFileName(sourcePath)
                    + " — the blue bar on the ruler covers it. Export and transcription still use the original file.");

                // Swap the preview over if the playhead sits on a clip from this source.
                double playheadSeconds = TrackTimelineView.PlayheadSeconds;
                TimelineSegment? active = ActiveVideoSegment(playheadSeconds);
                if (active != null && !string.IsNullOrEmpty(active.SourcePath)
                    && string.Equals(active.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    currentVideoSegment = null;
                    videoPlayRequested = false;
                    SafeSeek(TimeSpan.FromSeconds(playheadSeconds));
                }
            });
        }

        private void SetSourceAudio(string sourcePath, string audioPath)
        {
            RunOnUi(delegate
            {
                sourceAudioFiles[sourcePath] = audioPath;

                // Lanes reading this source were opened against the heavy original: drop their
                // players so the next cue reopens them on the audio-only file.
                InvalidateAudioPlayersForSource(sourcePath);
            });
        }

        private void RunOnUi(Action action)
        {
            if (Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            Dispatcher.BeginInvoke(action);
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
            MarkProjectDirty();
            PruneAudioPlayers();
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
                    segmentSnapshot.DurationSeconds = segment.DurationSeconds;
                    segmentSnapshot.SourceOffsetSeconds = segment.SourceOffsetSeconds;
                    segmentSnapshot.Samples = segment.Samples;
                    segmentSnapshot.Peaks = segment.Peaks;
                    segmentSnapshot.SharesSamples = segment.SharesSamples;
                    segmentSnapshot.HasRecordedAudio = segment.HasRecordedAudio;
                    segmentSnapshot.ClipFile = segment.ClipFile;
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
                    TimelineSegment segment = segmentSnapshot.Segment;
                    segment.StartSeconds = segmentSnapshot.StartSeconds;
                    segment.DurationSeconds = segmentSnapshot.DurationSeconds;
                    segment.SourceOffsetSeconds = segmentSnapshot.SourceOffsetSeconds;
                    segment.Samples = segmentSnapshot.Samples;
                    segment.Peaks = segmentSnapshot.Peaks;
                    segment.SharesSamples = segmentSnapshot.SharesSamples;
                    segment.HasRecordedAudio = segmentSnapshot.HasRecordedAudio;
                    segment.ClipFile = segmentSnapshot.ClipFile;
                    segment.IsSelected = segmentSnapshot.WasSelected;
                    InvalidateAudioPlayer(segment);
                    trackSnapshot.Track.Segments.Add(segment);
                }

                trackSnapshot.Track.IsSelected = trackSnapshot.WasSelected;
                tracks.Add(trackSnapshot.Track);
            }

            AfterTimelineStructureChanged();
        }

        // ── Seeking ────────────────────────────────────────────────────────

        private void SeekToSeconds(double seconds)
        {
            // One funnel for every seek surface: timeline clicks, slider drags, arrow keys and
            // the cue list. Playback always stops first, otherwise the playhead keeps running
            // away from the position the user just asked for.
            if (isPlaying)
            {
                PausePlayback();
            }

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

            if (isPlaying)
            {
                // Re-anchor the master clock so the playhead does not snap back.
                playbackAnchorSeconds = seconds;
                playbackClock.Reset();
                playbackClock.Start();
            }

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
            // Clicking or scrubbing the timeline while the preview is running should stop it,
            // otherwise playback keeps running away from the new position.
            if (isPlaying)
            {
                PausePlayback();
            }

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

            UndoStackPushTimeline("reorder tracks", before);

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
            UndoStackPushTimeline(label, before);

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
            AddColorEntries(colorItem.Items, track);
            menu.Items.Add(colorItem);

            MenuItem splitItem = new MenuItem();
            splitItem.Header = "Split at playhead (Ctrl+Shift+D)";
            splitItem.Click += delegate { SplitSelectedSegments(); };
            menu.Items.Add(splitItem);

            MenuItem joinItem = new MenuItem();
            joinItem.Header = "Join clips (Ctrl+Shift+J)";
            joinItem.IsEnabled = CanJoinSelection();
            joinItem.Click += delegate { JoinSelectedSegments(); };
            menu.Items.Add(joinItem);

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

            UndoStackPushTimeline("add " + name, before);

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

            UndoStackPush(
                "rename track",
                delegate
                {
                    track.Name = previous;
                    TrackTimelineView.Refresh();
                },
                delegate
                {
                    track.Name = updated;
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
            AddColorEntries(menu.Items, selection[0]);
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

            UndoStackPush(
                "track colour",
                delegate
                {
                    track.BackgroundColor = previous;
                    TrackTimelineView.Refresh();
                },
                delegate
                {
                    track.BackgroundColor = color;
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

            UndoStackPushTimeline("split clip", before);

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

            UndoStackPushTimeline("delete", before);

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
            VideoElement.IsMuted = true;

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
                InvalidateAudioPlayer(segment);
            }

            if (segment != null && written > 0 && undoSamples != null && undoPeaks != null)
            {
                TimelineSegment target = segment;
                short[] samplesSnapshot = undoSamples;
                float[] peaksSnapshot = undoPeaks;

                short[]? newSamples = target.Samples;
                float[]? newPeaks = target.Peaks;
                double newDuration = target.DurationSeconds;

                UndoStackPush(
                    "record",
                    delegate
                    {
                        target.Samples = samplesSnapshot;
                        target.Peaks = peaksSnapshot;
                        target.SharesSamples = false;
                        target.DurationSeconds = undoDuration;
                        InvalidateAudioPlayer(target);
                        AfterTimelineStructureChanged();
                    },
                    delegate
                    {
                        target.Samples = newSamples;
                        target.Peaks = newPeaks;
                        target.SharesSamples = false;
                        target.DurationSeconds = newDuration;
                        InvalidateAudioPlayer(target);
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

        private void RedoClick(object sender, RoutedEventArgs e)
        {
            RedoLastAction();
        }

        private void RedoLastAction()
        {
            string description;
            if (!undoStack.TryRedo(out description))
            {
                SetStatus("Nothing to redo.");
                return;
            }

            TrackTimelineView.Refresh();
            UpdateTimelineDuration();
            UpdateCommandStates();
            SetStatus("Redid: " + description + ".");
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

        /// <summary>
        /// Pushes a timeline snapshot edit. The "after" state is captured the moment the edit is
        /// undone, so redo can put the timeline back exactly the way the user left it.
        /// </summary>
        private void UndoStackPushTimeline(string description, TimelineSnapshot before)
        {
            TimelineSnapshot? after = null;
            undoStack.Push(new DelegateUndoAction(
                description,
                delegate
                {
                    after = CaptureTimeline();
                    RestoreTimeline(before);
                },
                delegate
                {
                    if (after != null)
                    {
                        RestoreTimeline(after);
                    }
                }));
        }

        private void UndoStackPush(string description, Action undo, Action? redo = null)
        {
            undoStack.Push(new DelegateUndoAction(description, undo, redo));
        }

        private void OnUndoStackChanged(object? sender, EventArgs e)
        {
            UndoTrackButton.IsEnabled = undoStack.CanUndo;
            RedoTrackButton.IsEnabled = undoStack.CanRedo;
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

        private bool SaveProject(bool forcePrompt)
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
                    return false;
                }

                target = dialog.FileName;
            }

            if (string.IsNullOrEmpty(target))
            {
                return false;
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
                    cueData.StyleProfileId = cue.StyleProfileId;
                    cueData.IdleProfileId = cue.IdleProfileId;
                    cueData.PopInProfileId = cue.PopInProfileId;
                    cueData.PositionX = cue.PositionX;
                    cueData.PositionY = cue.PositionY;
                    cueData.Scale = cue.Scale;
                    data.Cues.Add(cueData);
                }

                ProjectFile.Save(target, data);
                projectPath = target;
                projectDirty = false;
                UpdateTitleState();
                SetStatus("Project saved to " + Path.GetFileName(target) + ".");
                return true;
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
                return false;
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
                    SubtitleCue loadedCue = new SubtitleCue(
                        TimeSpan.FromSeconds(cueData.StartSeconds),
                        TimeSpan.FromSeconds(cueData.EndSeconds),
                        cueData.Text,
                        cueData.Confidence);
                    loadedCue.StyleProfileId = cueData.StyleProfileId;
                    loadedCue.IdleProfileId = cueData.IdleProfileId;
                    loadedCue.PopInProfileId = cueData.PopInProfileId;
                    loadedCue.PositionX = cueData.PositionX;
                    loadedCue.PositionY = cueData.PositionY;
                    loadedCue.Scale = cueData.Scale;
                    cues.Add(loadedCue);
                }

                CuePostProcessor.Renumber(cues);
                UpdateCueCount();
                DrawTimeline();
                timelineInitialized = true;
                fallbackVideoSegment = null;
                projectPath = path;
                pendingProxyJobs.Clear();
                sourceProxies.Clear();
                sourceReadyFractions.Clear();
                sourceAudioFiles.Clear();
                videoMetrics = new VideoMetrics();
                projectDirty = false;
                UpdateTitleState();
                AfterTimelineStructureChanged();
                SetStatus("Project loaded: " + Path.GetFileName(path) + " — " + tracks.Count.ToString(CultureInfo.InvariantCulture) + " tracks, "
                    + cues.Count.ToString(CultureInfo.InvariantCulture) + " cues.");

                projectDirty = false;
                UpdateTitleState();

                // Warm up proxies for every distinct video source the project references.
                foreach (TimelineTrack track in tracks)
                {
                    if (track.Kind != TrackKind.Video)
                    {
                        continue;
                    }

                    foreach (TimelineSegment segment in track.Segments)
                    {
                        if (!string.IsNullOrEmpty(segment.SourcePath))
                        {
                            QueueProxyBuild(segment.SourcePath, 0.0);
                        }
                    }
                }
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

        private const double TranscriptionChunkSeconds = 150.0;
        private const double TranscriptionChunkOverlapSeconds = 0.5;

        private async Task<List<SubtitleCue>> TranscribeSegmentsAsync(
            List<TimelineSegment> segments,
            string wavFolder,
            CancellationToken cancellationToken)
        {
            List<TranscriptionWindow> windows = new List<TranscriptionWindow>();

            int index = 0;
            foreach (TimelineSegment segment in segments)
            {
                index++;
                cancellationToken.ThrowIfCancellationRequested();

                double duration = segment.DurationSeconds;
                if (duration <= 0.05)
                {
                    continue;
                }

                // Whisper's alignment drifts and it starts looping when handed many minutes of
                // audio at once (the classic "subtitles speed up after ~3 minutes, then one cue
                // sits there forever"). Cutting the audio into independent windows bounds any
                // bad window to 150 seconds.
                double chunkSeconds = duration > TranscriptionChunkSeconds ? TranscriptionChunkSeconds : duration;
                int chunkCount = (int)Math.Ceiling(duration / chunkSeconds);
                string? ffmpeg = FfmpegLocator.Resolve("ffmpeg", App.Settings.FfmpegDirectory);

                for (int chunk = 0; chunk < chunkCount; chunk++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    double chunkStart = chunk == 0 ? 0.0 : (chunk * chunkSeconds) - TranscriptionChunkOverlapSeconds;
                    double chunkEnd = Math.Min(duration, (chunk + 1) * chunkSeconds);
                    double chunkDuration = chunkEnd - chunkStart;
                    if (chunkDuration <= 0.5)
                    {
                        continue;
                    }

                    string part = chunkCount > 1
                        ? " (part " + (chunk + 1).ToString(CultureInfo.InvariantCulture)
                            + " of " + chunkCount.ToString(CultureInfo.InvariantCulture) + ")"
                        : string.Empty;

                    SetStatus("Preparing audio " + index.ToString(CultureInfo.InvariantCulture)
                        + " of " + segments.Count.ToString(CultureInfo.InvariantCulture) + part + "…");

                    string wavPath = Path.Combine(
                        wavFolder,
                        "audio-" + Guid.NewGuid().ToString("N") + ".wav");

                    bool hasSource = false;

                    if (segment.HasRecordedAudio && segment.Samples.Length > 0)
                    {
                        int startSample = segment.TimelineToSampleIndex(segment.StartSeconds + chunkStart);
                        int sampleCount = (int)Math.Round(chunkDuration * TimelineSegment.SampleRate);
                        WavFile.Write(wavPath, segment.Samples, startSample, sampleCount);
                        hasSource = true;
                    }
                    else if (!string.IsNullOrEmpty(segment.SourcePath) && File.Exists(segment.SourcePath) && ffmpeg != null)
                    {
                        await AudioTrackExtractor
                            .ExtractSegmentAsync(
                                ffmpeg,
                                segment.SourcePath,
                                wavPath,
                                segment.SourceOffsetSeconds + chunkStart,
                                chunkDuration,
                                null,
                                cancellationToken)
                            .ConfigureAwait(true);
                        hasSource = true;
                    }

                    if (!hasSource)
                    {
                        continue;
                    }

                    TranscriptionWindow window = new TranscriptionWindow();
                    window.WavPath = wavPath;
                    window.OffsetSeconds = segment.StartSeconds + chunkStart;
                    window.SkipBeforeSeconds = chunk == 0 ? 0.0 : TranscriptionChunkOverlapSeconds;
                    windows.Add(window);
                }
            }

            if (windows.Count == 0)
            {
                return new List<SubtitleCue>();
            }

            try
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
                request.WavPath = windows[0].WavPath;
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

                // One model load for all the windows.
                TranscriptionResult result = await transcriber
                    .TranscribeWindowsAsync(request, windows, progress, new Progress<string>(SetStatus), cancellationToken)
                    .ConfigureAwait(true);

                lastRuntimeDescription = string.IsNullOrEmpty(result.RuntimeDescription)
                    ? "unknown runtime"
                    : result.RuntimeDescription;
                lastDetectedLanguage = result.DetectedLanguage;

                List<SubtitleCue> ordered = new List<SubtitleCue>(result.Cues);
                ordered.Sort(delegate (SubtitleCue left, SubtitleCue right)
                {
                    return left.Start.CompareTo(right.Start);
                });

                return ordered;
            }
            finally
            {
                foreach (TranscriptionWindow window in windows)
                {
                    TryDeleteFile(window.WavPath);
                }
            }
        }

        // ── Timeline playback ──────────────────────────────────────────────
        //
        // The preview is not "the opened file" any more: every tick the playhead decides which
        // video clip is on screen (topmost video lane that has content there) and which audio
        // clips are audible. MediaElement can only hold one file, so the primary video clip owns
        // the element and every audible audio clip gets its own MediaPlayer — which is also how
        // several audio lanes can be heard at once.

        private TimelineSegment? ActiveVideoSegment(double seconds)
        {
            if (!timelineInitialized && fallbackVideoSegment != null)
            {
                // The timeline has not been built yet — the loaded file is the only content,
                // so seeking and playing must still show it instead of a black screen.
                return fallbackVideoSegment;
            }

            foreach (TimelineTrack track in tracks)
            {
                if (track.Kind != TrackKind.Video)
                {
                    continue;
                }

                TimelineSegment? segment = track.SegmentAt(seconds);
                if (segment != null)
                {
                    return segment;
                }
            }

            return null;
        }

        private List<TimelineSegment> ActiveAudioSegments(double seconds)
        {
            List<TimelineSegment> active = new List<TimelineSegment>();
            foreach (TimelineTrack track in tracks)
            {
                if (track.Kind != TrackKind.Audio)
                {
                    continue;
                }

                TimelineSegment? segment = track.SegmentAt(seconds);
                if (segment != null)
                {
                    active.Add(segment);
                }
            }

            return active;
        }

        private static double SegmentMediaSeconds(TimelineSegment segment, double timelineSeconds)
        {
            double mediaSeconds = segment.SourceOffsetSeconds + (timelineSeconds - segment.StartSeconds);
            return mediaSeconds < 0.0 ? 0.0 : mediaSeconds;
        }

        /// <summary>
        /// A file a segment can be played from: its recorded take (written to a temporary WAV or
        /// the clip exported beside the project) or the media file it references.
        /// </summary>
        private string? SegmentMediaPath(TimelineSegment segment)
        {
            if (segment.HasRecordedAudio && segment.Samples.Length > 0)
            {
                string? known;
                if (recordedClipFiles.TryGetValue(segment, out known) && File.Exists(known))
                {
                    return known;
                }

                if (!string.IsNullOrEmpty(segment.ClipFile) && !string.IsNullOrEmpty(projectPath))
                {
                    string resolved = ProjectFile.ResolveRelativePath(projectPath, segment.ClipFile);
                    if (File.Exists(resolved))
                    {
                        recordedClipFiles[segment] = resolved;
                        return resolved;
                    }
                }

                string tempPath = Path.Combine(
                    Path.GetTempPath(),
                    "SubtitleForge",
                    "clip-" + Guid.NewGuid().ToString("N") + ".wav");
                string? folder = Path.GetDirectoryName(tempPath);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                WavFile.Write(tempPath, segment.Samples, 0, segment.Samples.Length);
                recordedClipFiles[segment] = tempPath;
                return tempPath;
            }

            string? source = segment.SourcePath;
            if (!string.IsNullOrEmpty(source))
            {
                // Decoding a 4K file just to hear its audio is what made playback stutter: use the
                // cached audio-only M4A when it exists, and the source file until it does.
                string cached;
                if (sourceAudioFiles.TryGetValue(source, out cached) && File.Exists(cached))
                {
                    return cached;
                }

                if (File.Exists(source))
                {
                    return source;
                }
            }

            return null;
        }

        private AudioPlaybackSlot? EnsureAudioPlayer(TimelineSegment segment)
        {
            AudioPlaybackSlot? slot;
            if (audioPlayers.TryGetValue(segment, out slot))
            {
                return slot;
            }

            string? mediaPath = SegmentMediaPath(segment);
            if (string.IsNullOrEmpty(mediaPath))
            {
                return null;
            }

            try
            {
                MediaPlayer player = new MediaPlayer();
                player.Volume = 1.0;
                player.Open(new Uri(mediaPath, UriKind.Absolute));

                AudioPlaybackSlot created = new AudioPlaybackSlot();
                created.Player = player;
                audioPlayers[segment] = created;
                return created;
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "EnsureAudioPlayer");
                return null;
            }
        }

        /// <summary>
        /// Keeps the audio lanes in step with the playhead.
        ///
        /// This used to re-set <see cref="MediaPlayer.Position"/> on every tick whenever it drifted
        /// more than 400 ms from the master clock, so any clock skew turned into a seek — audible
        /// as "a bit of audio every now and then". Now a slot is positioned once when it becomes
        /// active (or when the user seeks) and only re-synced if it is more than a second off.
        /// </summary>
        private void CueAudioSegments(double seconds, bool play, bool force)
        {
            List<TimelineSegment> active = ActiveAudioSegments(seconds);

            foreach (KeyValuePair<TimelineSegment, AudioPlaybackSlot> pair in audioPlayers)
            {
                if (active.Contains(pair.Key))
                {
                    continue;
                }

                try
                {
                    pair.Value.Player.Pause();
                }
                catch (Exception)
                {
                    // Player closed mid-flight — nothing to stop.
                }

                pair.Value.Playing = false;
                pair.Value.PositionApplied = false;
            }

            foreach (TimelineSegment segment in active)
            {
                AudioPlaybackSlot? slot = EnsureAudioPlayer(segment);
                if (slot == null)
                {
                    continue;
                }

                double mediaSeconds = SegmentMediaSeconds(segment, seconds);

                try
                {
                    if (force)
                    {
                        slot.PositionApplied = false;
                        slot.Playing = false;
                    }

                    TimeSpan target = TimeSpan.FromSeconds(mediaSeconds);

                    if (!slot.PositionApplied)
                    {
                        slot.Player.Position = target;
                        slot.PositionApplied = true;
                    }
                    else if (Math.Abs((slot.Player.Position - target).TotalSeconds) > 1.0)
                    {
                        // Really out of step (long stall, resumed window): resync once.
                        slot.Player.Position = target;
                        slot.Playing = false;
                    }

                    if (play && !slot.Playing)
                    {
                        slot.Player.Play();
                        slot.Playing = true;
                    }
                    else if (!play && slot.Playing)
                    {
                        slot.Player.Pause();
                        slot.Playing = false;
                    }
                }
                catch (Exception)
                {
                    // The player is still opening; the next tick retries.
                    slot.PositionApplied = false;
                }
            }

            // The preview never makes a sound of its own: all audio comes from the audio lanes.
            VideoElement.IsMuted = true;
        }

        private void CueVideoSegment(TimelineSegment? segment, double timelineSeconds, bool play)
        {
            currentVideoSegment = segment;

            string? path = segment == null ? null : segment.SourcePath;

            // The preview prefers the cached 540p proxy of the clip's source file, whichever
            // file that is and wherever the clip has been moved to.
            if (!string.IsNullOrEmpty(path))
            {
                string proxyPath;
                if (sourceProxies.TryGetValue(path, out proxyPath) && File.Exists(proxyPath))
                {
                    path = proxyPath;
                }
            }

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                try
                {
                    VideoElement.Pause();
                }
                catch (Exception)
                {
                    // Nothing loaded.
                }

                // No video under the playhead: black screen, audio keeps playing.
                VideoElement.Visibility = Visibility.Hidden;
                pendingVideoSeekSeconds = null;
                pendingVideoPlay = false;
                videoPlayRequested = false;
                return;
            }

            VideoElement.Visibility = Visibility.Visible;

            double mediaSeconds = segment != null ? SegmentMediaSeconds(segment, timelineSeconds) : 0.0;
            bool sameFile = !string.IsNullOrEmpty(currentVideoPath)
                && string.Equals(currentVideoPath, path, StringComparison.OrdinalIgnoreCase);

            pendingVideoSeekSeconds = mediaSeconds;
            pendingVideoPlay = play;

            if (!sameFile)
            {
                currentVideoPath = path;
                videoElementReady = false;
                try
                {
                    VideoElement.Source = new Uri(path, UriKind.Absolute);
                }
                catch (Exception exception)
                {
                    CrashLog.Write(exception, "CueVideoSource");
                }

                // MediaOpened applies the pending cue and play flag.
                return;
            }

            if (!videoElementReady)
            {
                // Still opening — MediaOpened applies the pending cue.
                videoPlayRequested = false;
                return;
            }

            try
            {
                VideoElement.Position = TimeSpan.FromSeconds(mediaSeconds);
                videoPlayRequested = false;
                if (play)
                {
                    VideoElement.Play();
                    videoPlayRequested = true;
                }
                else
                {
                    VideoElement.Pause();
                }

                pendingVideoSeekSeconds = null;
                pendingVideoPlay = false;
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "CueVideoSeek");
            }
        }

        private void FollowTimeline(double seconds, bool play)
        {
            TimelineSegment? videoSegment = ActiveVideoSegment(seconds);

            if (!ReferenceEquals(videoSegment, currentVideoSegment))
            {
                CueVideoSegment(videoSegment, seconds, play);
            }
            else if (!play)
            {
                try
                {
                    VideoElement.Pause();
                }
                catch (Exception)
                {
                    // Nothing loaded.
                }

                videoPlayRequested = false;
            }
            else if (videoSegment != null && videoElementReady && !videoPlayRequested)
            {
                // The clip under the playhead did not change, so nothing had asked the element
                // to roll — that is why pressing Play appeared to do nothing.
                try
                {
                    VideoElement.Play();
                    videoPlayRequested = true;
                }
                catch (Exception exception)
                {
                    CrashLog.Write(exception, "FollowPlay");
                }
            }

            CueAudioSegments(seconds, play, false);
        }

        private void StartTimelinePlayback()
        {
            if (string.IsNullOrEmpty(videoPath) && tracks.Count == 0)
            {
                return;
            }

            double seconds = TrackTimelineView.PlayheadSeconds;
            if (durationSeconds > 0.0 && seconds >= durationSeconds - 0.05)
            {
                // Pressed play at the very end — start over instead of doing nothing.
                seconds = 0.0;
            }

            playbackAnchorSeconds = seconds;
            playbackClock.Reset();
            playbackClock.Start();

            isPlaying = true;
            PlayPauseButton.Content = "❚❚ Pause";

            FollowTimeline(seconds, true);
            playbackTimer.Start();
        }

        private void StopTimelinePlayback()
        {
            playbackClock.Stop();
            isPlaying = false;
            videoPlayRequested = false;

            if (PlayPauseButton != null)
            {
                PlayPauseButton.Content = "▶ Play";
            }

            playbackTimer.Stop();

            try
            {
                VideoElement.Pause();
            }
            catch (Exception)
            {
                // Nothing loaded.
            }

            foreach (KeyValuePair<TimelineSegment, AudioPlaybackSlot> pair in audioPlayers)
            {
                try
                {
                    pair.Value.Player.Pause();
                }
                catch (Exception)
                {
                    // Player already gone.
                }

                pair.Value.Playing = false;
                pair.Value.PositionApplied = false;
            }
        }

        private void PruneAudioPlayers()
        {
            List<TimelineSegment> live = new List<TimelineSegment>();
            foreach (TimelineTrack track in tracks)
            {
                foreach (TimelineSegment segment in track.Segments)
                {
                    live.Add(segment);
                }
            }

            List<TimelineSegment> stale = new List<TimelineSegment>();
            foreach (KeyValuePair<TimelineSegment, AudioPlaybackSlot> pair in audioPlayers)
            {
                if (!live.Contains(pair.Key))
                {
                    stale.Add(pair.Key);
                }
            }

            foreach (TimelineSegment segment in stale)
            {
                AudioPlaybackSlot slot;
                if (audioPlayers.TryGetValue(segment, out slot))
                {
                    try
                    {
                        slot.Player.Close();
                    }
                    catch (Exception)
                    {
                        // Already closed.
                    }

                    audioPlayers.Remove(segment);
                }
            }
        }

        private void InvalidateAudioPlayer(TimelineSegment segment)
        {
            AudioPlaybackSlot slot;
            if (audioPlayers.TryGetValue(segment, out slot))
            {
                try
                {
                    slot.Player.Close();
                }
                catch (Exception)
                {
                    // Already closed.
                }

                audioPlayers.Remove(segment);
            }

            recordedClipFiles.Remove(segment);
        }

        /// <summary>
        /// Drops the players of every lane that reads this source, so the next cue reopens them
        /// against the freshly cached audio-only file.
        /// </summary>
        private void InvalidateAudioPlayersForSource(string sourcePath)
        {
            List<TimelineSegment> affected = new List<TimelineSegment>();
            foreach (KeyValuePair<TimelineSegment, AudioPlaybackSlot> pair in audioPlayers)
            {
                if (!string.IsNullOrEmpty(pair.Key.SourcePath)
                    && string.Equals(pair.Key.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    affected.Add(pair.Key);
                }
            }

            foreach (TimelineSegment segment in affected)
            {
                InvalidateAudioPlayer(segment);
            }
        }

        private void DisposeAudioPlayers()
        {
            foreach (KeyValuePair<TimelineSegment, AudioPlaybackSlot> pair in audioPlayers)
            {
                try
                {
                    pair.Value.Player.Close();
                }
                catch (Exception)
                {
                    // Already closed.
                }
            }

            audioPlayers.Clear();
        }

        // ── Audio files dropped from Explorer ───────────────────────────────

        private async void TrackTimelineAudioFilesDropped(object? sender, AudioFilesDroppedEventArgs e)
        {
            if (e.Paths == null || e.Paths.Length == 0)
            {
                return;
            }

            string? ffmpeg = FfmpegLocator.Resolve("ffmpeg", App.Settings.FfmpegDirectory);
            string? ffprobe = FfmpegLocator.Resolve("ffprobe", App.Settings.FfmpegDirectory);

            if (ffprobe == null)
            {
                SetStatus("ffprobe was not found, so the dropped files cannot be measured.");
                return;
            }

            TimelineSnapshot before = CaptureTimeline();
            double insertSeconds = e.Seconds > 0.0 ? e.Seconds : 0.0;
            int insertRow = e.RowIndex;
            int added = 0;

            foreach (string path in e.Paths)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    double fileDuration = await MediaInspector
                        .GetDurationSecondsAsync(ffprobe, path, CancellationToken.None)
                        .ConfigureAwait(true);

                    if (fileDuration <= 0.0)
                    {
                        SetStatus("Could not read the length of " + Path.GetFileName(path) + ".");
                        continue;
                    }

                    // Dropped files become their own lane named after the file: audio files get
                    // a waveform exactly like a recorded take, video files get a video lane with
                    // a thumbnail strip and their own preview proxy.
                    VideoMetrics droppedMetrics = new VideoMetrics();
                    try
                    {
                        droppedMetrics = await MediaInspector
                            .GetVideoMetricsAsync(ffprobe, path, CancellationToken.None)
                            .ConfigureAwait(true);
                    }
                    catch (Exception exception)
                    {
                        CrashLog.Write(exception, "DropMetrics");
                    }

                    TrackKind kind = droppedMetrics.HasVideo ? TrackKind.Video : TrackKind.Audio;

                    TimelineTrack track = new TimelineTrack(kind, Path.GetFileName(path), NextTrackColor(kind));

                    TimelineSegment segment = new TimelineSegment(kind);
                    segment.StartSeconds = insertSeconds;
                    segment.SourceOffsetSeconds = 0.0;
                    segment.DurationSeconds = fileDuration;
                    segment.SourceDuration = fileDuration;
                    segment.SourcePath = path;
                    segment.IsSelected = true;
                    track.Segments.Add(segment);

                    if (insertRow >= 0 && insertRow <= tracks.Count)
                    {
                        tracks.Insert(insertRow, track);
                        insertRow++;
                    }
                    else
                    {
                        tracks.Add(track);
                    }

                    added++;

                    if (ffmpeg != null && kind == TrackKind.Video)
                    {
                        try
                        {
                            int thumbnailCount = 16;
                            List<BitmapSource> extracted = await ThumbnailExtractor
                                .ExtractAsync(ffmpeg, path, fileDuration, thumbnailCount, 56, CancellationToken.None)
                                .ConfigureAwait(true);

                            if (extracted.Count > 0)
                            {
                                segment.Thumbnails = extracted.ToArray();
                            }
                        }
                        catch (Exception exception)
                        {
                            CrashLog.Write(exception, "DropThumbnails");
                        }

                        // Only this file gets encoded — cached proxies of other sources stay.
                        QueueProxyBuild(path, fileDuration);

                        // A video clip has to be audible too: give it an audio lane directly
                        // below, filled with its own audio, exactly like an imported audio file.
                        TimelineTrack videoAudioTrack = new TimelineTrack(
                            TrackKind.Audio,
                            Path.GetFileNameWithoutExtension(path) + " audio",
                            NextTrackColor(TrackKind.Audio));

                        TimelineSegment videoAudioSegment = new TimelineSegment(TrackKind.Audio);
                        videoAudioSegment.StartSeconds = insertSeconds;
                        videoAudioSegment.SourceOffsetSeconds = 0.0;
                        videoAudioSegment.DurationSeconds = fileDuration;
                        videoAudioSegment.SourceDuration = fileDuration;
                        videoAudioSegment.SourcePath = path;
                        videoAudioTrack.Segments.Add(videoAudioSegment);

                        if (insertRow >= 0 && insertRow <= tracks.Count)
                        {
                            tracks.Insert(insertRow, videoAudioTrack);
                            insertRow++;
                        }
                        else
                        {
                            tracks.Add(videoAudioTrack);
                        }

                        try
                        {
                            short[] videoAudioSamples = await AudioSampleLoader
                                .LoadAsync(ffmpeg, path, CancellationToken.None)
                                .ConfigureAwait(true);
                            videoAudioSegment.Samples = videoAudioSamples;
                            videoAudioSegment.RebuildPeaks();
                        }
                        catch (Exception exception)
                        {
                            CrashLog.Write(exception, "DropVideoAudio");
                        }

                        videoAudioTrack.NotifyContentChanged();
                    }
                    else if (ffmpeg != null)
                    {
                        try
                        {
                            short[] samples = await AudioSampleLoader
                                .LoadAsync(ffmpeg, path, CancellationToken.None)
                                .ConfigureAwait(true);
                            segment.Samples = samples;
                            segment.RebuildPeaks();
                        }
                        catch (Exception exception)
                        {
                            CrashLog.Write(exception, "DropAudioSamples");
                        }
                    }

                    track.NotifyContentChanged();
                }
                catch (Exception exception)
                {
                    CrashLog.Write(exception, "DropAudioFile");
                    SetStatus("Could not add " + Path.GetFileName(path) + ": " + exception.Message);
                }
            }

            if (added == 0)
            {
                return;
            }

            string label = added == 1 ? "add dropped file" : "add " + added.ToString(CultureInfo.InvariantCulture) + " dropped files";
            UndoStackPushTimeline(label, before);

            AfterTimelineStructureChanged();

            SetStatus(added == 1
                ? "Added the dropped file as its own track. Ctrl+Z undoes it."
                : "Added " + added.ToString(CultureInfo.InvariantCulture) + " dropped files as tracks. Ctrl+Z undoes it.");
        }

        // ── Profile editors ────────────────────────────────────────────────

        private void TextStyleClick(object sender, RoutedEventArgs e)
        {
            StyleProfileWindow window = new StyleProfileWindow(
                App.Profiles,
                App.Settings.DefaultStyleProfileId,
                App.Settings.DefaultStyleProfileId);
            window.PreviewIdle = App.Profiles.ResolveIdle(App.Settings.DefaultIdleProfileId);
            window.PreviewPopIn = App.Profiles.ResolvePopIn(App.Settings.DefaultPopInProfileId);
            window.Owner = this;

            bool saved = window.ShowDialog() == true;
            ApplyProfileEdits(window.DefaultChanged, window.DefaultProfileId, null, null, saved);
        }

        private void IdleProfileClick(object sender, RoutedEventArgs e)
        {
            IdleProfileWindow window = new IdleProfileWindow(
                App.Profiles,
                App.Settings.DefaultIdleProfileId,
                App.Settings.DefaultIdleProfileId);
            window.PreviewStyle = App.Profiles.ResolveStyle(App.Settings.DefaultStyleProfileId);
            window.Owner = this;

            bool saved = window.ShowDialog() == true;
            ApplyProfileEdits(window.DefaultChanged, null, window.DefaultProfileId, null, saved);
        }

        private void PopInProfileClick(object sender, RoutedEventArgs e)
        {
            PopInProfileWindow window = new PopInProfileWindow(
                App.Profiles,
                App.Settings.DefaultPopInProfileId,
                App.Settings.DefaultPopInProfileId);
            window.PreviewStyle = App.Profiles.ResolveStyle(App.Settings.DefaultStyleProfileId);
            window.PreviewIdle = App.Profiles.ResolveIdle(App.Settings.DefaultIdleProfileId);
            window.Owner = this;

            bool saved = window.ShowDialog() == true;
            ApplyProfileEdits(window.DefaultChanged, null, null, window.DefaultProfileId, saved);
        }

        private void ApplyProfileEdits(
            bool defaultChanged,
            string? styleId,
            string? idleId,
            string? popInId,
            bool saved)
        {
            if (defaultChanged)
            {
                if (!string.IsNullOrEmpty(styleId))
                {
                    App.Settings.DefaultStyleProfileId = styleId;
                }

                if (!string.IsNullOrEmpty(idleId))
                {
                    App.Settings.DefaultIdleProfileId = idleId;
                }

                if (!string.IsNullOrEmpty(popInId))
                {
                    App.Settings.DefaultPopInProfileId = popInId;
                }

                App.Settings.Save();
            }

            App.Profiles.Save();
            RefreshCueAppearanceCombos();
            UpdateOverlay(CurrentPosition);

            if (defaultChanged)
            {
                SetStatus("New default profile saved — every cue without its own override uses it.");
            }
            else if (saved)
            {
                SetStatus("Profile saved to " + ProfileStore.Folder + ".");
            }
        }

        private void OnOverlayTimerTick(object? sender, EventArgs e)
        {
            idleSeconds = overlayClock.Elapsed.TotalSeconds;

            // The playback timer only runs every 120 ms, so while playing the subtitle clock used to
            // advance in eight coarse steps a second: the text stepped while the video ran smoothly.
            // Advance the subtitle clock here, on the 16 ms timer, along with the idle channels.
            if (isPlaying && playbackClock != null)
            {
                double timelineSeconds = playbackAnchorSeconds + playbackClock.Elapsed.TotalSeconds;
                if (timelineSeconds < 0.0)
                {
                    timelineSeconds = 0.0;
                }

                UpdateOverlay(TimeSpan.FromSeconds(timelineSeconds));
            }

            if (!SubtitleOverlayView.HasContent)
            {
                return;
            }

            SubtitleOverlayView.IdleSeconds = idleSeconds;

            // Repaint only when something can actually change: an idle channel, a running entrance
            // or a new word (the highlight and the words-per-display window both follow the word
            // index). A static cue on a static style costs nothing.
            bool wordChanged = SubtitleOverlayView.CurrentWordIndex != SubtitleOverlayView.RenderedWordIndex;
            if (SubtitleOverlayView.IsAnimating || wordChanged)
            {
                SubtitleOverlayView.Refresh();
            }
        }

        // ── Export ─────────────────────────────────────────────────────────

        private void ExportClick(object sender, RoutedEventArgs e)
        {
            if (isBusy)
            {
                SetStatus("Wait for the current job to finish before exporting.");
                return;
            }

            ContextMenu menu = new ContextMenu();

            MenuItem srt = new MenuItem();
            srt.Header = "Subtitles as SubRip (.srt)…";
            srt.IsEnabled = cues.Count > 0;
            srt.Click += delegate { ExportSubtitlesAs("srt"); };
            menu.Items.Add(srt);

            MenuItem vtt = new MenuItem();
            vtt.Header = "Subtitles as WebVTT (.vtt)…";
            vtt.IsEnabled = cues.Count > 0;
            vtt.Click += delegate { ExportSubtitlesAs("vtt"); };
            menu.Items.Add(vtt);

            MenuItem ass = new MenuItem();
            ass.Header = "Subtitles as Advanced SubStation (.ass)…";
            ass.IsEnabled = cues.Count > 0;
            ass.Click += delegate { ExportSubtitlesAs("ass"); };
            menu.Items.Add(ass);

            menu.Items.Add(new Separator());

            MenuItem media = new MenuItem();
            media.Header = "As MP4…";
            media.Click += ExportMediaClick;
            menu.Items.Add(media);

            menu.PlacementTarget = ExportButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void ExportSubtitlesAs(string format)
        {
            if (!EnsureCuesAvailable())
            {
                return;
            }

            ApplyUiToSettings();

            string extension = "." + format;
            string filter;
            switch (format)
            {
                case "vtt":
                    filter = "WebVTT subtitles|*.vtt";
                    break;
                case "ass":
                    filter = "Advanced SubStation subtitles|*.ass";
                    break;
                default:
                    filter = "SubRip subtitles|*.srt";
                    break;
            }

            SaveFileDialog dialog = new SaveFileDialog();
            dialog.Title = "Export subtitles";
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

            string outputPath = dialog.FileName;
            RememberDirectory(outputPath);

            try
            {
                WriteSubtitleFile(outputPath, format);
                SetStatus("Saved " + Path.GetFileName(outputPath) + ".");
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "ExportSubtitles");
                SetStatus("Could not write the subtitle file.");
                MessageBox.Show(
                    this,
                    exception.Message,
                    "SubtitleForge",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void WriteSubtitleFile(string outputPath, string format)
        {
            switch (format)
            {
                case "vtt":
                    SubtitleWriter.SaveVtt(outputPath, cues);
                    break;
                case "ass":
                    SubtitleWriter.SaveAss(outputPath, cues, BuildAssStyle());
                    break;
                default:
                    SubtitleWriter.SaveSrt(outputPath, cues);
                    break;
            }
        }

        private async void ExportMediaClick(object sender, RoutedEventArgs e)
        {
            if (isBusy)
            {
                return;
            }

            ApplyUiToSettings();

            ExportWindow window = new ExportWindow();
            window.Owner = this;
            if (window.ShowDialog() != true)
            {
                return;
            }

            MediaExportOptions options = window.Options;
            options.SourcePath = ResolveExportSourcePath();
            options.DurationSeconds = durationSeconds > 0.0 ? durationSeconds : EstimatedDuration();
            options.Width = videoMetrics.Width > 0 ? videoMetrics.Width : 1920;
            options.Height = videoMetrics.Height > 0 ? videoMetrics.Height : 1080;
            options.FrameRate = videoMetrics.FrameRate > 1.0 ? videoMetrics.FrameRate : 30.0;

            bool solidBackdrop = options.BurnSubtitles && !options.IncludeVideo;
            if (!solidBackdrop && options.NeedsMediaFile && string.IsNullOrEmpty(options.SourcePath))
            {
                SetStatus("There is no video to export from yet — open one first.");
                return;
            }

            if (options.BurnSubtitles && cues.Count == 0)
            {
                SetStatus("There are no cues to burn in.");
                return;
            }

            string? subtitlePath = null;
            if (options.IncludeSubtitles)
            {
                SaveFileDialog subtitleDialog = new SaveFileDialog();
                subtitleDialog.Title = "Export subtitles";
                subtitleDialog.Filter = SubtitleFilterFor(options.SubtitleFormat);
                subtitleDialog.DefaultExt = "." + options.SubtitleFormat;
                subtitleDialog.FileName = BuildDefaultName("." + options.SubtitleFormat);
                subtitleDialog.AddExtension = true;
                subtitleDialog.OverwritePrompt = true;
                ApplyStartDirectory(subtitleDialog);

                if (subtitleDialog.ShowDialog(this) != true)
                {
                    return;
                }

                subtitlePath = subtitleDialog.FileName;
                RememberDirectory(subtitlePath);
            }

            string? mediaPath = null;
            if (options.NeedsMediaFile)
            {
                string extension = MediaExporter.SuggestedExtension(options);
                SaveFileDialog mediaDialog = new SaveFileDialog();
                mediaDialog.Title = "Export media";
                mediaDialog.Filter = extension == ".mp4" ? "MP4 video|*.mp4" : "M4A audio|*.m4a";
                mediaDialog.DefaultExt = extension;
                mediaDialog.FileName = MediaExporter.SuggestedFileName(BaseNameForExport(), options);
                mediaDialog.AddExtension = true;
                mediaDialog.OverwritePrompt = true;
                ApplyStartDirectory(mediaDialog);

                if (mediaDialog.ShowDialog(this) != true)
                {
                    return;
                }

                mediaPath = mediaDialog.FileName;
                RememberDirectory(mediaPath);
            }

            if (subtitlePath != null)
            {
                try
                {
                    WriteSubtitleFile(subtitlePath, options.SubtitleFormat);
                    SetStatus("Saved " + Path.GetFileName(subtitlePath) + ".");
                }
                catch (Exception exception)
                {
                    CrashLog.Write(exception, "ExportSubtitles");
                    MessageBox.Show(
                        this,
                        exception.Message,
                        "SubtitleForge",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }

            if (mediaPath == null)
            {
                return;
            }

            string? ffmpeg = FfmpegLocator.Resolve("ffmpeg", App.Settings.FfmpegDirectory);
            if (ffmpeg == null)
            {
                SetStatus("ffmpeg was not found — exporting media needs it.");
                MessageBox.Show(
                    this,
                    "ffmpeg.exe was not found.\n\nInstall ffmpeg and either put it on PATH or set \"FfmpegDirectory\" in:\n"
                    + AppSettings.SettingsPath,
                    "SubtitleForge",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            BackgroundTask task = BeginBackgroundTask("Export — " + Path.GetFileName(mediaPath));
            operationCancellation = new CancellationTokenSource();
            CancellationToken token = operationCancellation.Token;
            SetBusy(true);
            SetProgress(0.0);

            try
            {
                SetStatus("Exporting " + Path.GetFileName(mediaPath) + "…");
                IProgress<double> progress = new Progress<double>(delegate (double fraction)
                {
                    SetProgress(fraction);
                    UpdateBackgroundTask(
                        task,
                        fraction * 100.0,
                        "Exporting " + Path.GetFileName(mediaPath) + " — "
                            + Math.Round(fraction * 100.0).ToString(CultureInfo.InvariantCulture) + "%");
                });

                await MediaExporter
                    .ExportAsync(
                        ffmpeg,
                        options,
                        cues,
                        BuildAssStyle(),
                        options.SourcePath,
                        mediaPath,
                        progress,
                        token)
                    .ConfigureAwait(true);

                SetProgress(1.0);
                SetStatus("Exported " + Path.GetFileName(mediaPath) + ".");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Export cancelled.");
                TryDeleteFile(mediaPath);
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, "ExportMedia");
                SetStatus("Export failed.");
                MessageBox.Show(
                    this,
                    exception.Message,
                    "SubtitleForge",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                TryDeleteFile(mediaPath);
            }
            finally
            {
                SetBusy(false);
                FinishBackgroundTask(task);
            }
        }

        private static string SubtitleFilterFor(string format)
        {
            switch (format)
            {
                case "vtt":
                    return "WebVTT subtitles|*.vtt";
                case "ass":
                    return "Advanced SubStation subtitles|*.ass";
                default:
                    return "SubRip subtitles|*.srt";
            }
        }

        private string BaseNameForExport()
        {
            string source = ResolveExportSourcePath();
            if (!string.IsNullOrEmpty(source))
            {
                return Path.GetFileNameWithoutExtension(source);
            }

            return "export";
        }

        private string ResolveExportSourcePath()
        {
            if (!string.IsNullOrEmpty(videoPath))
            {
                return videoPath;
            }

            foreach (TimelineTrack track in tracks)
            {
                if (track.Kind != TrackKind.Video)
                {
                    continue;
                }

                foreach (TimelineSegment segment in track.Segments)
                {
                    if (!string.IsNullOrEmpty(segment.SourcePath))
                    {
                        return segment.SourcePath!;
                    }
                }
            }

            return string.Empty;
        }

        // ── Background tasks ───────────────────────────────────────────────

        private readonly ObservableCollection<BackgroundTask> backgroundTasks = new ObservableCollection<BackgroundTask>();

        private BackgroundTask? proxyTask;

        private BackgroundTask? transcriptionTask;

        private BackgroundTask? timelineBuildTask;

        private BackgroundTask BeginBackgroundTask(string title)
        {
            BackgroundTask task = new BackgroundTask();
            task.Title = title;
            task.Detail = "Starting…";
            task.Percent = 0.0;
            task.IsIndeterminate = true;

            RunOnUi(delegate
            {
                if (BackgroundTasksList.ItemsSource == null)
                {
                    BackgroundTasksList.ItemsSource = backgroundTasks;
                }

                backgroundTasks.Add(task);
                UpdateBackgroundTaskIndicator();
            });

            return task;
        }

        private void UpdateBackgroundTask(BackgroundTask? task, double percent, string detail)
        {
            if (task == null)
            {
                return;
            }

            RunOnUi(delegate
            {
                task.IsIndeterminate = percent < 0.0;
                if (percent >= 0.0)
                {
                    task.Percent = percent > 100.0 ? 100.0 : percent;
                }

                if (!string.IsNullOrEmpty(detail))
                {
                    task.Detail = detail;
                }
            });
        }

        private void FinishBackgroundTask(BackgroundTask? task)
        {
            if (task == null)
            {
                return;
            }

            RunOnUi(delegate
            {
                backgroundTasks.Remove(task);
                UpdateBackgroundTaskIndicator();

                if (backgroundTasks.Count == 0 && BackgroundTasksPopup.IsOpen)
                {
                    BackgroundTasksPopup.IsOpen = false;
                }
            });
        }

        private void UpdateBackgroundTaskIndicator()
        {
            int count = backgroundTasks.Count;
            if (count <= 0)
            {
                BackgroundTaskCountText.Visibility = Visibility.Collapsed;
                BackgroundTaskCountText.Text = string.Empty;
                return;
            }

            BackgroundTaskCountText.Text = count == 1
                ? "1 task running"
                : count.ToString(CultureInfo.InvariantCulture) + " tasks running";
            BackgroundTaskCountText.Visibility = Visibility.Visible;
        }

        private void StatusBarClick(object sender, MouseButtonEventArgs e)
        {
            BackgroundTasksList.ItemsSource = backgroundTasks;
            NoBackgroundTasksText.Visibility = backgroundTasks.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (backgroundTasks.Count == 0)
            {
                SetStatus("Nothing is running in the background right now.");
            }

            // Always open the panel: closing it on an empty list just looked like a dead click.
            BackgroundTasksPopup.IsOpen = !BackgroundTasksPopup.IsOpen;
        }

        // ── Joining clips ──────────────────────────────────────────────────

        private void JoinSelectedSegments()
        {
            List<KeyValuePair<TimelineTrack, TimelineSegment>> selection = SelectedSegments();
            if (selection.Count < 2)
            {
                SetStatus("Select two or more neighbouring clips on one track, then join them.");
                return;
            }

            List<TimelineTrack> trackOrder = new List<TimelineTrack>();
            Dictionary<TimelineTrack, List<TimelineSegment>> grouped =
                new Dictionary<TimelineTrack, List<TimelineSegment>>();

            foreach (KeyValuePair<TimelineTrack, TimelineSegment> entry in selection)
            {
                List<TimelineSegment> group;
                if (!grouped.TryGetValue(entry.Key, out group))
                {
                    group = new List<TimelineSegment>();
                    grouped[entry.Key] = group;
                    trackOrder.Add(entry.Key);
                }

                if (!group.Contains(entry.Value))
                {
                    group.Add(entry.Value);
                }
            }

            TimelineSnapshot before = CaptureTimeline();
            int joinCount = 0;

            foreach (TimelineTrack track in trackOrder)
            {
                List<TimelineSegment> group = grouped[track];
                if (group.Count < 2)
                {
                    continue;
                }

                group.Sort(delegate (TimelineSegment left, TimelineSegment right)
                {
                    return left.StartSeconds.CompareTo(right.StartSeconds);
                });

                if (!CanJoinSegments(group))
                {
                    continue;
                }

                TimelineSegment first = group[0];
                TimelineSegment last = group[group.Count - 1];
                double newDuration = last.EndSeconds - first.StartSeconds;

                first.DurationSeconds = newDuration;
                first.SourceDuration = first.SourceOffsetSeconds + newDuration;
                first.IsSelected = true;

                for (int index = 1; index < group.Count; index++)
                {
                    group[index].IsSelected = false;
                    track.Segments.Remove(group[index]);
                }

                joinCount++;
            }

            if (joinCount == 0)
            {
                SetStatus("Only neighbouring clips that come from the same source can be joined.");
                return;
            }

            UndoStackPushTimeline("join clips", before);

            AfterTimelineStructureChanged();
            SetStatus(joinCount == 1
                ? "Joined the selected clips. Ctrl+Z undoes it."
                : "Joined the selected clips on " + joinCount.ToString(CultureInfo.InvariantCulture)
                    + " tracks. Ctrl+Z undoes it.");
        }

        private bool CanJoinSelection()
        {
            List<KeyValuePair<TimelineTrack, TimelineSegment>> selection = SelectedSegments();
            if (selection.Count < 2)
            {
                return false;
            }

            Dictionary<TimelineTrack, List<TimelineSegment>> grouped =
                new Dictionary<TimelineTrack, List<TimelineSegment>>();

            foreach (KeyValuePair<TimelineTrack, TimelineSegment> entry in selection)
            {
                List<TimelineSegment> group;
                if (!grouped.TryGetValue(entry.Key, out group))
                {
                    group = new List<TimelineSegment>();
                    grouped[entry.Key] = group;
                }

                if (!group.Contains(entry.Value))
                {
                    group.Add(entry.Value);
                }
            }

            foreach (KeyValuePair<TimelineTrack, List<TimelineSegment>> pair in grouped)
            {
                List<TimelineSegment> group = pair.Value;
                if (group.Count < 2)
                {
                    continue;
                }

                group.Sort(delegate (TimelineSegment left, TimelineSegment right)
                {
                    return left.StartSeconds.CompareTo(right.StartSeconds);
                });

                if (CanJoinSegments(group))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CanJoinSegments(List<TimelineSegment> group)
        {
            const double tolerance = 0.05;
            TimelineSegment first = group[0];

            for (int index = 1; index < group.Count; index++)
            {
                TimelineSegment previous = group[index - 1];
                TimelineSegment current = group[index];

                if (current.StartSeconds - previous.EndSeconds > tolerance)
                {
                    return false;
                }

                double previousSourceEnd = previous.SourceOffsetSeconds
                    + (previous.EndSeconds - previous.StartSeconds);
                if (Math.Abs(previousSourceEnd - current.SourceOffsetSeconds) > tolerance)
                {
                    return false;
                }

                if (!SharesSource(first, current))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SharesSource(TimelineSegment left, TimelineSegment right)
        {
            if (ReferenceEquals(left.Samples, right.Samples) && left.Samples.Length > 0)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(left.SourcePath)
                && !string.IsNullOrEmpty(right.SourcePath)
                && string.Equals(left.SourcePath, right.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        // ── Cue editing undo ───────────────────────────────────────────────

        private List<SubtitleCue>? pendingCueTextSnapshot;

        private List<SubtitleCue>? pendingPlacementSnapshot;

        private List<SubtitleCue> CaptureCueSnapshot()
        {
            List<SubtitleCue> snapshot = new List<SubtitleCue>(cues.Count);
            foreach (SubtitleCue cue in cues)
            {
                snapshot.Add(cue.Clone());
            }

            return snapshot;
        }

        private static bool CueSnapshotsMatch(List<SubtitleCue> before, IList<SubtitleCue> current)
        {
            if (before.Count != current.Count)
            {
                return false;
            }

            for (int index = 0; index < before.Count; index++)
            {
                SubtitleCue left = before[index];
                SubtitleCue right = current[index];
                if (left.Start != right.Start || left.End != right.End || left.Text != right.Text)
                {
                    return false;
                }

                if (!string.Equals(left.StyleProfileId, right.StyleProfileId, StringComparison.Ordinal)
                    || !string.Equals(left.IdleProfileId, right.IdleProfileId, StringComparison.Ordinal)
                    || !string.Equals(left.PopInProfileId, right.PopInProfileId, StringComparison.Ordinal)
                    || !SameOptionalNumber(left.PositionX, right.PositionX)
                    || !SameOptionalNumber(left.PositionY, right.PositionY)
                    || !SameOptionalNumber(left.Scale, right.Scale))
                {
                    return false;
                }
            }

            return true;
        }

        private void RestoreCueSnapshot(List<SubtitleCue> snapshot)
        {
            cues.Clear();
            foreach (SubtitleCue cue in snapshot)
            {
                cues.Add(cue.Clone());
            }

            CuePostProcessor.Renumber(cues);
            if (cues.Count > 0)
            {
                CueList.SelectedIndex = 0;
            }

            RefreshCueEditor();
            UpdateOverlay(CurrentPosition);
            DrawTimeline();
        }

        private void PushCueUndoIfChanged(string description, List<SubtitleCue> before)
        {
            if (before == null || CueSnapshotsMatch(before, cues))
            {
                return;
            }

            List<SubtitleCue>? after = null;
            UndoStackPush(
                description,
                delegate
                {
                    after = CaptureCueSnapshot();
                    RestoreCueSnapshot(before);
                },
                delegate
                {
                    if (after != null)
                    {
                        RestoreCueSnapshot(after);
                    }
                });
        }

        private void SplitCueClick(object sender, RoutedEventArgs e)
        {
            List<SubtitleCue> before = CaptureCueSnapshot();
            SplitCueClickCore(sender, e);
            PushCueUndoIfChanged("split cue", before);
        }

        private void MergeCueClick(object sender, RoutedEventArgs e)
        {
            List<SubtitleCue> before = CaptureCueSnapshot();
            MergeCueClickCore(sender, e);
            PushCueUndoIfChanged("merge cues", before);
        }

        private void AddCueClick(object sender, RoutedEventArgs e)
        {
            List<SubtitleCue> before = CaptureCueSnapshot();
            AddCueClickCore(sender, e);
            PushCueUndoIfChanged("add cue", before);
        }

        private void DeleteCueClick(object sender, RoutedEventArgs e)
        {
            List<SubtitleCue> before = CaptureCueSnapshot();
            DeleteCueClickCore(sender, e);
            PushCueUndoIfChanged("delete cue", before);
        }

        private void NudgeBackClick(object sender, RoutedEventArgs e)
        {
            List<SubtitleCue> before = CaptureCueSnapshot();
            NudgeBackClickCore(sender, e);
            PushCueUndoIfChanged("nudge cue", before);
        }

        private void NudgeForwardClick(object sender, RoutedEventArgs e)
        {
            List<SubtitleCue> before = CaptureCueSnapshot();
            NudgeForwardClickCore(sender, e);
            PushCueUndoIfChanged("nudge cue", before);
        }

        private void SetStartClick(object sender, RoutedEventArgs e)
        {
            List<SubtitleCue> before = CaptureCueSnapshot();
            SetStartClickCore(sender, e);
            PushCueUndoIfChanged("set cue start", before);
        }

        private void SetEndClick(object sender, RoutedEventArgs e)
        {
            List<SubtitleCue> before = CaptureCueSnapshot();
            SetEndClickCore(sender, e);
            PushCueUndoIfChanged("set cue end", before);
        }

        private void StartEditorLostFocus(object sender, RoutedEventArgs e)
        {
            StartEditorLostFocusCore(sender, e);
        }

        private void EndEditorLostFocus(object sender, RoutedEventArgs e)
        {
            EndEditorLostFocusCore(sender, e);
        }

        private void CueTextEditorGotFocus(object sender, RoutedEventArgs e)
        {
            CueTextEditorGotFocusCore(sender, e);
        }

        private void CueTextEditorTextChanged(object sender, TextChangedEventArgs e)
        {
            if (isUpdatingCueEditor)
            {
                return;
            }

            if (pendingCueTextSnapshot == null)
            {
                pendingCueTextSnapshot = CaptureCueSnapshot();
            }

            CueTextEditorTextChangedCore(sender, e);
        }

        private void CueTextEditorLostFocus(object sender, RoutedEventArgs e)
        {
            List<SubtitleCue>? before = pendingCueTextSnapshot;
            pendingCueTextSnapshot = null;
            if (before == null)
            {
                return;
            }

            PushCueUndoIfChanged("edit cue text", before);
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
                if (control && e.Key == Key.S)
                {
                    SaveProject(shift);
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

            if (control && shift && e.Key == Key.J)
            {
                JoinSelectedSegments();
                e.Handled = true;
                return;
            }

            if (control && e.Key == Key.S)
            {
                SaveProject(shift);
                e.Handled = true;
                return;
            }

            if (control && !shift && e.Key == Key.Z)
            {
                UndoLastAction();
                e.Handled = true;
                return;
            }

            if (control && (e.Key == Key.Y || (shift && e.Key == Key.Z)))
            {
                RedoLastAction();
                e.Handled = true;
                return;
            }

            if (control && e.Key == Key.Q)
            {
                AddCueClick(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (control && !shift && e.Key == Key.R)
            {
                RecordClick(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (control && !shift && e.Key == Key.T)
            {
                AddAudioTrackClick(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (control && e.Key == Key.OemOpenBrackets)
            {
                SetStartClick(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (control && e.Key == Key.OemCloseBrackets)
            {
                SetEndClick(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (control && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
            {
                NudgeSelectedAndPlayhead(TimeSpan.FromMilliseconds(-100));
                e.Handled = true;
                return;
            }

            if (control && (e.Key == Key.OemPlus || e.Key == Key.Add))
            {
                NudgeSelectedAndPlayhead(TimeSpan.FromMilliseconds(100));
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

            public double DurationSeconds;

            public double SourceOffsetSeconds;

            public short[] Samples = Array.Empty<short>();

            public float[] Peaks = Array.Empty<float>();

            public bool SharesSamples;

            public bool HasRecordedAudio;

            public string? ClipFile;

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

                if (answer != MessageBoxResult.Yes)
                {
                    // Declined the download: leave the option off instead of pretending.
                    suppressCudaCheck = true;
                    GpuCheckBox.IsChecked = false;
                    suppressCudaCheck = false;
                    App.Settings.UseGpu = false;
                    SetStatus("GPU transcription stays off until the CUDA 12 toolkit is installed.");
                }
                else
                {
                    SetStatus("Install the CUDA 12 toolkit, then GPU transcription becomes available.");
                }
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

            ExportButton.IsEnabled = !isBusy && (hasCues || hasVideo);

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
            RedoTrackButton.IsEnabled = !isBusy && undoStack.CanRedo;
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
            if (isRecording)
            {
                StopRecording();
            }

            StopTimelinePlayback();

            if (projectDirty && tracks.Count > 0 && !isBusy)
            {
                MessageBoxResult answer = MessageBox.Show(
                    this,
                    "Save the project before closing?",
                    "SubtitleForge",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (answer == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (answer == MessageBoxResult.Yes && !SaveProject(false))
                {
                    // Saving failed or was cancelled in the file dialog: keep the app open so
                    // nothing is lost silently.
                    e.Cancel = true;
                    return;
                }
            }

            DisposeAudioPlayers();
            pendingProxyJobs.Clear();

            if (proxyCancellation != null)
            {
                proxyCancellation.Cancel();
            }

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
