using System.Windows.Media;

namespace MouseGestures.Models
{
    /// <summary>
    /// Global settings for gesture visualization.
    /// </summary>
    public class GestureVisualizationSettings
    {
        public bool ShowTrail { get; set; } = true;
        public bool ShowDirections { get; set; } = true;
        public string TrailColor { get; set; } = "#7B68AB";
        public double TrailThickness { get; set; } = 3.0;
        public double MinimumGestureDistance { get; set; } = 10.0;

        public Color GetTrailColorObject()
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(TrailColor);
            }
            catch
            {
                return Colors.Purple;
            }
        }
    }
}