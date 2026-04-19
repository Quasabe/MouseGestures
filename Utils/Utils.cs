using MouseGestures.Models;
using MouseGestures.UI;
using MouseGestures.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MouseGestures.Utils
{
    public static class Utils
    {
        public static List<VsCommandInfo> GetCommonVsCommands()
        {
            return new List<VsCommandInfo>
            {
                new VsCommandInfo("View.NavigateBackward", "Navigate Backward"),
                new VsCommandInfo("View.NavigateForward", "Navigate Forward"),
                new VsCommandInfo("Edit.GoToDefinition", "Go To Definition"),
                new VsCommandInfo("Edit.FindAllReferences", "Find All References"),
                new VsCommandInfo("Edit.CommentSelection", "Comment Selection"),
                new VsCommandInfo("Edit.UncommentSelection", "Uncomment Selection"),
                new VsCommandInfo("Edit.Copy", "Copy"),
                new VsCommandInfo("Edit.Cut", "Cut"),
                new VsCommandInfo("Edit.Paste", "Paste"),
                new VsCommandInfo("Edit.Undo", "Undo"),
                new VsCommandInfo("Edit.Redo", "Redo"),
                new VsCommandInfo("Build.BuildSolution", "Build Solution"),
                new VsCommandInfo("Debug.Start", "Start Debugging"),
                new VsCommandInfo("Debug.StopDebugging", "Stop Debugging"),
                new VsCommandInfo("File.SaveAll", "Save All"),
                new VsCommandInfo("Window.CloseDocumentWindow", "Close Document"),
                new VsCommandInfo("Edit.FormatDocument", "Format Document"),
                new VsCommandInfo("Edit.FormatSelection", "Format Selection"),
                new VsCommandInfo("Refactor.Rename", "Rename"),
                new VsCommandInfo("View.SolutionExplorer", "Solution Explorer"),
                new VsCommandInfo("View.Output", "Output Window"),
                new VsCommandInfo("View.ErrorList", "Error List"),
                new VsCommandInfo("Edit.QuickInfo", "Quick Info"),
                new VsCommandInfo("Edit.ParameterInfo", "Parameter Info"),
                new VsCommandInfo("Edit.CompleteWord", "Complete Word"),
            };
        }

        public static string GetDirectionArrow(GestureDirection direction)
        {
            switch (direction)
            {
                case GestureDirection.Up: return "↑";
                case GestureDirection.Down: return "↓";
                case GestureDirection.Left: return "←";
                case GestureDirection.Right: return "→";
                default: return "?";
            }
        }

        public static GestureDirection? DetermineDirection(double dx, double dy)
        {
            // Use only cardinal directions (no diagonals)
            bool isHorizontal = Math.Abs(dx) > Math.Abs(dy);

            if (isHorizontal)
                return dx > 0 ? GestureDirection.Right : GestureDirection.Left;

            return dy > 0 ? GestureDirection.Down : GestureDirection.Up;
        }

        public static double GetDistance(Point p1, Point p2)
        {
            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static void Show(string message, Point position)
        {
            var toast = new GestureToast(message, position);
            toast.Show();
        }
    }

    /// <summary>
    /// Converts a hex color string (e.g. "#7B68AB") to a WPF SolidColorBrush.
    /// Returns a transparent brush for any invalid value so the preview border stays empty.
    /// </summary>
    public class HexColorToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string hex && !string.IsNullOrWhiteSpace(hex))
            {
                try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
                catch { /* fall through */ }
            }
            return new SolidColorBrush(Colors.Transparent);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}