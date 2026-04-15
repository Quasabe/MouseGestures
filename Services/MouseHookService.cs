using Microsoft.VisualStudio.Shell;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace MouseGestures.Services
{
    /// <summary>
    /// Service for capturing low-level mouse events.
    /// </summary>
    public class MouseHookService : IDisposable
    {
        private readonly TraceSource _logger;
        private readonly uint _currentProcessId;
        private IntPtr _hookId = IntPtr.Zero;
        private NativeMethods.LowLevelMouseProc _hookCallback; // Keep strong reference
        private Point? _gestureStartPoint;
        private bool _isRightButtonDown;
        private bool _isGestureActive;
        private bool _isRecordingMode;
        private bool _isSyntheticEvent = false; // Track synthetic events
        private readonly object _gesturePointDispatchLock = new object();
        private Point _latestGesturePoint;
        private bool _hasLatestGesturePoint;
        private bool _isGesturePointDispatchScheduled;

        public event EventHandler<Point> GestureStarted;

        public event EventHandler<Point> GesturePointAdded;

        public event EventHandler GestureEnded;

        public event EventHandler RightClickDetected;

        public bool IsHookActive => _hookId != IntPtr.Zero;

        public MouseHookService(TraceSource logger)
        {
            _logger = logger;
            _currentProcessId = (uint)Process.GetCurrentProcess().Id;
        }

        public void SetRecordingMode(bool isRecording)
        {
            _isRecordingMode = isRecording;
            _logger.TraceEvent(TraceEventType.Information, 0, $"Recording mode set to: {isRecording}");
        }

        public void StartHook()
        {
            if (_hookId != IntPtr.Zero)
            {
                _logger.TraceEvent(TraceEventType.Warning, 0, "Hook already active");
                return;
            }

            // Store delegate in member field to prevent GC collection
            _hookCallback = HookCallback;

            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                if (curModule != null)
                {
                    _hookId = NativeMethods.SetWindowsHookEx(
                        NativeMethods.WH_MOUSE_LL,
                        _hookCallback, // Use stored delegate
                        NativeMethods.GetModuleHandle(curModule.ModuleName),
                        0);
                }
            }

            if (_hookId == IntPtr.Zero)
            {
                _logger.TraceEvent(TraceEventType.Error, 0, "Failed to set mouse hook");
            }
            else
            {
                _logger.TraceEvent(TraceEventType.Information, 0, "Mouse hook activated");
            }
        }

        public void StopHook()
        {
            if (_hookId != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                _logger.TraceEvent(TraceEventType.Information, 0, "Mouse hook deactivated");
            }
        }

        public void ResetGestureState()
        {
            _isRightButtonDown = false;
            _isGestureActive = false;
            _gestureStartPoint = null;
            ClearPendingGesturePointDispatch();
            _logger.TraceEvent(TraceEventType.Information, 0, "Gesture state reset");
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                bool isVsWindow = IsVisualStudioWindow();
                if (isVsWindow)
                {
                    var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    bool isInjected = (hookStruct.flags & NativeMethods.LLMHF_INJECTED) != 0;

                    // Handle WM_RBUTTONUP
                    if (wParam == (IntPtr)NativeMethods.WM_RBUTTONUP && _isRightButtonDown)
                    {
                        // If this is our synthetic event, let it pass through
                        if (_isSyntheticEvent && isInjected)
                        {
                            _isSyntheticEvent = false;
                            _logger.TraceEvent(TraceEventType.Verbose, 0, "Allowing synthetic WM_RBUTTONUP through");
                            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                        }

                        bool wasRecordingMode = _isRecordingMode;
                        bool wasGestureActive = _isGestureActive;

                        _logger.TraceEvent(TraceEventType.Verbose, 0,
                            $"WM_RBUTTONUP detected, VS active: {isVsWindow}, gesture active: {wasGestureActive}, recording: {wasRecordingMode}");

                        HandleRightButtonUp();

                        // Block WM_RBUTTONUP if gesture was active (to prevent context menu)
                        // Then send synthetic event to keep Windows mouse state correct
                        if (isVsWindow && wasGestureActive && !wasRecordingMode)
                        {
                            _logger.TraceEvent(TraceEventType.Verbose, 0, "Blocking WM_RBUTTONUP after gesture, sending synthetic event");

                            // Send synthetic RBUTTONUP to keep Windows state consistent
                            SendSyntheticRightButtonUp();

                            return (IntPtr)1; // Block original event
                        }

                        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    // For other events, only process if VS is active window
                    if (!isVsWindow)
                    {
                        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    var currentPoint = new Point(hookStruct.pt.x, hookStruct.pt.y);

                    try
                    {
                        if (wParam == (IntPtr)NativeMethods.WM_RBUTTONDOWN)
                        {
                            HandleRightButtonDown(currentPoint);

                            // Block right button down if gesture is already active
                            if (_isGestureActive)
                            {
                                _logger.TraceEvent(TraceEventType.Verbose, 0, "Blocking WM_RBUTTONDOWN - gesture active");
                                return (IntPtr)1;
                            }
                        }
                        else if (wParam == (IntPtr)NativeMethods.WM_MOUSEMOVE && _isRightButtonDown)
                        {
                            // DO NOT block WM_MOUSEMOVE - let the cursor move freely
                            HandleMouseMove(currentPoint);
                        }
                        else if (wParam == (IntPtr)NativeMethods.WM_LBUTTONDOWN && _isGestureActive)
                        {
                            // Block left button clicks during gesture to prevent text selection/cursor repositioning
                            _logger.TraceEvent(TraceEventType.Verbose, 0, "Blocking WM_LBUTTONDOWN - gesture active");
                            return (IntPtr)1;
                        }
                        else if (wParam == (IntPtr)NativeMethods.WM_LBUTTONUP && _isGestureActive)
                        {
                            // Don't block LBUTTONUP - prevents stuck button state
                            _logger.TraceEvent(TraceEventType.Verbose, 0, "Allowing WM_LBUTTONUP through");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.TraceEvent(TraceEventType.Error, 0, $"Error in hook callback: {ex.Message}");
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private void SendSyntheticRightButtonUp()
        {
            _isSyntheticEvent = true;

            var input = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = 0,
                        dy = 0,
                        mouseData = 0,
                        dwFlags = NativeMethods.MOUSEEVENTF_RIGHTUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
            _logger.TraceEvent(TraceEventType.Verbose, 0, "Sent synthetic RBUTTONUP");
        }

        private void HandleRightButtonDown(Point point)
        {
            _isRightButtonDown = true;
            _gestureStartPoint = point;
            _isGestureActive = false;
            ClearPendingGesturePointDispatch();
            _logger.TraceEvent(TraceEventType.Verbose, 0, $"Right button down at {point}");
        }

        private void HandleMouseMove(Point currentPoint)
        {
            if (!_isGestureActive && _gestureStartPoint.HasValue)
            {
                double distance = Utils.Utils.GetDistance(_gestureStartPoint.Value, currentPoint);
                if (distance >= 5)
                {
                    _isGestureActive = true;
                    // Fire-and-forget - don't block hook thread
                    _ = FireEventAsync(() => GestureStarted?.Invoke(this, _gestureStartPoint.Value));
                    _logger.TraceEvent(TraceEventType.Information, 0, "Gesture started");
                }
            }
            else if (_isGestureActive)
            {
                QueueLatestGesturePoint(currentPoint);
            }
        }

        private void HandleRightButtonUp()
        {
            _logger.TraceEvent(TraceEventType.Verbose, 0, $"Right button up, gesture active: {_isGestureActive}");

            if (_isGestureActive)
            {
                // Fire-and-forget - don't block hook thread
                _ = FireEventAsync(() => GestureEnded?.Invoke(this, EventArgs.Empty));
            }
            else
            {
                // Fire-and-forget - don't block hook thread
                _ = FireEventAsync(() => RightClickDetected?.Invoke(this, EventArgs.Empty));
            }

            _isRightButtonDown = false;
            _isGestureActive = false;
            ClearPendingGesturePointDispatch();
            _logger.TraceEvent(TraceEventType.Information, 0, "STATE RESET: _isGestureActive=false, _isRightButtonDown=false");
        }

        private void QueueLatestGesturePoint(Point currentPoint)
        {
            bool shouldStartDispatch = false;

            lock (_gesturePointDispatchLock)
            {
                _latestGesturePoint = currentPoint;
                _hasLatestGesturePoint = true;

                if (!_isGesturePointDispatchScheduled)
                {
                    _isGesturePointDispatchScheduled = true;
                    shouldStartDispatch = true;
                }
            }

            if (shouldStartDispatch)
            {
                _ = DispatchGesturePointsAsync();
            }
        }

        private async Task DispatchGesturePointsAsync()
        {
            while (true)
            {
                Point pointToDispatch;

                lock (_gesturePointDispatchLock)
                {
                    if (!_hasLatestGesturePoint)
                    {
                        _isGesturePointDispatchScheduled = false;
                        return;
                    }

                    pointToDispatch = _latestGesturePoint;
                    _hasLatestGesturePoint = false;
                }

                await FireEventAsync(() => GesturePointAdded?.Invoke(this, pointToDispatch));
            }
        }

        private void ClearPendingGesturePointDispatch()
        {
            lock (_gesturePointDispatchLock)
            {
                _hasLatestGesturePoint = false;
            }
        }

        private async Task FireEventAsync(Action eventInvoker)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                eventInvoker?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.TraceEvent(TraceEventType.Error, 0, $"Error firing event: {ex.Message}");
            }
        }

        private bool IsVisualStudioWindow()
        {
            IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
                return false;

            NativeMethods.GetWindowThreadProcessId(foregroundWindow, out uint processId);
            return processId == _currentProcessId;
        }

        // Method no longer needed - always block gestures
        public void SetShouldBlockContextMenu(bool shouldBlock)
        {
            // Kept for compatibility, does nothing
            _logger.TraceEvent(TraceEventType.Verbose, 0, $"SetShouldBlockContextMenu called (ignored)");
        }

        public void Dispose()
        {
            StopHook();
            _hookCallback = null; // Allow GC to collect after unhook
        }
    }
}