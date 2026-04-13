using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using MouseGestures.Services;
using MouseGestures.UI;
using MouseGestures.ViewModels;
using Task = System.Threading.Tasks.Task;

namespace MouseGestures.Commands
{
    /// <summary>
    /// Command handler for opening gesture settings.
    /// </summary>
    internal sealed class OpenGestureSettingsCommand
    {
        public const int CommandId = 0x0100;
        public static readonly Guid CommandSet = new Guid("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d");

        private readonly AsyncPackage _package;
        private readonly GestureManagerService _gestureManager;
        private readonly GestureOrchestratorService _orchestrator;

        private OpenGestureSettingsCommand(
            AsyncPackage package,
            IMenuCommandService commandService,
            GestureManagerService gestureManager,
            GestureOrchestratorService orchestrator)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _gestureManager = gestureManager ?? throw new ArgumentNullException(nameof(gestureManager));
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new OleMenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static OpenGestureSettingsCommand Instance { get; private set; }

        private IAsyncServiceProvider ServiceProvider => _package;

        public static async Task InitializeAsync(
            AsyncPackage package,
            GestureManagerService gestureManager,
            GestureOrchestratorService orchestrator)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as IMenuCommandService;
            Instance = new OpenGestureSettingsCommand(package, commandService, gestureManager, orchestrator);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Tell orchestrator that settings window is opening
            _orchestrator.SetSettingsWindowOpen(true);

            try
            {
                var viewModel = new GestureSettingsViewModel(_gestureManager, _orchestrator);
                var window = new GestureSettingsWindow(viewModel);

                window.ShowDialog();
            }
            finally
            {
                // Always reset when window closes (even if exception occurs)
                _orchestrator.SetSettingsWindowOpen(false);
            }
        }
    }
}