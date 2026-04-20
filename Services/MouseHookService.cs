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
        private bool _cachedIsVsWindow;
        private long _isVsWindowCacheExpiry;
        private bool _isGestureActive;
        private bool _isRecordingMode;
        private bool _isSyntheticEvent = false;      // Track synthetic RBUTTONUP (post-gesture cleanup)
        private bool _isSyntheticRightClick = false;   // Track synthetic right-click (no-gesture case)
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
            if (nCode < 0)
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

            // ── Fast-path: WM_MOUSEMOVE ──────────────────────────────────────────────
            // Fires hundreds of times per second. Skip IsVisualStudioWindow() entirely
            // and only do the minimum work needed to track gesture points.
            if (wParam == (IntPtr)NativeMethods.WM_MOUSEMOVE)
            {
                if (_isRightButtonDown)
                {
                    var ms = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    HandleMouseMove(new Point(ms.pt.x, ms.pt.y));
                }

                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // ── Fast-path: LButton ───────────────────────────────────────────────────
            if (wParam == (IntPtr)NativeMethods.WM_LBUTTONDOWN)
            {
                if (_isGestureActive)
                {
                    _logger.TraceEvent(TraceEventType.Verbose, 0, "Blocking WM_LBUTTONDOWN - gesture active");
                    return (IntPtr)1;
                }

                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            if (wParam == (IntPtr)NativeMethods.WM_LBUTTONUP)
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

            // ── RButton events only below this point ─────────────────────────────────
            if (wParam != (IntPtr)NativeMethods.WM_RBUTTONDOWN &&
                wParam != (IntPtr)NativeMethods.WM_RBUTTONUP)
            {
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            bool isInjected = (hookStruct.flags & NativeMethods.LLMHF_INJECTED) != 0;

            // ── WM_RBUTTONUP ─────────────────────────────────────────────────────────
            if (wParam == (IntPtr)NativeMethods.WM_RBUTTONUP)
            {
                // Synthetic right-click UP (no-gesture) — let through
                if (_isSyntheticRightClick && isInjected)
                {
                    _isSyntheticRightClick = false;
                    _logger.TraceEvent(TraceEventType.Verbose, 0, "Allowing synthetic WM_RBUTTONUP (right-click) through");

                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                if (!_isRightButtonDown)
                {
                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                // Synthetic UP sent to keep Windows state consistent after blocked gesture
                if (_isSyntheticEvent && isInjected)
                {
                    _isSyntheticEvent = false;
                    _logger.TraceEvent(TraceEventType.Verbose, 0, "Allowing synthetic WM_RBUTTONUP through");

                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                bool wasRecordingMode = _isRecordingMode;
                bool wasGestureActive = _isGestureActive;

                _logger.TraceEvent(TraceEventType.Verbose, 0,
                    $"WM_RBUTTONUP: gesture={wasGestureActive}, recording={wasRecordingMode}");

                HandleRightButtonUp();

                if (wasGestureActive && !wasRecordingMode && IsVisualStudioWindow())
                {
                    _logger.TraceEvent(TraceEventType.Verbose, 0, "Blocking WM_RBUTTONUP after gesture, sending synthetic event");
                    SendSyntheticRightButtonUp();
                    return (IntPtr)1;
                }

                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // ── WM_RBUTTONDOWN ───────────────────────────────────────────────────────
            // Synthetic right-click DOWN (no-gesture) — let through
            if (_isSyntheticRightClick && isInjected)
            {
                _logger.TraceEvent(TraceEventType.Verbose, 0, "Allowing synthetic WM_RBUTTONDOWN (right-click) through");

                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // Only intercept when VS is the foreground window
            if (!IsVisualStudioWindow())
            {
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            HandleRightButtonDown(new Point(hookStruct.pt.x, hookStruct.pt.y));

            // Always block real WM_RBUTTONDOWN to prevent VS text cursor repositioning
            _logger.TraceEvent(TraceEventType.Verbose, 0, "Blocking WM_RBUTTONDOWN to prevent cursor repositioning");

            return (IntPtr)1;
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

        private void SendSyntheticRightClick()
        {
            _isSyntheticRightClick = true;

            var inputs = new[]
            {
                new NativeMethods.INPUT
                {
                    type = NativeMethods.INPUT_MOUSE,
                    u = new NativeMethods.InputUnion
                    {
                        mi = new NativeMethods.MOUSEINPUT
                        {
                            dwFlags = NativeMethods.MOUSEEVENTF_RIGHTDOWN
                        }
                    }
                },
                new NativeMethods.INPUT
                {
                    type = NativeMethods.INPUT_MOUSE,
                    u = new NativeMethods.InputUnion
                    {
                        mi = new NativeMethods.MOUSEINPUT
                        {
                            dwFlags = NativeMethods.MOUSEEVENTF_RIGHTUP
                        }
                    }
                }
            };

            NativeMethods.SendInput(2, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
            _logger.TraceEvent(TraceEventType.Verbose, 0, "Sent synthetic right-click (DOWN+UP)");
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
                // No gesture — send synthetic right-click so context menu still appears
                if (_gestureStartPoint.HasValue)
                    SendSyntheticRightClick();

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

                // Throttle to ~60fps to avoid flooding the UI thread during heavy VS operations
                // (build, solution load). Any mouse moves that arrive during this delay are
                // coalesced – only the latest point is dispatched on the next iteration.
                await Task.Delay(16);
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
            long now = Stopwatch.GetTimestamp();
            if (now < _isVsWindowCacheExpiry)
                return _cachedIsVsWindow;

            IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                _cachedIsVsWindow = false;
            }
            else
            {
                NativeMethods.GetWindowThreadProcessId(foregroundWindow, out uint processId);
                _cachedIsVsWindow = processId == _currentProcessId;
            }

            // Cache result for 100 ms to avoid repeated WinAPI calls on every hook event
            _isVsWindowCacheExpiry = now + (Stopwatch.Frequency / 10);
            return _cachedIsVsWindow;
        }

        // Method no longer needed - always block gestures
        public void SetShouldBlockContextMenu(bool shouldBlock)
        {
            // Kept for compatibility, does nothing
            _logger.TraceEvent(TraceEventType.Verbose, 0, $"SetShouldBlockContextMenu called (ignored)");
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopHook();
                _hookCallback = null; // Allow GC to collect after unhook
            }
        }
    }
}