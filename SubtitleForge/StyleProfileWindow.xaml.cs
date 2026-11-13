using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SubtitleForge.Controls;
using SubtitleForge.Models;
using SubtitleForge.Services;

namespace SubtitleForge
{
    /// <summary>Creates and edits the text style profiles: font, fill, outlines and shadows.</summary>
    public partial class StyleProfileWindow : Window
    {
        private readonly ProfileStore store;
        private readonly DispatcherTimer previewTimer;
        private readonly System.Diagnostics.Stopwatch previewClock = System.Diagnostics.Stopwatch.StartNew();

        private const string SampleText = "Sample Text";

        private const string DefaultPreviewText = SampleText;

        private StyleProfile draft = new StyleProfile();
        private bool suppress;

        /// <summary>True once the user scrubbed the preview size, which stops the profile size from overriding it.</summary>
        private bool previewSizePinned;

        /// <summary>
        /// Font size used by the preview only. The profile keeps its own size, which is authored for
        /// a 1080-tall canvas and would render far too large in a 250 px preview.
        /// </summary>
        private double previewFontSize = 190.0;

        public StyleProfileWindow(ProfileStore profileStore, string? startProfileId, string? defaultProfileId)
        {
            InitializeComponent();
            store = profileStore;
            DefaultProfileId = defaultProfileId;

            LoadFontFamilies();
            ReloadProfileList(startProfileId);

            PreviewOverlay.PositionX = 0.5;
            PreviewOverlay.PositionY = 0.62;
            PreviewOverlay.Scale = 1.0;
            PreviewOverlay.CueStartSeconds = 0.0;
            PreviewOverlay.CueEndSeconds = 4.0;
            PreviewOverlay.CueText = SampleText;
            PreviewSizeBox.SetValueSilently(previewFontSize);

            previewTimer = new DispatcherTimer(DispatcherPriority.Render);
            previewTimer.Interval = TimeSpan.FromMilliseconds(16);
            previewTimer.Tick += OnPreviewTick;
            previewTimer.Start();

            UpdatePreview();
        }

        private IdleAnimationProfile? previewIdle;
        private PopInProfile? previewPopIn;

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

        /// <summary>Pop-in animation used for the preview (the current default).</summary>
        public PopInProfile? PreviewPopIn
        {
            get { return previewPopIn; }
            set
            {
                previewPopIn = value;
                UpdatePreview();
            }
        }

        public string? DefaultProfileId { get; private set; }

        public bool DefaultChanged { get; private set; }

        private void OnPreviewTick(object? sender, EventArgs e)
        {
            double seconds = previewClock.Elapsed.TotalSeconds;
            PreviewOverlay.IdleSeconds = seconds;
            // Loop the entrance so the pop-in can be judged.
            PreviewOverlay.PlayheadSeconds = seconds % 4.0;
            PreviewOverlay.Refresh();
        }

        private void LoadFontFamilies()
        {
            List<string> families = new List<string>();
            foreach (FontFamily family in Fonts.SystemFontFamilies)
            {
                families.Add(family.Source);
            }

            families.Sort(StringComparer.CurrentCultureIgnoreCase);
            foreach (string family in families)
            {
                FontCombo.Items.Add(family);
            }
        }

        private void ReloadProfileList(string? selectId)
        {
            suppress = true;
            try
            {
                ProfileCombo.Items.Clear();
                foreach (StyleProfile style in store.Styles)
                {
                    ProfileCombo.Items.Add(new ProfileListItem(style.Id, style.Name));
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

            draft = store.ResolveStyle(item.Id).Clone();
            LoadDraftIntoUi();
        }

        private void LoadDraftIntoUi()
        {
            suppress = true;
            try
            {
                NameBox.Text = draft.Name;

                bool fontFound = false;
                for (int index = 0; index < FontCombo.Items.Count; index++)
                {
                    string? value = FontCombo.Items[index] as string;
                    if (string.Equals(value, draft.FontFamily, StringComparison.OrdinalIgnoreCase))
                    {
                        FontCombo.SelectedIndex = index;
                        fontFound = true;
                        break;
                    }
                }

                if (!fontFound && FontCombo.Items.Count > 0)
                {
                    FontCombo.SelectedIndex = 0;
                }

                BoldCheck.IsChecked = draft.Bold;
                ItalicCheck.IsChecked = draft.Italic;
                FontSizeBox.SetValueSilently(draft.FontSize);
                CharacterSpacingBox.SetValueSilently(draft.CharacterSpacing);
                WordSpacingBox.SetValueSilently(draft.WordSpacing);
                WordsBox.SetValueSilently(draft.WordsPerDisplay);
                OpacityBox.SetValueSilently(draft.Opacity);
                HighlightCheck.IsChecked = draft.HighlightSpokenWord;

                FillPicker.Load(draft.Fill);
                HighlightPicker.Load(draft.HighlightFill);

                draft.Normalize();

                EffectList.Items.Clear();
                foreach (EffectLayer effect in draft.Effects)
                {
                    EffectList.Items.Add(effect);
                }

                if (EffectList.Items.Count > 0)
                {
                    EffectList.SelectedIndex = 0;
                }
            }
            finally
            {
                suppress = false;
            }

            LoadEffectFields();
            UpdatePreview();
        }

        // ── Effect stack editing ───────────────────────────────────────────

        private EffectLayer? SelectedEffect
        {
            get { return EffectList.SelectedItem as EffectLayer; }
        }

        private void EffectSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppress)
            {
                return;
            }

            LoadEffectFields();
        }

