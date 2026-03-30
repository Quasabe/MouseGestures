using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MouseGestures.UI
{
    public class GestureToast : Window
    {
        public GestureToast(string message, Point position)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 40, 40, 40)),
                BorderBrush = new SolidColorBrush(Colors.LimeGreen),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15, 8, 15, 8),
                Child = new TextBlock
                {
                    Text = message,
                    Foreground = Brushes.White,
                    FontSize = 14,
                    FontWeight = FontWeights.Bold
                }
            };

            Content = border;
            Left = position.X - 50;
            Top = position.Y - 30;

            // Fade out animation
            Loaded += (s, e) =>
            {
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5))
                {
                    BeginTime = TimeSpan.FromSeconds(1)
                };
                fadeOut.Completed += (_, __) => Close();
                BeginAnimation(OpacityProperty, fadeOut);
            };
        }
    }
}