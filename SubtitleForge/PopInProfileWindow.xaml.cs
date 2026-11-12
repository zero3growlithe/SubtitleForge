using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SubtitleForge.Models;
using SubtitleForge.Services;

namespace SubtitleForge
{
    /// <summary>Creates and edits the entrance (pop-in) animation profiles.</summary>
    public partial class PopInProfileWindow : Window
    {
        private const double PreviewLoopSeconds = 2.2;

        private readonly ProfileStore store;
        private readonly DispatcherTimer previewTimer;
        private readonly System.Diagnostics.Stopwatch previewClock = System.Diagnostics.Stopwatch.StartNew();

        private PopInProfile draft = new PopInProfile();
        private bool suppress;

        public PopInProfileWindow(ProfileStore profileStore, string? startProfileId, string? defaultProfileId)
        {
            InitializeComponent();
            store = profileStore;
            DefaultProfileId = defaultProfileId;

            ReloadProfileList(startProfileId);

            PreviewOverlay.PositionX = 0.5;
            PreviewOverlay.PositionY = 0.58;
            PreviewOverlay.CueStartSeconds = 0.0;
            PreviewOverlay.CueEndSeconds = 1000.0;
            PreviewOverlay.CueText = "Pop-in preview";

            previewTimer = new DispatcherTimer(DispatcherPriority.Render);
            previewTimer.Interval = TimeSpan.FromMilliseconds(16);
            previewTimer.Tick += OnPreviewTick;
            previewTimer.Start();

            UpdatePreview();
        }

        private StyleProfile? previewStyle;
        private IdleAnimationProfile? previewIdle;

        /// <summary>Style used for the preview (the current default style).</summary>
        public StyleProfile? PreviewStyle
        {
            get { return previewStyle; }
            set
            {
                previewStyle = value;
                UpdatePreview();
            }
        }

        /// <summary>Idle animation used for the preview (the current default).</summary>
        public IdleAnimationProfile? PreviewIdle
        {
            get { return previewIdle; }
            set
            {
                previewIdle = value;
                UpdatePreview();
            }
        }

        public string? DefaultProfileId { get; private set; }

        public bool DefaultChanged { get; private set; }

        private void OnPreviewTick(object? sender, EventArgs e)
        {
            double seconds = previewClock.Elapsed.TotalSeconds;
            PreviewOverlay.IdleSeconds = seconds;
            PreviewOverlay.PlayheadSeconds = seconds % PreviewLoopSeconds;
            PreviewOverlay.Refresh();
        }

        private void ReloadProfileList(string? selectId)
        {
            suppress = true;
            try
            {
                ProfileCombo.Items.Clear();
                foreach (PopInProfile profile in store.PopIns)
                {
                    ProfileCombo.Items.Add(new ProfileListItem(profile.Id, profile.Name));
                }

                if (ProfileCombo.Items.Count == 0)
                {
                    return;
                }

                int index = 0;
                for (int itemIndex = 0; itemIndex < ProfileCombo.Items.Count; itemIndex++)
                {
                    ProfileListItem? item = ProfileCombo.Items[itemIndex] as ProfileListItem;
                    if (item != null && string.Equals(item.Id, selectId, StringComparison.Ordinal))
                    {
                        index = itemIndex;
                        break;
                    }
                }

                ProfileCombo.SelectedIndex = index;
            }
            finally
            {
                suppress = false;
            }

            LoadDraftFromStore();
        }

        private void LoadDraftFromStore()
        {
            ProfileListItem? item = ProfileCombo.SelectedItem as ProfileListItem;
            if (item == null)
            {
                return;
            }

            draft = store.ResolvePopIn(item.Id).Clone();
            LoadDraftIntoUi();
        }

        private void LoadDraftIntoUi()
        {
            suppress = true;
            try
            {
                NameBox.Text = draft.Name;
                SelectTag(KindCombo, draft.Kind);
                SelectTag(DirectionCombo, draft.Direction);
                DurationBox.Text = draft.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
                PerCharacterCheck.IsChecked = draft.PerCharacter;
                CharacterDelayBox.Text = draft.CharacterDelaySeconds.ToString("0.###", CultureInfo.InvariantCulture);
                DistanceBox.Text = draft.DistanceMultiplier.ToString("0.###", CultureInfo.InvariantCulture);
                StiffnessBox.Text = draft.SpringStiffness.ToString("0.##", CultureInfo.InvariantCulture);
                DampingBox.Text = draft.SpringDamping.ToString("0.##", CultureInfo.InvariantCulture);
                JellyXBox.Text = draft.JellyAmplitudeX.ToString("0.###", CultureInfo.InvariantCulture);
                JellyYBox.Text = draft.JellyAmplitudeY.ToString("0.###", CultureInfo.InvariantCulture);
                JellyFrequencyBox.Text = draft.JellyFrequency.ToString("0.##", CultureInfo.InvariantCulture);
                JellyFalloffBox.Text = draft.JellyFalloff.ToString("0.##", CultureInfo.InvariantCulture);
            }
            finally
            {
                suppress = false;
            }

            UpdatePreview();
        }

