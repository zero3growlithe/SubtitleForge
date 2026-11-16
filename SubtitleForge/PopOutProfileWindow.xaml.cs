using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SubtitleForge.Controls;
using SubtitleForge.Models;
using SubtitleForge.Services;

namespace SubtitleForge
{
    /// <summary>Creates and edits the exit (pop-out) animation profiles.</summary>
    public partial class PopOutProfileWindow : Window
    {
        private const string DefaultPreviewText = "Sample Text";

        /// <summary>Length of one preview loop. The cue ends before the loop does, so the exit plays.</summary>
        private const double PreviewLoopSeconds = 2.6;

        /// <summary>Where the preview cue ends: the exit is anchored there, so it plays before the loop wraps.</summary>
        private const double PreviewCueEndSeconds = 2.0;

        private readonly ProfileStore store;
        private readonly DispatcherTimer previewTimer;
        private readonly System.Diagnostics.Stopwatch previewClock = System.Diagnostics.Stopwatch.StartNew();

        private PopOutProfile draft = new PopOutProfile();

        /// <summary>Font size used by the preview only, never written into the profile.</summary>
        private double previewFontSize = 190.0;
        private bool suppress;

        public PopOutProfileWindow(ProfileStore profileStore, string? startProfileId, string? defaultProfileId)
        {
            InitializeComponent();
            store = profileStore;
            DefaultProfileId = defaultProfileId;

            ReloadProfileList(startProfileId);

            PreviewOverlay.PositionX = 0.5;
            PreviewOverlay.PositionY = 0.56;
            PreviewOverlay.CueStartSeconds = 0.0;
            PreviewOverlay.CueEndSeconds = PreviewCueEndSeconds;
            PreviewOverlay.CueText = DefaultPreviewText;
            PreviewSizeBox.SetValueSilently(previewFontSize);

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
                foreach (PopOutProfile profile in store.PopOuts)
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

            draft = store.ResolvePopOut(item.Id).Clone();
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
                SelectTag(SpinAxisCombo, draft.SpinAxis);
                SelectTag(OriginCombo, draft.Origin);
                SelectTag(StaggerGroupingCombo, draft.StaggerGrouping);
                DurationBox.SetValueSilently(draft.DurationSeconds);
                EaseBox.SetValueSilently(draft.EasePower);
                PerCharacterCheck.IsChecked = draft.PerCharacter;
                CharacterDelayBox.SetValueSilently(draft.CharacterDelaySeconds);
                DistanceBox.SetValueSilently(draft.DistanceMultiplier);
                EndScaleBox.SetValueSilently(draft.EndScale);
                JellyXBox.SetValueSilently(draft.JellyAmplitudeX);
                JellyYBox.SetValueSilently(draft.JellyAmplitudeY);
                JellyFrequencyBox.SetValueSilently(draft.JellyFrequency);
                JellyFalloffBox.SetValueSilently(draft.JellyFalloff);
                SpinTurnsBox.SetValueSilently(draft.SpinTurns);
                SpinEndScaleBox.SetValueSilently(draft.SpinEndScale);
                CellCountBox.SetValueSilently(draft.CellCount);
                NoiseBox.SetValueSilently(draft.NoiseAmount);
                SeedBox.SetValueSilently(draft.Seed);
                ParticleCountBox.SetValueSilently(draft.ParticleCount);
                ParticleSizeBox.SetValueSilently(draft.ParticleSize);
                WindAngleBox.SetValueSilently(draft.WindAngleDegrees);
                WindForceBox.SetValueSilently(draft.WindForce);
                WindSpreadBox.SetValueSilently(draft.WindSpread);
                ParticleSpinBox.SetValueSilently(draft.ParticleSpinDegrees);
                CharacterDelayBox.IsEnabled = draft.PerCharacter;
                StaggerGroupingCombo.IsEnabled = draft.PerCharacter;
            }
            finally
            {
                suppress = false;
            }

            UpdateVisibleSections();
            UpdatePreview();
        }

        /// <summary>Only the settings of the chosen kind are on screen, so a dust profile is not buried in spin fields.</summary>
        private void UpdateVisibleSections()
        {
            string kind = SelectedTag(KindCombo, PopOutKind.FadeOut);
            SlidePanel.Visibility = Is(kind, PopOutKind.SlideOut) ? Visibility.Visible : Visibility.Collapsed;
            ScalePanel.Visibility = Is(kind, PopOutKind.ScaleOut) ? Visibility.Visible : Visibility.Collapsed;
            SpinPanel.Visibility = Is(kind, PopOutKind.SpinOut) ? Visibility.Visible : Visibility.Collapsed;
            bool breaks = Is(kind, PopOutKind.Dissolve) || Is(kind, PopOutKind.Dust);
            BreakUpPanel.Visibility = breaks ? Visibility.Visible : Visibility.Collapsed;
            DustPanel.Visibility = Is(kind, PopOutKind.Dust) ? Visibility.Visible : Visibility.Collapsed;
        }

