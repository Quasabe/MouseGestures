using Microsoft.VisualStudio.Shell;
using MouseGestures.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;

namespace MouseGestures.Services
{
    /// <summary>
    /// Orchestrates gesture detection, visualization, and command execution.
    /// </summary>
    public class GestureOrchestratorService : IDisposable
    {
        private readonly MouseHookService _mouseHook;
        private readonly GestureRecognitionService _recognitionService;
        private readonly GestureManagerService _gestureManager;
        private readonly TraceSource _logger;
        private readonly GestureVisualizationSettings _visualSettings;
        private GestureAdornerService _adornerService;
        private Action<List<GestureDirection>> _recordingCallback;
        private Action _gestureStartedCallback;
        private bool _isRecordingMode;
        private bool _visualizationEnabled = true;

        public GestureVisualizationSettings VisualizationSettings => _visualSettings;

        public GestureOrchestratorService(
            MouseHookService mouseHook,
            GestureRecognitionService recognitionService,
            GestureManagerService gestureManager,
            TraceSource logger)
        {
            _mouseHook = mouseHook;
            _recognitionService = recognitionService;
            _gestureManager = gestureManager;
            _logger = logger;
            _visualSettings = new GestureVisualizationSettings();

            SubscribeToEvents();
        }

        public void Start()
        {
            _mouseHook.StartHook();
            _logger.TraceEvent(TraceEventType.Information, 0, "Gesture orchestrator started");
        }

        public void Stop()
        {
            _mouseHook.StopHook();
            CleanupAdorner();
            _isRecordingMode = false;
            _recordingCallback = null;
            _logger.TraceEvent(TraceEventType.Information, 0, "Gesture orchestrator stopped");
        }

        public void StartGestureRecording(Action<List<GestureDirection>> patternCallback, Action gestureStartedCallback = null)
        {
            _isRecordingMode = true;
            _recordingCallback = patternCallback;
            _gestureStartedCallback = gestureStartedCallback;
            _mouseHook.SetRecordingMode(true);
            _logger.TraceEvent(TraceEventType.Information, 0, "Started gesture recording mode");
        }

        public void StopGestureRecording()
        {
            _isRecordingMode = false;
            _recordingCallback = null;
            _gestureStartedCallback = null;
            _mouseHook.SetRecordingMode(false);
            CleanupAdorner();

            // Reset mouse hook state to prevent blocking mouse events
            _mouseHook.ResetGestureState();

            _logger.TraceEvent(TraceEventType.Information, 0, "Stopped gesture recording mode");
        }

        private void SubscribeToEvents()
        {
            _mouseHook.GestureStarted += OnGestureStarted;
            _mouseHook.GesturePointAdded += OnGesturePointAdded;
            _mouseHook.GestureEnded += OnGestureEnded;
            _mouseHook.RightClickDetected += OnRightClickDetected;
        }

        private void OnGestureStarted(object sender, Point startPoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _recognitionService.StartGesture(startPoint);

            // Call gesture started callback (for making window transparent)
            if (_isRecordingMode && _gestureStartedCallback != null)
            {
                try
                {
                    _gestureStartedCallback.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.TraceEvent(TraceEventType.Warning, 0, $"Gesture started callback error: {ex.Message}");
                }
            }

            // Show visualization in both normal and recording mode
            if (_visualizationEnabled && (_visualSettings.ShowTrail || _visualSettings.ShowDirections || _isRecordingMode))
            {
                try
                {
                    // Ensure previous adorner is cleaned up
                    CleanupAdorner();

                    _adornerService = new GestureAdornerService(_logger);
                    if (_adornerService.TryInitialize(_visualSettings, _isRecordingMode))
                    {
                        _adornerService.StartGesture(startPoint);
                        _logger.TraceEvent(TraceEventType.Information, 0, "Gesture visualization started");
                    }
                    else
                    {
                        _logger.TraceEvent(TraceEventType.Warning, 0, "Failed to initialize adorner, disabling visualization");
                        _visualizationEnabled = false;
                        CleanupAdorner();
                    }
                }
                catch (Exception ex)
                {
                    _logger.TraceEvent(TraceEventType.Warning, 0, $"Failed to create adorner, disabling visualization: {ex.Message}");
                    _visualizationEnabled = false;
                    CleanupAdorner();
                }
            }
        }

