using System;
using System.Collections.Generic;
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
    /// <summary>Creates and edits idle animation profiles (shake, sine drift, pulse, wiggle, spin).</summary>
    public partial class IdleProfileWindow : Window
    {
        private const string DefaultPreviewText = "Sample Text";

        private readonly ProfileStore store;
        private readonly Dictionary<string, IdleChannelEditor> editors = new Dictionary<string, IdleChannelEditor>();
        private readonly DispatcherTimer previewTimer;
        private readonly System.Diagnostics.Stopwatch previewClock = System.Diagnostics.Stopwatch.StartNew();

        private IdleAnimationProfile draft = new IdleAnimationProfile();

        /// <summary>Font size used by the preview only, never written into the profile.</summary>
        private double previewFontSize = 190.0;
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
            PreviewOverlay.CueText = DefaultPreviewText;
            PreviewOverlay.PlayheadSeconds = 5.0;
            PreviewSizeBox.SetValueSilently(previewFontSize);

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

            // Picking another profile commits the edits made to the one that was showing.
            CommitDraftToStore();
            LoadDraftFromStore();
            UpdateDefaultButton();
        }

        /// <summary>Writes every field back to the store so switching profiles never loses an edit.</summary>
        private void CommitDraftToStore()
        {
            draft.Name = NameBox.Text;
            draft.PerCharacter = PerCharacterCheck.IsChecked == true;
            draft.CharacterStaggerSeconds = StaggerBox.Value;
            draft.StaggerGrouping = SelectedGrouping();
            draft.Shake = editors["Shake"].Channel ?? draft.Shake;
            draft.SineX = editors["SineX"].Channel ?? draft.SineX;
            draft.SineY = editors["SineY"].Channel ?? draft.SineY;
            draft.Pulse = editors["Pulse"].Channel ?? draft.Pulse;
            draft.Wiggle = editors["Wiggle"].Channel ?? draft.Wiggle;
            draft.Spin = editors["Spin"].Channel ?? draft.Spin;
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                draft.Name = "Idle";
            }

            store.UpsertIdle(draft.Clone());
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

            foreach (IdleAnimationProfile profile in store.Idle)
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
            if (!store.CopyIdleSettings(draft.Id, targetId))
            {
                return;
            }

            ReloadProfileList(draft.Id);
            UpdateDefaultButton();

            MessageBox.Show(
                "The settings were copied to \"" + store.ResolveIdle(targetId).Name + "\".",
                "Copy settings",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void DuplicateProfileClick(object sender, RoutedEventArgs e)
        {
            CommitDraftToStore();

            IdleAnimationProfile copy = draft.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = draft.Name + " copy";
            store.UpsertIdle(copy);
            store.Save();
            ReloadProfileList(copy.Id);
            UpdateDefaultButton();
        }

        private void NewProfileClick(object sender, RoutedEventArgs e)
        {
            CommitDraftToStore();

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
            CommitDraftToStore();

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
