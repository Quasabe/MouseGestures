using System.ComponentModel;
using System.Windows.Media;

namespace MouseGestures.Models
{
    /// <summary>
    /// Global settings for gesture visualization.
    /// </summary>
    public class GestureVisualizationSettings : INotifyPropertyChanged
    {
        private bool _showTrail = true;
        private bool _showDirections = true;
        private string _trailColor = "#7B68AB";
        private double _trailThickness = 3.0;
        private double _minimumGestureDistance = 10.0;

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public bool ShowTrail
        {
            get => _showTrail;
            set { if (_showTrail != value) { _showTrail = value; OnPropertyChanged(nameof(ShowTrail)); } }
        }

        public bool ShowDirections
        {
            get => _showDirections;
            set { if (_showDirections != value) { _showDirections = value; OnPropertyChanged(nameof(ShowDirections)); } }
        }

        public string TrailColor
        {
            get => _trailColor;
            set { if (_trailColor != value) { _trailColor = value; OnPropertyChanged(nameof(TrailColor)); } }
        }

        public double TrailThickness
        {
            get => _trailThickness;
            set { if (_trailThickness != value) { _trailThickness = value; OnPropertyChanged(nameof(TrailThickness)); } }
        }

        public double MinimumGestureDistance
        {
            get => _minimumGestureDistance;
            set { if (_minimumGestureDistance != value) { _minimumGestureDistance = value; OnPropertyChanged(nameof(MinimumGestureDistance)); } }
        }

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