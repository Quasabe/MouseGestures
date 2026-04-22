using MouseGestures.Models;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MouseGestures.UI
{
    /// <summary>
    /// Adorner for drawing gesture trail and directions directly in VS UI.
    /// </summary>
    public class GestureAdorner : Adorner
    {
        private readonly List<Point> _points = new List<Point>();
        private readonly GestureVisualizationSettings _settings;
        private List<GestureDirection> _directions = new List<GestureDirection>();
        private readonly bool _isRecordingMode;
        private Pen _trailPen;
        private Brush _backgroundBrush;
        private Brush _matchedBackgroundBrush;
        private Pen _borderPen;
        private Pen _matchedBorderPen;
        private FormattedText _cachedDirectionText;
        private FormattedText _cachedCommandText;
        private string _matchedCommandName;
        private readonly Typeface _typeface;
        private readonly double _dpi;

        public GestureAdorner(UIElement adornedElement, GestureVisualizationSettings settings, bool isRecordingMode = false)
            : base(adornedElement)
        {
            _settings = settings;
            _isRecordingMode = isRecordingMode;
            IsHitTestVisible = false;

            // Pre-create frozen resources for performance
            InitializeRenderingResources();
            _typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            _dpi = VisualTreeHelper.GetDpi(adornedElement).PixelsPerDip;
        }

        private void InitializeRenderingResources()
        {
            var color = _isRecordingMode ? Colors.OrangeRed : _settings.GetTrailColorObject();
            var thickness = _isRecordingMode ? _settings.TrailThickness + 2 : _settings.TrailThickness;

            _trailPen = new Pen(new SolidColorBrush(color), thickness)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            _trailPen.Freeze();

            _backgroundBrush = new SolidColorBrush(Color.FromArgb(220, 40, 40, 40));
            _backgroundBrush.Freeze();

            _matchedBackgroundBrush = new SolidColorBrush(Color.FromArgb(220, 34, 139, 34));
            _matchedBackgroundBrush.Freeze();

            _borderPen = new Pen(new SolidColorBrush(_isRecordingMode ? Colors.OrangeRed : Colors.White), 2);
            _borderPen.Freeze();

            _matchedBorderPen = new Pen(new SolidColorBrush(Colors.LightGreen), 2);
            _matchedBorderPen.Freeze();
        }

        public void StartGesture(Point startPoint)
        {
            _points.Clear();
            _directions.Clear();
            _cachedDirectionText = null;
            _cachedCommandText = null;
            _matchedCommandName = null;
            _points.Add(startPoint);
            InvalidateVisual();
        }

        public void AddPoint(Point point)
        {
            _points.Add(point);
            InvalidateVisual();
        }

        public void UpdateDirections(List<GestureDirection> directions)
        {
            _directions = new List<GestureDirection>(directions);
            _cachedDirectionText = null; // Invalidate cache
            InvalidateVisual();
        }

        public void UpdateMatchedCommand(string commandName)
        {
            _matchedCommandName = commandName;
            _cachedCommandText = null; // Invalidate cache
            InvalidateVisual();
        }

        public void Clear()
        {
            _points.Clear();
            _directions.Clear();
            _cachedDirectionText = null;
            _cachedCommandText = null;
            _matchedCommandName = null;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (_points.Count < 2)
                return;

            // Draw trail
            if (_settings.ShowTrail || _isRecordingMode)
            {
                DrawTrail(drawingContext);
            }

            // Draw directions
            if ((_settings.ShowDirections || _isRecordingMode) && _directions.Count > 0)
            {
                DrawDirectionIndicators(drawingContext);
            }
        }

        private void DrawTrail(DrawingContext drawingContext)
        {
            if (_points.Count < 2) return;

            for (int i = 1; i < _points.Count; i++)
            {
                drawingContext.DrawLine(_trailPen, _points[i - 1], _points[i]);
            }
        }

        private void DrawDirectionIndicators(DrawingContext drawingContext)
        {
            // Create or use cached direction text
            if (_cachedDirectionText == null)
            {
                var directionsText = string.Join(" ", _directions.Select(Utils.Utils.GetDirectionArrow));
                _cachedDirectionText = new FormattedText(
                    directionsText,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    _typeface,
                    16,
                    Brushes.White,
                    _dpi);
            }

            var lastPoint = _points[_points.Count - 1];
            var hasMatch = !string.IsNullOrEmpty(_matchedCommandName);

            // Calculate dimensions for direction box
            var directionRect = new Rect(
                lastPoint.X + 20,
                lastPoint.Y - _cachedDirectionText.Height / 2,
                _cachedDirectionText.Width + 20,
                _cachedDirectionText.Height + 10);

            // If there's a matched command, we need to draw two boxes
            if (hasMatch)
            {
                // Create or use cached command text
                if (_cachedCommandText == null)
                {
                    _cachedCommandText = new FormattedText(
                        _matchedCommandName,
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        14,
                        Brushes.White,
                        _dpi);
                }

                // Adjust direction box to be on top
                directionRect.Y = lastPoint.Y - _cachedDirectionText.Height - 15;

                // Draw direction box with matched styling
                drawingContext.DrawRoundedRectangle(_matchedBackgroundBrush, _matchedBorderPen, directionRect, 8, 8);
                drawingContext.DrawText(_cachedDirectionText, new Point(directionRect.Left + 10, directionRect.Top + 5));

                // Draw command box below
                var commandRect = new Rect(
                    lastPoint.X + 20,
                    lastPoint.Y + 5,
                    _cachedCommandText.Width + 20,
                    _cachedCommandText.Height + 10);

                drawingContext.DrawRoundedRectangle(_matchedBackgroundBrush, _matchedBorderPen, commandRect, 8, 8);
                drawingContext.DrawText(_cachedCommandText, new Point(commandRect.Left + 10, commandRect.Top + 5));
            }
            else
            {
                // Draw only direction box with normal styling
                drawingContext.DrawRoundedRectangle(_backgroundBrush, _borderPen, directionRect, 8, 8);
                drawingContext.DrawText(_cachedDirectionText, new Point(directionRect.Left + 10, directionRect.Top + 5));
            }
        }
    }
}