        private void LoadEffectFields()
        {
            EffectLayer? effect = SelectedEffect;
            suppress = true;
            try
            {
                bool enabled = effect != null;
                EffectNameBox.IsEnabled = enabled;
                EffectEnabledCheck.IsEnabled = enabled;
                EffectTypeCombo.IsEnabled = enabled;
                EffectTargetCombo.IsEnabled = enabled;
                EffectThicknessBox.IsEnabled = enabled;
                EffectOffsetXBox.IsEnabled = enabled;
                EffectOffsetYBox.IsEnabled = enabled;
                EffectOpacityBox.IsEnabled = enabled;
                EffectInsideCheck.IsEnabled = enabled;
                EffectFilledCheck.IsEnabled = enabled;
                EffectColorPicker.IsEnabled = enabled;
                UpdateStackButtons();

                if (effect == null)
                {
                    EffectNameBox.Text = string.Empty;
                    EffectTypeCombo.SelectedIndex = -1;
                    EffectTargetCombo.SelectedIndex = -1;
                    EffectShadowRow.Visibility = Visibility.Collapsed;
                    EffectOutlineRow.Visibility = Visibility.Collapsed;
                    return;
                }

                EffectNameBox.Text = effect.Name;
                EffectEnabledCheck.IsChecked = effect.Enabled;
                SelectType(effect.Kind);
                SelectTarget(EffectTargetCombo, effect.Target);
                EffectThicknessBox.SetValueSilently(effect.Thickness);
                EffectOffsetXBox.SetValueSilently(effect.OffsetX);
                EffectOffsetYBox.SetValueSilently(effect.OffsetY);
                EffectOpacityBox.SetValueSilently(effect.Opacity);
                EffectInsideCheck.IsChecked = effect.Inside;
                EffectFilledCheck.IsChecked = effect.Filled;
                EffectColorPicker.Load(effect.Color);
                UpdateTypeVisibility(effect.IsShadow);
            }
            finally
            {
                suppress = false;
            }
        }

        private void SelectType(string? kind)
        {
            string value = string.Equals(kind, EffectKinds.Shadow, StringComparison.OrdinalIgnoreCase)
                ? EffectKinds.Shadow
                : EffectKinds.Outline;

            for (int index = 0; index < EffectTypeCombo.Items.Count; index++)
            {
                ComboBoxItem? item = EffectTypeCombo.Items[index] as ComboBoxItem;
                if (item != null && string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase))
                {
                    EffectTypeCombo.SelectedIndex = index;
                    return;
                }
            }

            EffectTypeCombo.SelectedIndex = 0;
        }

        /// <summary>Shows only the fields that mean something for the chosen effect type.</summary>
        private void UpdateTypeVisibility(bool shadow)
        {
            EffectShadowRow.Visibility = shadow ? Visibility.Visible : Visibility.Collapsed;
            EffectOutlineRow.Visibility = shadow ? Visibility.Collapsed : Visibility.Visible;
            EffectThicknessLabel.Text = shadow ? "Spread" : "Thickness";
            EffectThicknessBox.ToolTip = shadow
                ? "How far the shadow is spread and softened"
                : "Stroke width. Negative values render the outline to the inside.";
        }

