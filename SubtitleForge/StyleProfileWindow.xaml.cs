using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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

        private StyleProfile draft = new StyleProfile();
        private bool suppress;

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

                OutlineList.Items.Clear();
                foreach (OutlineLayer outline in draft.Outlines)
                {
                    OutlineList.Items.Add(outline);
                }

                if (OutlineList.Items.Count > 0)
                {
                    OutlineList.SelectedIndex = 0;
                }

                ShadowList.Items.Clear();
                foreach (ShadowLayer shadow in draft.Shadows)
                {
                    ShadowList.Items.Add(shadow);
                }

                if (ShadowList.Items.Count > 0)
                {
                    ShadowList.SelectedIndex = 0;
                }
            }
            finally
            {
                suppress = false;
            }

            LoadOutlineFields();
            LoadShadowFields();
            UpdatePreview();
        }

        // ── Outline layer editing ──────────────────────────────────────────

        private OutlineLayer? SelectedOutline
        {
            get { return OutlineList.SelectedItem as OutlineLayer; }
        }

        private ShadowLayer? SelectedShadow
        {
            get { return ShadowList.SelectedItem as ShadowLayer; }
        }

        private void OutlineSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppress)
            {
                return;
            }

            LoadOutlineFields();
        }

        private void ShadowSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppress)
            {
                return;
            }

            LoadShadowFields();
        }

        private void LoadOutlineFields()
        {
            OutlineLayer? outline = SelectedOutline;
            suppress = true;
            try
            {
                bool enabled = outline != null;
                OutlineNameBox.IsEnabled = enabled;
                OutlineEnabledCheck.IsEnabled = enabled;
                OutlineTargetCombo.IsEnabled = enabled;
                OutlineThicknessBox.IsEnabled = enabled;
                OutlineOffsetXBox.IsEnabled = enabled;
                OutlineOffsetYBox.IsEnabled = enabled;
                OutlineInsideCheck.IsEnabled = enabled;
                OutlineFilledCheck.IsEnabled = enabled;
                OutlineColorPicker.IsEnabled = enabled;
                UpdateStackButtons();

                if (outline == null)
                {
                    OutlineNameBox.Text = string.Empty;
                    return;
                }

                OutlineNameBox.Text = outline.Name;
                OutlineEnabledCheck.IsChecked = outline.Enabled;
                SelectTarget(OutlineTargetCombo, outline.Target);
                OutlineThicknessBox.SetValueSilently(outline.Thickness);
                OutlineOffsetXBox.SetValueSilently(outline.OffsetX);
                OutlineOffsetYBox.SetValueSilently(outline.OffsetY);
                OutlineInsideCheck.IsChecked = outline.Inside;
                OutlineFilledCheck.IsChecked = outline.Filled;
                OutlineColorPicker.Load(outline.Color);
            }
            finally
            {
                suppress = false;
            }
        }

        private void LoadShadowFields()
        {
            ShadowLayer? shadow = SelectedShadow;
            suppress = true;
            try
            {
                bool enabled = shadow != null;
                ShadowNameBox.IsEnabled = enabled;
                ShadowEnabledCheck.IsEnabled = enabled;
                ShadowTargetCombo.IsEnabled = enabled;
                ShadowThicknessBox.IsEnabled = enabled;
                ShadowOffsetXBox.IsEnabled = enabled;
                ShadowOffsetYBox.IsEnabled = enabled;
                ShadowOpacityBox.IsEnabled = enabled;
                ShadowColorPicker.IsEnabled = enabled;
                UpdateStackButtons();

                if (shadow == null)
                {
                    ShadowNameBox.Text = string.Empty;
                    return;
                }

                ShadowNameBox.Text = shadow.Name;
                ShadowEnabledCheck.IsChecked = shadow.Enabled;
                SelectTarget(ShadowTargetCombo, shadow.Target);
                ShadowThicknessBox.SetValueSilently(shadow.Thickness);
                ShadowOffsetXBox.SetValueSilently(shadow.OffsetX);
                ShadowOffsetYBox.SetValueSilently(shadow.OffsetY);
                ShadowOpacityBox.SetValueSilently(shadow.Opacity);
                ShadowColorPicker.Load(shadow.Color);
            }
            finally
            {
                suppress = false;
            }
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

        private void OutlineFieldChanged(object? sender, EventArgs e)
        {
            if (suppress)
            {
                return;
            }

            OutlineLayer? outline = SelectedOutline;
            if (outline == null)
            {
                return;
            }

            outline.Name = OutlineNameBox.Text;
            outline.Enabled = OutlineEnabledCheck.IsChecked == true;
            outline.Target = SelectedTarget(OutlineTargetCombo);
            outline.Thickness = OutlineThicknessBox.Value;
            outline.OffsetX = OutlineOffsetXBox.Value;
            outline.OffsetY = OutlineOffsetYBox.Value;
            outline.Inside = OutlineInsideCheck.IsChecked == true;
            outline.Filled = OutlineFilledCheck.IsChecked == true;
            outline.Color = OutlineColorPicker.Capture();

            RefreshOutlineList();
            UpdatePreview();
        }

        private void OutlineColorChanged(object? sender, EventArgs e)
        {
            if (suppress)
            {
                return;
            }

            OutlineLayer? outline = SelectedOutline;
            if (outline == null)
            {
                return;
            }

            outline.Color = OutlineColorPicker.Capture();
            UpdatePreview();
        }

        private void ShadowFieldChanged(object? sender, EventArgs e)
        {
            if (suppress)
            {
                return;
            }

            ShadowLayer? shadow = SelectedShadow;
            if (shadow == null)
            {
                return;
            }

            shadow.Name = ShadowNameBox.Text;
            shadow.Enabled = ShadowEnabledCheck.IsChecked == true;
            shadow.Target = SelectedTarget(ShadowTargetCombo);
            shadow.Thickness = ShadowThicknessBox.Value;
            shadow.OffsetX = ShadowOffsetXBox.Value;
            shadow.OffsetY = ShadowOffsetYBox.Value;
            shadow.Opacity = ShadowOpacityBox.Value;
            shadow.Color = ShadowColorPicker.Capture();

            RefreshShadowList();
            UpdatePreview();
        }

        private void ShadowColorChanged(object? sender, EventArgs e)
        {
            if (suppress)
            {
                return;
            }

            ShadowLayer? shadow = SelectedShadow;
            if (shadow == null)
            {
                return;
            }

            shadow.Color = ShadowColorPicker.Capture();
            UpdatePreview();
        }

        private void RefreshOutlineList()
        {
            int index = OutlineList.SelectedIndex;
            suppress = true;
            try
            {
                OutlineList.Items.Clear();
                foreach (OutlineLayer outline in draft.Outlines)
                {
                    OutlineList.Items.Add(outline);
                }

                if (OutlineList.Items.Count > 0)
                {
                    OutlineList.SelectedIndex = index >= 0 && index < OutlineList.Items.Count ? index : 0;
                }
            }
            finally
            {
                suppress = false;
            }
        }

        private void RefreshShadowList()
        {
            int index = ShadowList.SelectedIndex;
            suppress = true;
            try
            {
                ShadowList.Items.Clear();
                foreach (ShadowLayer shadow in draft.Shadows)
                {
                    ShadowList.Items.Add(shadow);
                }

                if (ShadowList.Items.Count > 0)
                {
                    ShadowList.SelectedIndex = index >= 0 && index < ShadowList.Items.Count ? index : 0;
                }
            }
            finally
            {
                suppress = false;
            }
        }

        // ── Stack order ────────────────────────────────────────────────────

        /// <summary>Greys out the arrows that would move an entry past either end of the stack.</summary>
        private void UpdateStackButtons()
        {
            OutlineLayer? outline = SelectedOutline;
            int outlineIndex = outline == null ? -1 : draft.Outlines.IndexOf(outline);
            OutlineUpButton.IsEnabled = outlineIndex > 0;
            OutlineDownButton.IsEnabled = outlineIndex >= 0 && outlineIndex < draft.Outlines.Count - 1;

            ShadowLayer? shadow = SelectedShadow;
            int shadowIndex = shadow == null ? -1 : draft.Shadows.IndexOf(shadow);
            ShadowUpButton.IsEnabled = shadowIndex > 0;
            ShadowDownButton.IsEnabled = shadowIndex >= 0 && shadowIndex < draft.Shadows.Count - 1;
        }

        private void MoveOutlineUpClick(object sender, RoutedEventArgs e)
        {
            MoveOutline(-1);
        }

        private void MoveOutlineDownClick(object sender, RoutedEventArgs e)
        {
            MoveOutline(1);
        }

        private void MoveShadowUpClick(object sender, RoutedEventArgs e)
        {
            MoveShadow(-1);
        }

        private void MoveShadowDownClick(object sender, RoutedEventArgs e)
        {
            MoveShadow(1);
        }

        /// <summary>
        /// Moves the selected outline one step through the stack. The list order is the paint order,
        /// so moving an entry earlier puts its ring underneath the ones after it.
        /// </summary>
        private void MoveOutline(int offset)
        {
            OutlineLayer? outline = SelectedOutline;
            if (outline == null)
            {
                return;
            }

            int index = draft.Outlines.IndexOf(outline);
            int target = index + offset;
            if (index < 0 || target < 0 || target >= draft.Outlines.Count)
            {
                return;
            }

            draft.Outlines.RemoveAt(index);
            draft.Outlines.Insert(target, outline);

            RefreshOutlineList();
            OutlineList.SelectedIndex = target;
            LoadOutlineFields();
            UpdatePreview();
        }

        private void MoveShadow(int offset)
        {
            ShadowLayer? shadow = SelectedShadow;
            if (shadow == null)
            {
                return;
            }

            int index = draft.Shadows.IndexOf(shadow);
            int target = index + offset;
            if (index < 0 || target < 0 || target >= draft.Shadows.Count)
            {
                return;
            }

            draft.Shadows.RemoveAt(index);
            draft.Shadows.Insert(target, shadow);

            RefreshShadowList();
            ShadowList.SelectedIndex = target;
            LoadShadowFields();
            UpdatePreview();
        }

        private void StackPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                return;
            }

            bool outlineList = ReferenceEquals(sender, OutlineList);
            if (e.Key == Key.Up)
            {
                if (outlineList)
                {
                    MoveOutline(-1);
                }
                else
                {
                    MoveShadow(-1);
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if (outlineList)
                {
                    MoveOutline(1);
                }
                else
                {
                    MoveShadow(1);
                }

                e.Handled = true;
            }
        }

        private void AddOutlineClick(object sender, RoutedEventArgs e)
        {
            OutlineLayer outline = new OutlineLayer();
            outline.Name = "Outline " + (draft.Outlines.Count + 1).ToString(CultureInfo.InvariantCulture);
            outline.Color = ColorSpec.Solid("#FF000000");
            draft.Outlines.Add(outline);
            RefreshOutlineList();
            OutlineList.SelectedIndex = OutlineList.Items.Count - 1;
            LoadOutlineFields();
            UpdatePreview();
        }

        private void CopyOutlineClick(object sender, RoutedEventArgs e)
        {
            OutlineLayer? source = SelectedOutline;
            if (source == null)
            {
                return;
            }

            OutlineLayer copy = source.Clone();
            copy.Name = source.Name + " copy";
            draft.Outlines.Add(copy);
            RefreshOutlineList();
            OutlineList.SelectedIndex = OutlineList.Items.Count - 1;
            LoadOutlineFields();
            UpdatePreview();
        }

        private void RemoveOutlineClick(object sender, RoutedEventArgs e)
        {
            OutlineLayer? outline = SelectedOutline;
            if (outline == null)
            {
                return;
            }

            draft.Outlines.Remove(outline);
            RefreshOutlineList();
            LoadOutlineFields();
            UpdatePreview();
        }

        private void AddShadowClick(object sender, RoutedEventArgs e)
        {
            ShadowLayer shadow = new ShadowLayer();
            shadow.Name = "Shadow " + (draft.Shadows.Count + 1).ToString(CultureInfo.InvariantCulture);
            draft.Shadows.Add(shadow);
            RefreshShadowList();
            ShadowList.SelectedIndex = ShadowList.Items.Count - 1;
            LoadShadowFields();
            UpdatePreview();
        }

        private void CopyShadowClick(object sender, RoutedEventArgs e)
        {
            ShadowLayer? source = SelectedShadow;
            if (source == null)
            {
                return;
            }

            ShadowLayer copy = source.Clone();
            copy.Name = source.Name + " copy";
            draft.Shadows.Add(copy);
            RefreshShadowList();
            ShadowList.SelectedIndex = ShadowList.Items.Count - 1;
            LoadShadowFields();
            UpdatePreview();
        }

        private void RemoveShadowClick(object sender, RoutedEventArgs e)
        {
            ShadowLayer? shadow = SelectedShadow;
            if (shadow == null)
            {
                return;
            }

            draft.Shadows.Remove(shadow);
            RefreshShadowList();
            LoadShadowFields();
            UpdatePreview();
        }

        // ── Basics ────────────────────────────────────────────────────────

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
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            StyleProfile preview = draft.Clone();
            preview.FontSize = previewFontSize;
            PreviewOverlay.TextStyle = preview;
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

            LoadDraftFromStore();
            UpdateDefaultButton();
        }

        private void NewProfileClick(object sender, RoutedEventArgs e)
        {
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
            BasicsChanged(sender, e);
            FillChanged(sender, e);
            draft.Fill = FillPicker.Capture();
            draft.HighlightFill = HighlightPicker.Capture();
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                draft.Name = "Style";
            }

            store.UpsertStyle(draft.Clone());
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
