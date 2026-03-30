using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using MouseGestures.Models;

namespace MouseGestures.Services
{
    /// <summary>
    /// Service for recognizing mouse gesture patterns.
    /// </summary>
    public class GestureRecognitionService
    {
        private readonly List<Point> _gesturePoints = new List<Point>();
        private readonly TraceSource _logger;
        private const double MinimumDistance = 20.0;

        public GestureRecognitionService(TraceSource logger)
        {
            _logger = logger;
        }

        public void StartGesture(Point startPoint)
        {
            _gesturePoints.Clear();
            _gesturePoints.Add(startPoint);
            _logger.TraceEvent(TraceEventType.Information, 0, $"Gesture started at {startPoint}");
        }

        public void AddPoint(Point point)
        {
            _gesturePoints.Add(point);
        }

        public List<GestureDirection> EndGesture()
        {
            var directions = RecognizeDirections();
            _logger.TraceEvent(TraceEventType.Information, 0, $"Gesture ended with pattern: {string.Join(", ", directions)}");
            return directions;
        }

        public List<GestureDirection> RecognizeDirections()
        {
            var directions = new List<GestureDirection>();

            if (_gesturePoints.Count < 2)
                return directions;

            Point lastSignificantPoint = _gesturePoints[0];

            for (int i = 1; i < _gesturePoints.Count; i++)
            {
                Point currentPoint = _gesturePoints[i];
                double dx = currentPoint.X - lastSignificantPoint.X;
                double dy = currentPoint.Y - lastSignificantPoint.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);

                if (distance < MinimumDistance)
                    continue;

                GestureDirection? direction = Utils.Utils.DetermineDirection(dx, dy);

                if (direction.HasValue)
                {
                    if (directions.Count == 0 || directions[directions.Count - 1] != direction.Value)
                    {
                        directions.Add(direction.Value);
                    }

                    lastSignificantPoint = currentPoint;
                }
            }

            return directions;
        }
    }
}