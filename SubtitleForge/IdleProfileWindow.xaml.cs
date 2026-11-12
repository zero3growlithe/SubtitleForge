using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SubtitleForge.Controls;
using SubtitleForge.Models;
using SubtitleForge.Services;

namespace SubtitleForge
{
    /// <summary>Creates and edits idle animation profiles (shake, sine drift, pulse, wiggle, spin).</summary>
    public partial class IdleProfileWindow : Window
    {
        private readonly ProfileStore store;
        private readonly Dictionary<string, IdleChannelEditor> editors = new Dictionary<string, IdleChannelEditor>();
        private readonly DispatcherTimer previewTimer;
        private readonly System.Diagnostics.Stopwatch previewClock = System.Diagnostics.Stopwatch.StartNew();

        private IdleAnimationProfile draft = new IdleAnimationProfile();
        private bool suppress;

        public IdleProfileWindow(ProfileStore profileStore, string? startProfileId, string? defaultProfileId)
        {
            InitializeComponent();
            store = profileStore;
            DefaultProfileId = defaultProfileId;

            BuildChannelEditors();
            ReloadProfileList(startProfileId);

            PreviewOverlay.TextStyle = null;
            PreviewOverlay.PositionX = 0.5;
            PreviewOverlay.PositionY = 0.55;
            PreviewOverlay.CueStartSeconds = 0.0;
            PreviewOverlay.CueEndSeconds = 1000.0;
            PreviewOverlay.CueText = "Sample Text";
            PreviewOverlay.PlayheadSeconds = 5.0;

            previewTimer = new DispatcherTimer(DispatcherPriority.Render);
            previewTimer.Interval = TimeSpan.FromMilliseconds(16);
            previewTimer.Tick += OnPreviewTick;
            previewTimer.Start();

            UpdatePreview();
        }

        private StyleProfile? previewStyle;

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

        public string? DefaultProfileId { get; private set; }

        public bool DefaultChanged { get; private set; }

        private void BuildChannelEditors()
        {
            AddEditor("Shake", "Shake", "Jitter size, as a multiple of the font size");
            AddEditor("SineX", "Sine X", "Horizontal drift, as a multiple of the font size");
            AddEditor("SineY", "Sine Y", "Vertical drift, as a multiple of the font size");
            AddEditor("Pulse", "Pulse", "Scale delta, 0.1 = ±10%");
            AddEditor("Wiggle", "Wiggle", "Rotation in degrees");
            AddEditor("Spin", "Spin (Y axis)", "Rotations per second; the width is squashed to fake the turn");
        }

        private void AddEditor(string key, string title, string hint)
        {
            IdleChannelEditor editor = new IdleChannelEditor(title, hint);
            editor.Changed += OnChannelChanged;
            editors[key] = editor;
            ChannelPanel.Children.Add(editor);
        }

        private void OnChannelChanged(object? sender, EventArgs e)
        {
            UpdatePreview();
        }

        private void OnPreviewTick(object? sender, EventArgs e)
        {
            PreviewOverlay.IdleSeconds = previewClock.Elapsed.TotalSeconds;
            PreviewOverlay.Refresh();
        }

        private void ReloadProfileList(string? selectId)
        {
            suppress = true;
            try
            {
                ProfileCombo.Items.Clear();
                foreach (IdleAnimationProfile profile in store.Idle)
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

            draft = store.ResolveIdle(item.Id).Clone();
            LoadDraftIntoUi();
        }

        private void LoadDraftIntoUi()
        {
            suppress = true;
            try
            {
                NameBox.Text = draft.Name;
                PerCharacterCheck.IsChecked = draft.PerCharacter;
                StaggerBox.SetValueSilently(draft.CharacterStaggerSeconds);
                SelectGrouping(draft.StaggerGrouping);
                StaggerBox.IsEnabled = draft.PerCharacter;
                StaggerGroupingCombo.IsEnabled = draft.PerCharacter;

                editors["Shake"].Load(draft.Shake);
                editors["SineX"].Load(draft.SineX);
                editors["SineY"].Load(draft.SineY);
                editors["Pulse"].Load(draft.Pulse);
                editors["Wiggle"].Load(draft.Wiggle);
                editors["Spin"].Load(draft.Spin);
            }
            finally
            {
                suppress = false;
            }

            UpdatePreview();
        }

        private void BasicsChanged(object? sender, EventArgs e)
        {
            if (suppress)
            {
                return;
            }

            draft.Name = NameBox.Text;
            draft.PerCharacter = PerCharacterCheck.IsChecked == true;
            draft.CharacterStaggerSeconds = StaggerBox.Value;
            draft.StaggerGrouping = SelectedGrouping();
            StaggerBox.IsEnabled = draft.PerCharacter;
            StaggerGroupingCombo.IsEnabled = draft.PerCharacter;

            UpdatePreview();
        }

        private void SelectGrouping(string? grouping)
        {
            string wanted = string.IsNullOrEmpty(grouping) ? StaggerGroupings.Character : grouping;
            for (int index = 0; index < StaggerGroupingCombo.Items.Count; index++)
            {
                ComboBoxItem? item = StaggerGroupingCombo.Items[index] as ComboBoxItem;
                if (item != null && string.Equals(item.Tag as string, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    StaggerGroupingCombo.SelectedIndex = index;
                    return;
                }
            }

            StaggerGroupingCombo.SelectedIndex = 0;
        }

        private string SelectedGrouping()
        {
            ComboBoxItem? item = StaggerGroupingCombo.SelectedItem as ComboBoxItem;
            string? tag = item == null ? null : item.Tag as string;
            return string.IsNullOrEmpty(tag) ? StaggerGroupings.Character : tag;
        }

        private void UpdatePreview()
        {
            PreviewOverlay.TextStyle = PreviewStyle ?? ProfileStore.CreateDefaultStyle();
            PreviewOverlay.Idle = draft;
            PreviewOverlay.PopIn = null;
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
            IdleAnimationProfile created = IdleAnimationProfile.CreateStill();
            created.Id = Guid.NewGuid().ToString("N");
            created.Name = "Idle " + (store.Idle.Count + 1).ToString(CultureInfo.InvariantCulture);
            store.UpsertIdle(created);
            store.Save();
            ReloadProfileList(created.Id);
            UpdateDefaultButton();
        }

        private void DeleteProfileClick(object sender, RoutedEventArgs e)
        {
            ProfileListItem? item = ProfileCombo.SelectedItem as ProfileListItem;
            if (item == null || store.Idle.Count <= 1)
            {
                MessageBox.Show(
                    this,
                    "At least one idle profile has to stay.",
                    "SubtitleForge",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            IdleAnimationProfile? target = null;
            foreach (IdleAnimationProfile profile in store.Idle)
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

            store.Idle.Remove(target);
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
            BasicsChanged(sender, e);
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                draft.Name = "Idle";
            }

            store.UpsertIdle(draft.Clone());
            store.Save();
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