        private static bool Is(string kind, string expected)
        {
            return string.Equals(kind, expected, StringComparison.OrdinalIgnoreCase);
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

        private void FieldChanged(object? sender, EventArgs e)
        {
            if (suppress)
            {
                return;
            }

            ReadDraftFromUi();
            UpdateVisibleSections();
            UpdatePreview();
        }

        /// <summary>Every field in one place: the live edit and the commit path stay in step.</summary>
        private void ReadDraftFromUi()
        {
            draft.Name = NameBox.Text;
            draft.Kind = SelectedTag(KindCombo, PopOutKind.FadeOut);
            draft.Direction = SelectedTag(DirectionCombo, PopInDirection.Bottom);
            draft.SpinAxis = SelectedTag(SpinAxisCombo, PopOutAxis.Y);
            draft.Origin = SelectedTag(OriginCombo, PopOutOrigin.LeftToRight);
            draft.DurationSeconds = DurationBox.Value;
            draft.EasePower = EaseBox.Value;
            draft.PerCharacter = PerCharacterCheck.IsChecked == true;
            draft.StaggerGrouping = SelectedTag(StaggerGroupingCombo, StaggerGroupings.Character);
            draft.CharacterDelaySeconds = CharacterDelayBox.Value;
            draft.DistanceMultiplier = DistanceBox.Value;
            draft.EndScale = EndScaleBox.Value;
            draft.JellyAmplitudeX = JellyXBox.Value;
            draft.JellyAmplitudeY = JellyYBox.Value;
            draft.JellyFrequency = JellyFrequencyBox.Value;
            draft.JellyFalloff = JellyFalloffBox.Value;
            draft.SpinTurns = SpinTurnsBox.Value;
            draft.SpinEndScale = SpinEndScaleBox.Value;
            draft.CellCount = (int)Math.Round(CellCountBox.Value);
            draft.NoiseAmount = NoiseBox.Value;
            draft.Seed = (int)Math.Round(SeedBox.Value);
            draft.ParticleCount = (int)Math.Round(ParticleCountBox.Value);
            draft.ParticleSize = ParticleSizeBox.Value;
            draft.WindAngleDegrees = WindAngleBox.Value;
            draft.WindForce = WindForceBox.Value;
            draft.WindSpread = WindSpreadBox.Value;
            draft.ParticleSpinDegrees = ParticleSpinBox.Value;

            CharacterDelayBox.IsEnabled = draft.PerCharacter;
            StaggerGroupingCombo.IsEnabled = draft.PerCharacter;
        }

        private void PreviewSizeChanged(object? sender, EventArgs e)
        {
            previewFontSize = PreviewSizeBox.Value;
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            // Scale rather than replace the font size: the overlay multiplies the font *and* every
            // effect width by it, so the preview keeps the exact proportions of the finished video.
            StyleProfile preview = PreviewStyle ?? ProfileStore.CreateDefaultStyle();
            double reference = preview.FontSize > 0.5 ? preview.FontSize : 64.0;
            PreviewOverlay.Scale = previewFontSize / reference;
            PreviewOverlay.TextStyle = preview;
            PreviewOverlay.Idle = PreviewIdle;

            // Only the exit is on show here; the entrance has its own window.
            PreviewOverlay.PopIn = null;
            PreviewOverlay.PopOut = draft;
            PreviewOverlay.InvalidateLayout();
        }

        private void ProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppress)
            {
                return;
            }

