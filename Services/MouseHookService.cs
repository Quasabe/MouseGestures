using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MouseGestures.Services
{
    /// <summary>
    /// Service for capturing low-level mouse events.
    /// </summary>
    public class MouseHookService : IDisposable
    {
        private const int GestureDispatchIntervalMs = 8;
        private const int MaxQueuedGesturePoints = 2048;
        private const int MaxPointsPerDispatchTick = 256;

        private readonly TraceSource _logger;
        private readonly uint _currentProcessId;
        private readonly ConcurrentQueue<Point> _queuedGesturePoints = new ConcurrentQueue<Point>();
        private readonly ManualResetEventSlim _hookThreadReady = new ManualResetEventSlim(false);

        private IntPtr _hookId = IntPtr.Zero;
        private NativeMethods.LowLevelMouseProc _hookCallback; // Keep strong reference
        private Thread _hookThread;
        private int _hookThreadId;
        private bool _hookStartupSucceeded;

        private Point? _gestureStartPoint;
        private bool _isRightButtonDown;
        private bool _cachedIsVsWindow;
        private long _isVsWindowCacheExpiry;
        private bool _isGestureActive;
        private bool _isRecordingMode;
        private bool _isSyntheticEvent = false;      // Track synthetic RBUTTONUP (post-gesture cleanup)
        private bool _isSyntheticRightClick = false;   // Track synthetic right-click (no-gesture case)

        private DispatcherTimer _gesturePointDispatchTimer;
        private int _queuedGesturePointCount;

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
            if (_hookThread != null)
            {
                _logger.TraceEvent(TraceEventType.Warning, 0, "Hook already active");
                return;
            }

            // Store delegate in member field to prevent GC collection
            EnsureGesturePointDispatchTimerStarted();

            _hookThreadReady.Reset();
            _hookStartupSucceeded = false;

            _hookThread = new Thread(HookThreadProc)
            {
                IsBackground = true,
                Name = "MouseGestures.MouseHookThread"
            };

            _hookThread.Start();

            if (!_hookThreadReady.Wait(TimeSpan.FromSeconds(3)) || !_hookStartupSucceeded)
            {
                _logger.TraceEvent(TraceEventType.Error, 0, "Failed to set mouse hook on dedicated hook thread");
                StopHook();
                return;
            }

            _logger.TraceEvent(TraceEventType.Information, 0, "Mouse hook activated (dedicated hook thread)");
        }

        public void StopHook()
        {
            var thread = _hookThread;
            if (thread == null)
            {
                StopGesturePointDispatchTimer();
                return;
            }

            try
            {
                if (_hookThreadId != 0)
                {
                    NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                }

                if (!thread.Join(TimeSpan.FromSeconds(2)))
                {
                    _logger.TraceEvent(TraceEventType.Warning, 0, "Hook thread did not exit in time");
                }
            }
            finally
            {
                _hookThread = null;
                _hookThreadId = 0;
                _hookId = IntPtr.Zero;
                ClearQueuedGesturePoints();
                StopGesturePointDispatchTimer();
                _logger.TraceEvent(TraceEventType.Information, 0, "Mouse hook deactivated");
            }
        }

        public void ResetGestureState()
        {
            _isRightButtonDown = false;
            _isGestureActive = false;
            _gestureStartPoint = null;
            ClearQueuedGesturePoints();
            _logger.TraceEvent(TraceEventType.Information, 0, "Gesture state reset");
        }

        private void HookThreadProc()
        {
            try
            {
                _hookThreadId = (int)NativeMethods.GetCurrentThreadId();
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

                _hookStartupSucceeded = _hookId != IntPtr.Zero;
                _hookThreadReady.Set();

                if (!_hookStartupSucceeded)
                    return;

                while (NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                _logger.TraceEvent(TraceEventType.Error, 0, $"Hook thread failure: {ex.Message}");
                _hookStartupSucceeded = false;
                _hookThreadReady.Set();
            }
            finally
            {
                if (_hookId != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(_hookId);
                    _hookId = IntPtr.Zero;
                }
            }
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
                    if (!IsVisualStudioWindow())
                    {
                        CancelGestureDueToInactiveVsWindow();
                        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

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

                if (_isSyntheticEvent && isInjected)
                {
                    _isSyntheticEvent = false;
                    _logger.TraceEvent(TraceEventType.Verbose, 0, "Allowing synthetic WM_RBUTTONUP through");
                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                if (!_isRightButtonDown)
                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

                if (!IsVisualStudioWindow())
                {
                    CancelGestureDueToInactiveVsWindow();
                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                bool wasRecordingMode = _isRecordingMode;
                bool wasGestureActive = _isGestureActive;

                _logger.TraceEvent(TraceEventType.Verbose, 0,
                    $"WM_RBUTTONUP: gesture={wasGestureActive}, recording={wasRecordingMode}");

                HandleRightButtonUp();

                if (wasGestureActive && !wasRecordingMode)
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
            ClearQueuedGesturePoints();
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
                QueueGesturePoint(currentPoint);
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
            _gestureStartPoint = null;
            ClearQueuedGesturePoints();
            _logger.TraceEvent(TraceEventType.Information, 0, "STATE RESET: _isGestureActive=false, _isRightButtonDown=false");
        }

        private void CancelGestureDueToInactiveVsWindow()
        {
            if (!_isRightButtonDown && !_isGestureActive)
                return;

            bool hadActiveGesture = _isGestureActive;

            _isRightButtonDown = false;
            _isGestureActive = false;
            _gestureStartPoint = null;
            ClearQueuedGesturePoints();

            if (hadActiveGesture)
            {
                _ = FireEventAsync(() => RightClickDetected?.Invoke(this, EventArgs.Empty));
            }

            _logger.TraceEvent(TraceEventType.Verbose, 0, "Gesture capture stopped - Visual Studio window is not active");
        }

        private void QueueGesturePoint(Point point)
        {
            if (Volatile.Read(ref _queuedGesturePointCount) >= MaxQueuedGesturePoints && _queuedGesturePoints.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _queuedGesturePointCount);
            }

            _queuedGesturePoints.Enqueue(point);
            Interlocked.Increment(ref _queuedGesturePointCount);
        }

        private void EnsureGesturePointDispatchTimerStarted()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                _logger.TraceEvent(TraceEventType.Warning, 0, "Application dispatcher unavailable - gesture point batching timer not started");
                return;
            }

            Action startTimer = () =>
            {
                if (_gesturePointDispatchTimer == null)
                {
                    _gesturePointDispatchTimer = new DispatcherTimer(DispatcherPriority.Render)
                    {
                        Interval = TimeSpan.FromMilliseconds(GestureDispatchIntervalMs)
                    };
                    _gesturePointDispatchTimer.Tick += GesturePointDispatchTimer_Tick;
                }

                if (!_gesturePointDispatchTimer.IsEnabled)
                {
                    _gesturePointDispatchTimer.Start();
                }
            };

            if (dispatcher.CheckAccess())
                startTimer();
            else
                dispatcher.BeginInvoke(startTimer);
        }

        private void StopGesturePointDispatchTimer()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || _gesturePointDispatchTimer == null)
                return;

            Action stopTimer = () =>
            {
                if (_gesturePointDispatchTimer != null)
                {
                    _gesturePointDispatchTimer.Stop();
                    _gesturePointDispatchTimer.Tick -= GesturePointDispatchTimer_Tick;
                    _gesturePointDispatchTimer = null;
                }
            };

            if (dispatcher.CheckAccess())
                stopTimer();
            else
                dispatcher.BeginInvoke(stopTimer);
        }

        private void GesturePointDispatchTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                int processed = 0;
                while (processed < MaxPointsPerDispatchTick && _queuedGesturePoints.TryDequeue(out var point))
                {
                    Interlocked.Decrement(ref _queuedGesturePointCount);
                    GesturePointAdded?.Invoke(this, point);
                    processed++;
                }
            }
            catch (Exception ex)
            {
                _logger.TraceEvent(TraceEventType.Error, 0, $"Error dispatching gesture points: {ex.Message}");
            }
        }

        private void ClearQueuedGesturePoints()
        {
            while (_queuedGesturePoints.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _queuedGesturePointCount);
            }

            Interlocked.Exchange(ref _queuedGesturePointCount, 0);
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
                _hookCallback = null;
                _hookThreadReady.Dispose();
            }
        }
    }
}