        private void EffectTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppress)
            {
                return;
            }

            EffectLayer? effect = SelectedEffect;
            if (effect == null)
            {
                return;
            }

            ComboBoxItem? item = EffectTypeCombo.SelectedItem as ComboBoxItem;
            string? tag = item == null ? null : item.Tag as string;
            string kind = string.Equals(tag, EffectKinds.Shadow, StringComparison.OrdinalIgnoreCase)
                ? EffectKinds.Shadow
                : EffectKinds.Outline;

            if (string.Equals(effect.Kind, kind, StringComparison.Ordinal))
            {
                return;
            }

            bool wasDefaultName = string.Equals(effect.Name, "Outline", StringComparison.Ordinal)
                || string.Equals(effect.Name, "Shadow", StringComparison.Ordinal)
                || string.Equals(effect.Name, "Outline 1", StringComparison.Ordinal)
                || string.Equals(effect.Name, "Shadow 1", StringComparison.Ordinal);

            effect.Kind = kind;
            if (wasDefaultName)
            {
                effect.Name = string.Equals(kind, EffectKinds.Shadow, StringComparison.Ordinal)
                    ? "Shadow"
                    : "Outline";
            }

            suppress = true;
            try
            {
                EffectNameBox.Text = effect.Name;
            }
            finally
            {
                suppress = false;
            }

            UpdateTypeVisibility(effect.IsShadow);
            RefreshEffectList();
            UpdatePreview();
        }

        private static void SelectTarget(ComboBox combo, string? target)
        {
            string value = string.IsNullOrEmpty(target) ? EffectTarget.Font : target;
            for (int index = 0; index < combo.Items.Count; index++)
            {
                ComboBoxItem? item = combo.Items[index] as ComboBoxItem;
                if (item != null && string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = index;
                    return;
                }
            }

            combo.SelectedIndex = 0;
        }

        private static string SelectedTarget(ComboBox combo)
        {
            ComboBoxItem? item = combo.SelectedItem as ComboBoxItem;
            string? tag = item == null ? null : item.Tag as string;
            return string.IsNullOrEmpty(tag) ? EffectTarget.Font : tag;
        }

        private void EffectFieldChanged(object? sender, EventArgs e)
        {
            if (suppress)
            {
                return;
            }

            EffectLayer? effect = SelectedEffect;
            if (effect == null)
            {
                return;
            }

            string previousName = effect.Name;

            effect.Name = EffectNameBox.Text;
            effect.Enabled = EffectEnabledCheck.IsChecked == true;
            effect.Target = SelectedTarget(EffectTargetCombo);
            effect.Thickness = EffectThicknessBox.Value;
            effect.OffsetX = EffectOffsetXBox.Value;
            effect.OffsetY = EffectOffsetYBox.Value;
            effect.Opacity = EffectOpacityBox.Value;
            effect.Inside = EffectInsideCheck.IsChecked == true;
            effect.Filled = EffectFilledCheck.IsChecked == true;

            // The list only shows the name, so rebuilding it on every scrub tick is wasted work.
            if (!string.Equals(previousName, effect.Name, StringComparison.Ordinal))
            {
                RefreshEffectList();
            }

            UpdatePreview();
        }

        private void EffectColorChanged(object? sender, EventArgs e)
        {
            if (suppress)
            {
                return;
            }

            EffectLayer? effect = SelectedEffect;
            if (effect == null)
            {
                return;
            }

            effect.Color = EffectColorPicker.Capture();
            RefreshEffectList();
            UpdatePreview();
        }

        private void RefreshEffectList()
        {
            int index = EffectList.SelectedIndex;
            suppress = true;
            try
            {
                EffectList.Items.Clear();
                foreach (EffectLayer effect in draft.Effects)
                {
                    EffectList.Items.Add(effect);
                }

                if (EffectList.Items.Count > 0)
                {
                    EffectList.SelectedIndex = index >= 0 && index < EffectList.Items.Count ? index : 0;
                }
            }
            finally
            {
                suppress = false;
            }

            UpdateStackButtons();
        }

        /// <summary>Greys out the arrows that would move an entry past either end of the stack.</summary>
        private void UpdateStackButtons()
        {
            EffectLayer? effect = SelectedEffect;
            int index = effect == null ? -1 : draft.Effects.IndexOf(effect);
            EffectUpButton.IsEnabled = index > 0;
            EffectDownButton.IsEnabled = index >= 0 && index < draft.Effects.Count - 1;
        }

        private void MoveEffectUpClick(object sender, RoutedEventArgs e)
        {
            MoveEffect(-1);
        }

        private void MoveEffectDownClick(object sender, RoutedEventArgs e)
        {
            MoveEffect(1);
        }

        private void EffectListPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                return;
            }

            if (e.Key == Key.Up)
            {
                MoveEffect(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                MoveEffect(1);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Moves the selected effect one step through the stack. The list order is the paint order,
        /// so moving an entry earlier puts it underneath the ones after it.
        /// </summary>
        private void MoveEffect(int offset)
        {
            EffectLayer? effect = SelectedEffect;
            if (effect == null)
            {
                return;
            }

            int index = draft.Effects.IndexOf(effect);
            int target = index + offset;
            if (index < 0 || target < 0 || target >= draft.Effects.Count)
            {
                return;
            }

            draft.Effects.RemoveAt(index);
            draft.Effects.Insert(target, effect);

            RefreshEffectList();
            EffectList.SelectedIndex = target;
            LoadEffectFields();
            UpdatePreview();
        }

        private void AddEffectClick(object sender, RoutedEventArgs e)
        {
            // A new entry starts as the same kind as the one being edited, so adding a second
            // shadow does not mean switching the type afterwards.
            EffectLayer? current = SelectedEffect;
            bool shadow = current != null && current.IsShadow;

            EffectLayer effect = shadow ? EffectLayer.CreateShadow() : EffectLayer.CreateOutline();
            if (shadow)
            {
                effect.Name = "Shadow " + (draft.Effects.Count + 1).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                effect.Name = "Outline " + (draft.Effects.Count + 1).ToString(CultureInfo.InvariantCulture);
            }

            draft.Effects.Add(effect);
            RefreshEffectList();
            EffectList.SelectedIndex = EffectList.Items.Count - 1;
            LoadEffectFields();
            UpdatePreview();
        }

        private void CopyEffectClick(object sender, RoutedEventArgs e)
        {
            EffectLayer? source = SelectedEffect;
            if (source == null)
            {
                return;
            }

            EffectLayer copy = source.Clone();
            copy.Name = source.Name + " copy";
            draft.Effects.Add(copy);
            RefreshEffectList();
            EffectList.SelectedIndex = EffectList.Items.Count - 1;
            LoadEffectFields();
            UpdatePreview();
        }

        private void RemoveEffectClick(object sender, RoutedEventArgs e)
        {
            EffectLayer? effect = SelectedEffect;
            if (effect == null)
            {
                return;
            }

            draft.Effects.Remove(effect);
            RefreshEffectList();
            LoadEffectFields();
            UpdatePreview();
        }

        private void BasicsChanged(object? sender, EventArgs e)
        {
            if (suppress)
            {
                return;
            }

            draft.Name = NameBox.Text;
            string? family = FontCombo.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(family))
            {
                draft.FontFamily = family;
            }

            draft.Bold = BoldCheck.IsChecked == true;
            draft.Italic = ItalicCheck.IsChecked == true;
            draft.FontSize = FontSizeBox.Value;
            SyncPreviewSizeToProfile();
            draft.CharacterSpacing = CharacterSpacingBox.Value;
            draft.WordSpacing = WordSpacingBox.Value;
            draft.WordsPerDisplay = WordsBox.IntValue;
            draft.Opacity = OpacityBox.Value;
            draft.HighlightSpokenWord = HighlightCheck.IsChecked == true;

            UpdatePreview();
        }

        private void FillChanged(object? sender, EventArgs e)
        {
            if (suppress)
            {
                return;
            }

            draft.Fill = FillPicker.Capture();
            draft.HighlightFill = HighlightPicker.Capture();
            UpdatePreview();
        }

        private void PreviewSizeChanged(object? sender, EventArgs e)
        {
            previewFontSize = PreviewSizeBox.Value;
            previewSizePinned = true;
            UpdatePreview();
        }

        /// <summary>
        /// Keeps the preview at a comfortable size when the profile's own font size changes: the
        /// field shows the equivalent design size, which is the same thing the Scale works out to.
        /// </summary>
        private void SyncPreviewSizeToProfile()
        {
            if (previewSizePinned)
            {
                return;
            }

            previewFontSize = draft.FontSize > 0.5 ? draft.FontSize : 64.0;
            suppress = true;
            try
            {
                PreviewSizeBox.SetValueSilently(previewFontSize);
            }
            finally
            {
                suppress = false;
            }
        }

        private void UpdatePreview()
        {
            // The preview is a scaled-down copy of the real thing: the overlay's Scale multiplies the
            // font size *and* every effect width/offset, so what the preview shows is exactly what
            // the video shows at the same relative size. Overriding FontSize instead would leave the
            // outline thickness at the old ratio and the preview would lie.
            double reference = draft.FontSize > 0.5 ? draft.FontSize : 64.0;
            double scale = previewFontSize / reference;
            if (scale < 0.05)
            {
                scale = 0.05;
            }

            PreviewOverlay.Scale = scale;
            PreviewOverlay.TextStyle = draft;
            PreviewOverlay.Idle = PreviewIdle;
            PreviewOverlay.PopIn = PreviewPopIn;
            PreviewOverlay.InvalidateLayout();
        }

        private void ProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppress)
            {
                return;
            }

            // Picking another profile is also a commit: whatever is on screen belongs to the
            // profile that was selected until now.
            CommitDraftToStore();
            LoadDraftFromStore();
            UpdateDefaultButton();
        }

        /// <summary>
        /// Copies every field into <see cref="draft"/> and writes it back to the store, so an edit
        /// is never lost by switching profiles, adding a new one or duplicating.
        /// </summary>
        private void CommitDraftToStore()
        {
            draft.Name = NameBox.Text;
            string? family = FontCombo.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(family))
            {
                draft.FontFamily = family;
            }

            draft.Bold = BoldCheck.IsChecked == true;
            draft.Italic = ItalicCheck.IsChecked == true;
            draft.FontSize = FontSizeBox.Value;
            draft.CharacterSpacing = CharacterSpacingBox.Value;
            draft.WordSpacing = WordSpacingBox.Value;
            draft.WordsPerDisplay = WordsBox.IntValue;
            draft.Opacity = OpacityBox.Value;
            draft.HighlightSpokenWord = HighlightCheck.IsChecked == true;
            draft.Fill = FillPicker.Capture();
            draft.HighlightFill = HighlightPicker.Capture();
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                draft.Name = "Style";
            }

            // An effect that was being edited in the detail panel is already in the draft, but the
            // last keystroke in a name box may not have been committed yet.
            EffectLayer? effect = SelectedEffect;
            if (effect != null)
            {
                effect.Name = EffectNameBox.Text;
            }

            store.UpsertStyle(draft.Clone());
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

            foreach (StyleProfile profile in store.Styles)
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
            if (!store.CopyStyleSettings(draft.Id, targetId))
            {
                return;
            }

            ReloadProfileList(draft.Id);
            UpdateDefaultButton();

            MessageBox.Show(
                "The settings were copied to \"" + store.ResolveStyle(targetId).Name + "\".",
                "Copy settings",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void DuplicateProfileClick(object sender, RoutedEventArgs e)
        {
            CommitDraftToStore();

            StyleProfile copy = draft.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = draft.Name + " copy";
            store.UpsertStyle(copy);
            store.Save();
            ReloadProfileList(copy.Id);
            UpdateDefaultButton();
        }

        private void NewProfileClick(object sender, RoutedEventArgs e)
        {
            CommitDraftToStore();

            StyleProfile created = ProfileStore.CreateDefaultStyle();
            created.Id = Guid.NewGuid().ToString("N");
            created.Name = "Style " + (store.Styles.Count + 1).ToString(CultureInfo.InvariantCulture);
            store.UpsertStyle(created);
            store.Save();
            ReloadProfileList(created.Id);
            UpdateDefaultButton();
        }

        private void DeleteProfileClick(object sender, RoutedEventArgs e)
        {
            CommitDraftToStore();

            ProfileListItem? item = ProfileCombo.SelectedItem as ProfileListItem;
            if (item == null || store.Styles.Count <= 1)
            {
                MessageBox.Show(
                    this,
                    "At least one style profile has to stay.",
                    "SubtitleForge",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            StyleProfile? target = null;
            foreach (StyleProfile style in store.Styles)
            {
                if (string.Equals(style.Id, item.Id, StringComparison.Ordinal))
                {
                    target = style;
                    break;
                }
            }

            if (target == null)
            {
                return;
            }

            store.Styles.Remove(target);
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


        // ── Field entry ────────────────────────────────────────────────────

        /// <summary>
        /// Enter while a field is being edited submits that field: the value is validated, written
        /// to the draft and the field gives up focus so the rest of the window reacts. Without this
        /// Enter fell through to the window's default Save button and saved the whole profile.
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