        private static void SelectTag(ComboBox combo, string? value)
        {
            string wanted = string.IsNullOrEmpty(value) ? string.Empty : value;
            for (int index = 0; index < combo.Items.Count; index++)
            {
                ComboBoxItem? item = combo.Items[index] as ComboBoxItem;
                if (item != null && string.Equals(item.Tag as string, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = index;
                    return;
                }
            }

            combo.SelectedIndex = 0;
        }

        private static string SelectedTag(ComboBox combo, string fallback)
        {
            ComboBoxItem? item = combo.SelectedItem as ComboBoxItem;
            string? tag = item == null ? null : item.Tag as string;
            return string.IsNullOrEmpty(tag) ? fallback : tag;
        }

        private void FieldChanged(object sender, RoutedEventArgs e)
        {
            if (suppress)
            {
                return;
            }

            draft.Name = NameBox.Text;
            draft.Kind = SelectedTag(KindCombo, PopInKind.FadeIn);
            draft.Direction = SelectedTag(DirectionCombo, PopInDirection.Bottom);
            draft.DurationSeconds = ParseDouble(DurationBox.Text, draft.DurationSeconds, 0.02, 5.0);
            draft.PerCharacter = PerCharacterCheck.IsChecked == true;
            draft.CharacterDelaySeconds = ParseDouble(CharacterDelayBox.Text, draft.CharacterDelaySeconds, 0.0, 1.0);
            draft.DistanceMultiplier = ParseDouble(DistanceBox.Text, draft.DistanceMultiplier, -6.0, 6.0);
            draft.SpringStiffness = ParseDouble(StiffnessBox.Text, draft.SpringStiffness, 0.5, 60.0);
            draft.SpringDamping = ParseDouble(DampingBox.Text, draft.SpringDamping, 0.05, 3.0);
            draft.JellyAmplitudeX = ParseDouble(JellyXBox.Text, draft.JellyAmplitudeX, 0.0, 3.0);
            draft.JellyAmplitudeY = ParseDouble(JellyYBox.Text, draft.JellyAmplitudeY, 0.0, 3.0);
            draft.JellyFrequency = ParseDouble(JellyFrequencyBox.Text, draft.JellyFrequency, 0.0, 20.0);
            draft.JellyFalloff = ParseDouble(JellyFalloffBox.Text, draft.JellyFalloff, 0.0, 20.0);

            UpdatePreview();
        }

        private void UpdatePreview()
        {
            PreviewOverlay.TextStyle = PreviewStyle;
            PreviewOverlay.Idle = PreviewIdle;
            PreviewOverlay.PopIn = draft;
            PreviewOverlay.InvalidateLayout();
        }

        private void ProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppress)
            {
                return;
            }

            LoadDraftFromStore();
            UpdateDefaultButton();
        }

        private void NewProfileClick(object sender, RoutedEventArgs e)
        {
            PopInProfile created = PopInProfile.CreateSoftFade();
            created.Id = Guid.NewGuid().ToString("N");
            created.Name = "Pop-in " + (store.PopIns.Count + 1).ToString(CultureInfo.InvariantCulture);
            store.UpsertPopIn(created);
            store.Save();
            ReloadProfileList(created.Id);
            UpdateDefaultButton();
        }

        private void DeleteProfileClick(object sender, RoutedEventArgs e)
        {
            ProfileListItem? item = ProfileCombo.SelectedItem as ProfileListItem;
            if (item == null || store.PopIns.Count <= 1)
            {
                MessageBox.Show(
                    this,
                    "At least one pop-in profile has to stay.",
                    "SubtitleForge",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            PopInProfile? target = null;
            foreach (PopInProfile profile in store.PopIns)
            {
                if (string.Equals(profile.Id, item.Id, StringComparison.Ordinal))
                {
                    target = profile;
                    break;
                }
            }

            if (target == null)
            {
                return;
            }

            store.PopIns.Remove(target);
            store.Save();
            ReloadProfileList(null);
            UpdateDefaultButton();
        }

        private void SetDefaultClick(object sender, RoutedEventArgs e)
        {
            ProfileListItem? item = ProfileCombo.SelectedItem as ProfileListItem;
            if (item == null)
            {
                return;
            }

            DefaultProfileId = item.Id;
            DefaultChanged = true;
            UpdateDefaultButton();
        }

        private void UpdateDefaultButton()
        {
            ProfileListItem? item = ProfileCombo.SelectedItem as ProfileListItem;
            bool isDefault = item != null && string.Equals(item.Id, DefaultProfileId, StringComparison.Ordinal);
            DefaultButton.Content = isDefault ? "Default ✓" : "Set as default";
        }

        private void SaveClick(object sender, RoutedEventArgs e)
        {
            FieldChanged(sender, e);
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                draft.Name = "Pop-in";
            }

            store.UpsertPopIn(draft.Clone());
            store.Save();
            DialogResult = true;
            Close();
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static double ParseDouble(string? text, double fallback, double minimum, double maximum)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            double value;
            if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return fallback;
            }

            if (value < minimum)
            {
                value = minimum;
            }

            if (value > maximum)
            {
                value = maximum;
            }

            return value;
        }
    }
}
