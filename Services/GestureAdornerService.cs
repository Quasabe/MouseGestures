using MouseGestures.Models;
using MouseGestures.UI;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MouseGestures.Services
{
    /// <summary>
    /// Service for managing gesture adorner in VS main window.
    /// </summary>
    public class GestureAdornerService : IDisposable
    {
        private readonly TraceSource _logger;
        private GestureAdorner _adorner;
        private UIElement _adornerTarget;
        private AdornerLayer _adornerLayer;
        private bool _disposed;

        public GestureAdornerService(TraceSource logger)
        {
            _logger = logger;
        }

        public bool TryInitialize(GestureVisualizationSettings settings, bool isRecordingMode = false)
        {
            try
            {
                // Get VS main window
                var mainWindow = Application.Current?.MainWindow;
                if (mainWindow == null)
                {
                    _logger.TraceEvent(TraceEventType.Warning, 0, "Could not find Application MainWindow");
                    return false;
                }

                // Find the root content element
                _adornerTarget = FindAdornerTarget(mainWindow);
                if (_adornerTarget == null)
                {
                    _logger.TraceEvent(TraceEventType.Warning, 0, "Could not find suitable adorner target");
                    return false;
                }

                // Get adorner layer
                _adornerLayer = AdornerLayer.GetAdornerLayer(_adornerTarget);
                if (_adornerLayer == null)
                {
                    _logger.TraceEvent(TraceEventType.Warning, 0, "Could not get AdornerLayer");
                    return false;
                }

                // Create adorner
                _adorner = new GestureAdorner(_adornerTarget, settings, isRecordingMode);
                _adornerLayer.Add(_adorner);

                _logger.TraceEvent(TraceEventType.Information, 0, "Gesture adorner initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.TraceEvent(TraceEventType.Error, 0, $"Failed to initialize adorner: {ex.Message}");
                return false;
            }
        }

        private UIElement FindAdornerTarget(Window window)
        {
            // Try to find the content presenter or root grid
            if (window.Content is UIElement content)
            {
                return content;
            }

            // Fallback: try to find via visual tree
            return FindFirstVisualChild<UIElement>(window);
        }

        private T FindFirstVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    return typedChild;

                var result = FindFirstVisualChild<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        public void StartGesture(Point startPoint)
        {
            if (_adorner == null) return;

            try
            {
                // Convert screen coordinates to adorner coordinates
                var localPoint = ConvertScreenToLocal(startPoint);
                _adorner.StartGesture(localPoint);
            }
            catch (Exception ex)
            {
                _logger.TraceEvent(TraceEventType.Error, 0, $"StartGesture error: {ex.Message}");
            }
        }

        public void AddPoint(Point point)
        {
            if (_adorner == null) return;

            try
            {
                var localPoint = ConvertScreenToLocal(point);
                _adorner.AddPoint(localPoint);
            }
            catch (Exception ex)
            {
                _logger.TraceEvent(TraceEventType.Error, 0, $"AddPoint error: {ex.Message}");
            }
        }

        public void UpdateDirections(System.Collections.Generic.List<GestureDirection> directions)
        {
            if (_adorner == null) return;

            try
            {
                _adorner.UpdateDirections(directions);
            }
            catch (Exception ex)
            {
                _logger.TraceEvent(TraceEventType.Error, 0, $"UpdateDirections error: {ex.Message}");
            }
        }

        public void UpdateMatchedCommand(string commandName)
        {
            if (_adorner == null) return;

            try
            {
                _adorner.UpdateMatchedCommand(commandName);
            }
            catch (Exception ex)
            {
                _logger.TraceEvent(TraceEventType.Error, 0, $"UpdateMatchedCommand error: {ex.Message}");
            }
        }

        public void EndGesture()
        {
            if (_adorner == null) return;

            try
            {
                _adorner.Clear();
            }
            catch (Exception ex)
            {
                _logger.TraceEvent(TraceEventType.Error, 0, $"EndGesture error: {ex.Message}");
            }
        }

        private Point ConvertScreenToLocal(Point screenPoint)
        {
            if (_adornerTarget == null)
                return screenPoint;

            try
            {
                // Convert screen coordinates to element coordinates
                return _adornerTarget.PointFromScreen(screenPoint);
            }
            catch
            {
                return screenPoint;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                if (_adorner != null && _adornerLayer != null)
                {
                    _adornerLayer.Remove(_adorner);
                    _adorner = null;
                }

                _adornerLayer = null;
                _adornerTarget = null;
                _disposed = true;

                _logger.TraceEvent(TraceEventType.Information, 0, "Gesture adorner disposed");
            }
            catch (Exception ex)
            {
                _logger.TraceEvent(TraceEventType.Warning, 0, $"Dispose error: {ex.Message}");
            }
        }
    }
}