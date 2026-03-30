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
        private bool _shouldBlockContextMenu = false;

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
            _logger.TraceEvent(TraceEventType.Information, 0, "Gesture state reset");
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                bool isVsWindow = IsVisualStudioWindow();

                // Always handle WM_RBUTTONUP if we have an active gesture
                if (wParam == (IntPtr)NativeMethods.WM_RBUTTONUP && _isRightButtonDown)
                {
                    bool wasRecordingMode = _isRecordingMode;
                    bool wasGestureActive = _isGestureActive;

                    _logger.TraceEvent(TraceEventType.Verbose, 0,
                        $"WM_RBUTTONUP detected, VS active: {isVsWindow}, gesture active: {wasGestureActive}, recording: {wasRecordingMode}");

                    HandleRightButtonUp();

                    // Block WM_RBUTTONUP only if gesture was active AND we should block context menu
                    // This prevents context menu from appearing
                    if (isVsWindow && wasGestureActive && !wasRecordingMode && _shouldBlockContextMenu)
                    {
                        _logger.TraceEvent(TraceEventType.Verbose, 0, "Blocking WM_RBUTTONUP to prevent context menu");
                        _shouldBlockContextMenu = false; // Reset flag
                        return (IntPtr)1;
                    }

                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                // For other events, only process if VS is active window
                if (!isVsWindow)
                {
                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var currentPoint = new Point(hookStruct.pt.x, hookStruct.pt.y);

                try
                {
                    if (wParam == (IntPtr)NativeMethods.WM_RBUTTONDOWN)
                    {
                        HandleRightButtonDown(currentPoint);

                        // Block right button down if gesture is already active
                        // (shouldn't happen, but safety check)
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
                        // Don't block LBUTTONUP either - same reason as RBUTTONUP
                        _logger.TraceEvent(TraceEventType.Verbose, 0, "Allowing WM_LBUTTONUP through");
                    }
                }
                catch (Exception ex)
                {
                    _logger.TraceEvent(TraceEventType.Error, 0, $"Error in hook callback: {ex.Message}");
                }
            }

            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private void HandleRightButtonDown(Point point)
        {
            _isRightButtonDown = true;
            _gestureStartPoint = point;
            _isGestureActive = false;
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
                // Fire-and-forget - don't block hook thread
                _ = FireEventAsync(() => GesturePointAdded?.Invoke(this, currentPoint));
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
            _logger.TraceEvent(TraceEventType.Information, 0, "STATE RESET: _isGestureActive=false, _isRightButtonDown=false");
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

        public void SetShouldBlockContextMenu(bool shouldBlock)
        {
            _shouldBlockContextMenu = shouldBlock;
            _logger.TraceEvent(TraceEventType.Verbose, 0, $"Context menu blocking set to: {shouldBlock}");
        }

        public void Dispose()
        {
            StopHook();
            _hookCallback = null; // Allow GC to collect after unhook
        }
    }
}