        private void OnGesturePointAdded(object sender, Point point)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _recognitionService.AddPoint(point);

            if (_adornerService != null)
            {
                try
                {
                    _adornerService.AddPoint(point);
                }
                catch (Exception ex)
                {
                    _logger.TraceEvent(TraceEventType.Warning, 0, $"Adorner AddPoint error: {ex.Message}");
                }
            }

            // Show directions in both modes
            if (_visualSettings.ShowDirections || _isRecordingMode)
            {
                var currentDirections = _recognitionService.RecognizeDirections();
                if (_adornerService != null)
                {
                    try
                    {
                        _adornerService.UpdateDirections(currentDirections);

                        // Check if current pattern matches any gesture
                        if (currentDirections.Count > 0)
                        {
                            var matchingGesture = _gestureManager.FindMatchingGesture(currentDirections);
                            if (matchingGesture != null)
                            {
                                _adornerService.UpdateMatchedCommand(matchingGesture.Name);
                            }
                            else
                            {
                                _adornerService.UpdateMatchedCommand(null);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.TraceEvent(TraceEventType.Warning, 0, $"Adorner UpdateDirections error: {ex.Message}");
                    }
                }
            }
        }

        private void OnGestureEnded(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var detectedPattern = _recognitionService.EndGesture();

            _logger.TraceEvent(TraceEventType.Information, 0,
                $"Gesture ended with {detectedPattern.Count} directions: {string.Join(", ", detectedPattern)}");

            // Clean up adorner IMMEDIATELY
            CleanupAdorner();

            if (_isRecordingMode)
            {
                // Recording mode - return pattern to callback
                _logger.TraceEvent(TraceEventType.Information, 0,
                    $"Gesture recorded: {string.Join(", ", detectedPattern)}");

                var callback = _recordingCallback;
                _recordingCallback = null;
                _isRecordingMode = false;

                // Reset mouse hook state
                _mouseHook.ResetGestureState();

                // Invoke callback after cleanup
                callback?.Invoke(detectedPattern);
            }
            else if (detectedPattern.Count > 0)
            {
                // Normal mode - execute matching gesture
                var matchingGesture = _gestureManager.FindMatchingGesture(detectedPattern);

                if (matchingGesture != null)
                {
                    _logger.TraceEvent(TraceEventType.Information, 0,
                        $"Gesture matched: {matchingGesture.Name}");

                    // Tell hook to block context menu for this matched gesture
                    _mouseHook.SetShouldBlockContextMenu(true);

                    ExecuteVsCommand(matchingGesture.VsCommandId);

                    // Reset mouse hook state
                    _mouseHook.ResetGestureState();
                }
                else
                {
                    _logger.TraceEvent(TraceEventType.Information, 0,
                        $"No matching gesture for pattern: {string.Join(", ", detectedPattern)}");

                    // Tell hook to block context menu for this matched gesture
                    _mouseHook.SetShouldBlockContextMenu(true);
                }
            }
        }

        private void OnRightClickDetected(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Ensure adorner is cleaned up even on right-click
            CleanupAdorner();

            _logger.TraceEvent(TraceEventType.Verbose, 0, "Right click detected (no gesture)");
        }

        private void CleanupAdorner()
        {
            if (_adornerService != null)
            {
                try
                {
                    _adornerService.EndGesture();
                    _adornerService.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.TraceEvent(TraceEventType.Warning, 0, $"Adorner cleanup error: {ex.Message}");
                }
                finally
                {
                    _adornerService = null;
                }
            }
        }

        private void ExecuteVsCommand(string commandId)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                dte?.ExecuteCommand(commandId);
                _logger.TraceEvent(TraceEventType.Information, 0, $"Executed command: {commandId}");
            }
            catch (Exception ex)
            {
                _logger.TraceEvent(TraceEventType.Error, 0, $"Failed to execute command {commandId}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Stop();
            CleanupAdorner();
        }
    }
}