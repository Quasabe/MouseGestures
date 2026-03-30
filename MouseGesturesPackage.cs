using Microsoft.VisualStudio.Shell;
using MouseGestures.Commands;
using MouseGestures.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace MouseGestures
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(MouseGesturesPackage.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class MouseGesturesPackage : AsyncPackage
    {
        /// <summary>
        /// MouseGesturesPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "94562305-f936-4b56-b160-176b58df9fea";

        private GestureManagerService _gestureManager;
        private GestureOrchestratorService _orchestrator;

        #region Package Members

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to monitor for initialization cancellation, which can occur when VS is shutting down.</param>
        /// <param name="progress">A provider for progress updates.</param>
        /// <returns>A task representing the async work of package initialization, or an already completed task if there is none. Do not return null from this method.</returns>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // When initialized asynchronously, the current thread may be a background thread at this point.
            // Do any initialization that requires the UI thread after switching to the UI thread.
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Initialize services
            var logger = new TraceSource("MouseGestures")
            {
                Switch = new SourceSwitch("MouseGesturesSwitch", "All") // alebo "All"
            };

            // Add file listener
            var logPath = Path.Combine(
                "C:\\Users\\PeterJanák\\Downloads\\",
                "MouseGestures",
                "trace.log");

            Directory.CreateDirectory(Path.GetDirectoryName(logPath));

            var fileListener = new TextWriterTraceListener(logPath);
            fileListener.TraceOutputOptions = TraceOptions.DateTime | TraceOptions.ThreadId;
            logger.Listeners.Add(fileListener);

            // Also add console listener for Output window
            logger.Listeners.Add(new DefaultTraceListener());

            _gestureManager = new GestureManagerService();
            await _gestureManager.LoadGesturesAsync();

            var recognitionService = new GestureRecognitionService(logger);
            var mouseHook = new MouseHookService(logger);

            _orchestrator = new GestureOrchestratorService(
                mouseHook,
                recognitionService,
                _gestureManager,
                logger);

            // Start gesture detection
            _orchestrator.Start();

            // Register commands
            await OpenGestureSettingsCommand.InitializeAsync(this, _gestureManager, _orchestrator);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _orchestrator?.Stop();
                _orchestrator?.Dispose();

                // Flush log listeners
                Trace.Flush();
            }

            base.Dispose(disposing);
        }

        #endregion Package Members
    }
}