            // Picking another profile commits the edits made to the one that was showing.
            CommitDraftToStore();
            LoadDraftFromStore();
            UpdateDefaultButton();
        }

        /// <summary>Writes every field back to the store so switching profiles never loses an edit.</summary>
        private void CommitDraftToStore()
        {
            ReadDraftFromUi();
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                draft.Name = "Pop-out";
            }

            store.UpsertPopOut(draft.Clone());
            store.Save();
            RefreshProfileItemName();
        }

        /// <summary>
        /// Updates the name shown in the profile combo. It looks the entry up by id, never by the
        /// current selection: when switching profiles the selection already points at the next
        /// profile while <see cref="draft"/> still belongs to the one being left.
        /// </summary>
        private void RefreshProfileItemName()
        {
            for (int index = 0; index < ProfileCombo.Items.Count; index++)
            {
                ProfileListItem? item = ProfileCombo.Items[index] as ProfileListItem;
                if (item == null || !string.Equals(item.Id, draft.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(item.Name, draft.Name, StringComparison.Ordinal))
                {
                    return;
                }

                suppress = true;
                try
                {
                    int selected = ProfileCombo.SelectedIndex;
                    ProfileCombo.Items[index] = new ProfileListItem(item.Id, draft.Name);
                    ProfileCombo.SelectedIndex = selected;
                }
                finally
                {
                    suppress = false;
                }

                return;
            }
        }

        /// <summary>
        /// Offers the other profiles of this kind so the current settings can be pushed into one of
        /// them, which is how a variant is started from an existing look.
        /// </summary>
        private void CopyToProfileClick(object sender, RoutedEventArgs e)
        {
            CommitDraftToStore();

            ContextMenu menu = new ContextMenu();
            UIElement? anchor = sender as UIElement;
            if (anchor != null)
            {
                menu.PlacementTarget = anchor;
            }

            foreach (PopOutProfile profile in store.PopOuts)
            {
                if (string.Equals(profile.Id, draft.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                MenuItem entry = new MenuItem();
                entry.Header = profile.Name;
                entry.Tag = profile.Id;
                entry.Click += CopySettingsToClick;
                menu.Items.Add(entry);
            }

            if (menu.Items.Count == 0)
            {
                MessageBox.Show(
                    "There is no other profile to copy these settings into yet.",
                    "Copy settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            menu.IsOpen = true;
        }

        private void CopySettingsToClick(object sender, RoutedEventArgs e)
        {
            MenuItem? entry = sender as MenuItem;
            if (entry == null || !(entry.Tag is string targetId))
            {
                return;
            }

            // The draft is the source, so it has to be in the store before it is copied out of it.
            CommitDraftToStore();
            if (!store.CopyPopOutSettings(draft.Id, targetId))
            {
                return;
            }

            ReloadProfileList(draft.Id);
            UpdateDefaultButton();

            MessageBox.Show(
                "The settings were copied to \"" + store.ResolvePopOut(targetId).Name + "\".",
                "Copy settings",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void DuplicateProfileClick(object sender, RoutedEventArgs e)
        {
            CommitDraftToStore();

            PopOutProfile copy = draft.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = draft.Name + " copy";
            store.UpsertPopOut(copy);
            store.Save();
            ReloadProfileList(copy.Id);
            UpdateDefaultButton();
        }

        private void NewProfileClick(object sender, RoutedEventArgs e)
        {
            CommitDraftToStore();

            PopOutProfile created = PopOutProfile.CreateFade();
            created.Id = Guid.NewGuid().ToString("N");
            created.Name = "Pop-out " + (store.PopOuts.Count + 1).ToString(CultureInfo.InvariantCulture);
            store.UpsertPopOut(created);
            store.Save();
            ReloadProfileList(created.Id);
            UpdateDefaultButton();
        }

        private void DeleteProfileClick(object sender, RoutedEventArgs e)
        {
            CommitDraftToStore();

            ProfileListItem? item = ProfileCombo.SelectedItem as ProfileListItem;
            if (item == null || store.PopOuts.Count <= 1)
            {
                MessageBox.Show(
                    this,
                    "At least one pop-out profile has to stay.",
                    "SubtitleForge",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            PopOutProfile? target = null;
            foreach (PopOutProfile profile in store.PopOuts)
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

            store.PopOuts.Remove(target);
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
            CommitDraftToStore();
            DialogResult = true;
            Close();
        }

        /// <summary>
        /// Enter while a field is being edited submits that field: the value is validated, written
        /// to the draft and the field gives up focus. Without this Enter fell through to the window's
        /// default Save button and saved the whole profile.
        /// </summary>
        private void FieldPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return)
            {
                return;
            }

            if (Keyboard.FocusedElement is not TextBox box)
            {
                return;
            }

            NumericDragBox? numeric = box as NumericDragBox;
            if (numeric != null)
            {
                numeric.Commit();
            }

            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// Live preview text. It only affects the preview; the profile keeps no text of its own.
        /// </summary>
        private void PreviewTextChanged(object sender, TextChangedEventArgs e)
        {
            if (PreviewOverlay == null || PreviewTextBox == null)
            {
                // TextChanged also fires while the window is still being built.
                return;
            }

            string text = PreviewTextBox.Text ?? string.Empty;
            PreviewOverlay.CueText = text.Trim().Length == 0 ? DefaultPreviewText : text;
        }
    }
}
