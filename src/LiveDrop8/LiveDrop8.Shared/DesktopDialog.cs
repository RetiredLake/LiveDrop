#if !WINDOWS_PHONE_APP
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI;

namespace LiveDrop8
{
    // Windows 8.1 desktop predates ContentDialog; keep its small phone-compatible surface.
    internal enum ContentDialogResult { None, Primary, Secondary }
    internal sealed class ContentDialog
    {
        public string Title { get; set; }
        public UIElement Content { get; set; }
        public string PrimaryButtonText { get; set; }
        public string SecondaryButtonText { get; set; }

        public Task<ContentDialogResult> ShowAsync()
        {
            var completion = new TaskCompletionSource<ContentDialogResult>();
            var popup = new Popup();
            var panel = new StackPanel { Width = 360, Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = Title, FontSize = 20, Margin = new Thickness(0, 0, 0, 20) });
            panel.Children.Add(Content);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
            var primary = new Button { Content = PrimaryButtonText };
            primary.Click += (s, e) => { completion.TrySetResult(ContentDialogResult.Primary); popup.IsOpen = false; };
            buttons.Children.Add(primary);
            if (!string.IsNullOrEmpty(SecondaryButtonText))
            {
                var secondary = new Button { Content = SecondaryButtonText, Margin = new Thickness(12, 0, 0, 0) };
                secondary.Click += (s, e) => { completion.TrySetResult(ContentDialogResult.Secondary); popup.IsOpen = false; };
                buttons.Children.Add(secondary);
            }
            panel.Children.Add(buttons);
            var overlay = new Grid { Width = Window.Current.Bounds.Width, Height = Window.Current.Bounds.Height, Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)) };
            var border = new Border { Child = panel, Background = new SolidColorBrush(Colors.Black), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            overlay.Children.Add(border);
            popup.Child = overlay;
            popup.Closed += (s, e) => completion.TrySetResult(ContentDialogResult.None);
            popup.IsOpen = true;
            return completion.Task;
        }
    }
}
#endif
