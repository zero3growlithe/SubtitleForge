using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SubtitleForge
{
    /// <summary>
    /// Minimal modal prompt — one label, one text box, OK/Cancel. Used for renaming tracks
    /// so the app does not need a XAML window per field it wants to edit.
    /// </summary>
    public class TextInputDialog : Window
    {
        private readonly TextBox input;

        public TextInputDialog(string title, string label, string initialValue)
        {
            Title = title;
            Width = 380;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F));
            ShowInTaskbar = false;

            Grid root = new Grid();
            root.Margin = new Thickness(14);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock labelBlock = new TextBlock();
            labelBlock.Text = label;
            labelBlock.Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xDC, 0xE6));
            labelBlock.Margin = new Thickness(0, 0, 0, 6);
            Grid.SetRow(labelBlock, 0);
            root.Children.Add(labelBlock);

            input = new TextBox();
            input.Text = initialValue ?? string.Empty;
            input.Margin = new Thickness(0, 0, 0, 12);
            input.Padding = new Thickness(6, 4, 6, 4);
            input.KeyDown += InputKeyDown;
            Grid.SetRow(input, 1);
            root.Children.Add(input);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(buttons, 2);

            Button okButton = new Button();
            okButton.Content = "OK";
            okButton.MinWidth = 78;
            okButton.Padding = new Thickness(12, 4, 12, 4);
            okButton.Margin = new Thickness(0, 0, 8, 0);
            okButton.IsDefault = true;
            okButton.Click += OkClick;
            buttons.Children.Add(okButton);

            Button cancelButton = new Button();
            cancelButton.Content = "Cancel";
            cancelButton.MinWidth = 78;
            cancelButton.Padding = new Thickness(12, 4, 12, 4);
            cancelButton.IsCancel = true;
            buttons.Children.Add(cancelButton);

            root.Children.Add(buttons);
            Content = root;

            Loaded += DialogLoaded;
        }

        public string ResponseText
        {
            get { return input.Text.Trim(); }
        }

        private void DialogLoaded(object sender, RoutedEventArgs e)
        {
            input.Focus();
            input.SelectAll();
        }

        private void InputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            DialogResult = true;
            e.Handled = true;
        }

        private void OkClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
