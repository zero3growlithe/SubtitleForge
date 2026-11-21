using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SubtitleForge
{
    /// <summary>
    /// The whole keyboard map in one modal window. Built in code rather than XAML so the shortcut
    /// list cannot drift away from the handler: the table below is the only source, and the dialog
    /// is a straight rendering of it.
    /// </summary>
    public class HotkeysWindow : Window
    {
        private const string PlaybackGroup = "Playback";
        private const string TimelineGroup = "Timeline / tracks";
        private const string CueGroup = "Cues";
        private const string ProjectGroup = "Project / files";

        /// <summary>
        /// Group, key names and what they do, in the order the dialog shows them. Every entry here
        /// mirrors a branch of MainWindow.WindowPreviewKeyDown or a menu item next to it.
        /// </summary>
        private static readonly ShortcutRow[] Shortcuts = new ShortcutRow[]
        {
            // ── Playback ───────────────────────────────────────────────────
            new ShortcutRow(PlaybackGroup, "Space", "Play or pause the preview."),
            new ShortcutRow(PlaybackGroup, "← / →", "Move the playhead one second back or forward."),
            new ShortcutRow(PlaybackGroup, "Shift+← / Shift+→", "Move the playhead ten seconds back or forward."),
            new ShortcutRow(PlaybackGroup, ", / .", "Step one frame back or forward."),
            new ShortcutRow(PlaybackGroup, "Home / End", "Jump to the start or the end of the video."),

            // ── Timeline / tracks ──────────────────────────────────────────
            new ShortcutRow(TimelineGroup, "Ctrl+Shift+D", "Split the selected clips at the playhead; both halves stay in the same row."),
            new ShortcutRow(TimelineGroup, "Ctrl+Alt+Shift+D", "Split the selected clips and move the second half into a new track right below."),
            new ShortcutRow(TimelineGroup, "Ctrl+Shift+J", "Join the selected clips back into one."),
            new ShortcutRow(TimelineGroup, "Ctrl+M", "Merge the selected tracks down."),
            new ShortcutRow(TimelineGroup, "Ctrl+Shift+M", "Merge the selected tracks up."),
            new ShortcutRow(TimelineGroup, "Ctrl+T", "Add an audio track."),
            new ShortcutRow(TimelineGroup, "Ctrl+R", "Record into the selected audio clip at the playhead; press it again to stop."),
            new ShortcutRow(TimelineGroup, "Delete", "Delete the selection: cues, clips or tracks, whichever view was used last."),
            new ShortcutRow(TimelineGroup, "Alt+wheel", "Zoom the timeline around the pointer."),
            new ShortcutRow(TimelineGroup, "Ctrl+wheel", "Make the track rows taller or shorter."),
            new ShortcutRow(TimelineGroup, "Shift+wheel", "Scroll along the timeline while it is zoomed in."),

            // ── Cues ───────────────────────────────────────────────────────
            new ShortcutRow(CueGroup, "Ctrl+Q", "Add a cue at the playhead."),
            new ShortcutRow(CueGroup, "[ / ]", "Jump to the start of the previous or next cue."),
            new ShortcutRow(CueGroup, "Ctrl+[ / Ctrl+]", "Set the start or the end of the selected cues to the playhead."),
            new ShortcutRow(CueGroup, "Ctrl+- / Ctrl++", "Move the selected cues and the playhead by 100 ms."),
            new ShortcutRow(CueGroup, "Ctrl+Alt+- / Ctrl+Alt++", "Move only the start of the selected cues by 100 ms."),
            new ShortcutRow(CueGroup, "Ctrl+Alt+Shift+- / Ctrl+Alt+Shift++", "Move only the end of the selected cues by 100 ms."),

            // ── Project / files ────────────────────────────────────────────
            new ShortcutRow(ProjectGroup, "Ctrl+S", "Save the project; the first save asks for a file."),
            new ShortcutRow(ProjectGroup, "Ctrl+Shift+S", "Save the project under a new name."),
            new ShortcutRow(ProjectGroup, "Ctrl+Z", "Undo the last edit."),
            new ShortcutRow(ProjectGroup, "Ctrl+Y / Ctrl+Shift+Z", "Redo the edit that was undone."),
            new ShortcutRow(ProjectGroup, "Escape", "Leave a text field; a numeric field gets its previous value back first.")
        };

        public HotkeysWindow()
        {
            Title = "Keyboard shortcuts";
            Width = 520;
            Height = 640;
            MinWidth = 380;
            MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;
            Background = ResourceBrush("WindowBackgroundBrush", Color.FromRgb(0x1B, 0x1B, 0x1F));

            Content = BuildContent();

            // Escape closes the window even when nothing inside it has the focus, which is the same
            // expectation every other dialog in the app sets.
            PreviewKeyDown += DialogPreviewKeyDown;
        }

        /// <summary>
        /// One line of the map: a group heading, the keys as they are typed and the sentence the user
        /// reads next to them.
        /// </summary>
        private sealed class ShortcutRow
        {
            public ShortcutRow(string group, string keys, string description)
            {
                Group = group;
                Keys = keys;
                Description = description;
            }

            public string Group { get; }

            public string Keys { get; }

            public string Description { get; }
        }

        private UIElement BuildContent()
        {
            Grid root = new Grid();
            root.Margin = new Thickness(16, 14, 16, 14);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock intro = new TextBlock();
            intro.Text = "Shortcuts work anywhere in the window except inside a text field, where the keys"
                + " edit the field instead.";
            intro.TextWrapping = TextWrapping.Wrap;
            intro.Foreground = ResourceBrush("SubtleTextBrush", Color.FromRgb(0x9A, 0x9A, 0xA6));
            intro.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetRow(intro, 0);
            root.Children.Add(intro);

            StackPanel list = new StackPanel();
            string currentGroup = string.Empty;
            foreach (ShortcutRow shortcut in Shortcuts)
            {
                if (shortcut.Group != currentGroup)
                {
                    currentGroup = shortcut.Group;
                    list.Children.Add(BuildGroupHeading(currentGroup));
                }

                list.Children.Add(BuildRow(shortcut));
            }

            ScrollViewer scroller = new ScrollViewer();
            scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroller.Padding = new Thickness(0, 0, 10, 0);
            scroller.Content = list;
            Grid.SetRow(scroller, 1);
            root.Children.Add(scroller);

            Button closeButton = new Button();
            closeButton.Content = "Close";
            closeButton.MinWidth = 78;
            closeButton.Padding = new Thickness(12, 4, 12, 4);
            closeButton.Margin = new Thickness(0, 12, 0, 0);
            closeButton.HorizontalAlignment = HorizontalAlignment.Right;
            closeButton.IsCancel = true;
            closeButton.IsDefault = true;
            Grid.SetRow(closeButton, 2);
            root.Children.Add(closeButton);

            return root;
        }

        private UIElement BuildGroupHeading(string group)
        {
            TextBlock heading = new TextBlock();
            heading.Text = group.ToUpperInvariant();
            heading.FontWeight = FontWeights.SemiBold;
            heading.Foreground = ResourceBrush("AccentTextBrush", Color.FromRgb(0x4A, 0xA3, 0xFF));
            heading.Margin = new Thickness(0, 14, 0, 6);
            return heading;
        }

        private UIElement BuildRow(ShortcutRow shortcut)
        {
            Grid row = new Grid();
            row.Margin = new Thickness(0, 0, 0, 6);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224.0) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            // The keys are monospaced so the columns line up and a combination reads as one token.
            TextBlock keys = new TextBlock();
            keys.Text = shortcut.Keys;
            keys.FontFamily = new FontFamily("Consolas");
            keys.FontSize = 12.0;
            keys.Foreground = ResourceBrush("TextBrush", Color.FromRgb(0xE8, 0xE8, 0xEC));
            keys.TextWrapping = TextWrapping.Wrap;
            keys.Margin = new Thickness(0, 0, 10, 0);
            keys.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(keys, 0);
            row.Children.Add(keys);

            TextBlock description = new TextBlock();
            description.Text = shortcut.Description;
            description.TextWrapping = TextWrapping.Wrap;
            description.Foreground = ResourceBrush("SubtleTextBrush", Color.FromRgb(0x9A, 0x9A, 0xA6));
            description.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(description, 1);
            row.Children.Add(description);

            return row;
        }

        /// <summary>
        /// Looks the brush up in the window and then in the app resources — the dialog is created
        /// before it has a visual parent, so a failed lookup would otherwise leave black-on-black.
        /// </summary>
        private Brush ResourceBrush(string resourceKey, Color fallback)
        {
            object? resource = TryFindResource(resourceKey);
            if (resource == null && Application.Current != null)
            {
                resource = Application.Current.TryFindResource(resourceKey);
            }

            Brush? brush = resource as Brush;
            if (brush != null)
            {
                return brush;
            }

            return new SolidColorBrush(fallback);
        }

        private void DialogPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
            {
                return;
            }

            Close();
            e.Handled = true;
        }
    }
}
