using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace VSILViewer
{
    internal sealed class ILViewerCommand
    {
        public const int CommandId = 0x0100;
        public static readonly Guid CommandSet = new Guid("e4c5f6a7-8901-2345-6789-abcdef012345");

        private readonly AsyncPackage _package;

        private ILViewerCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandId = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandId);
            commandService.AddCommand(menuItem);
        }

        public static ILViewerCommand? Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                // Menu command service unavailable - extension won't have menu item
                return;
            }

            Instance = new ILViewerCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);

                try
                {
                    var window = await _package.ShowToolWindowAsync(
                        typeof(ILViewerToolWindow),
                        0,
                        create: true,
                        _package.DisposalToken);

                    if (window?.Frame is IVsWindowFrame frame)
                    {
                        Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
                    }

                    if (window is ILViewerToolWindow toolWindow)
                    {
                        await toolWindow.Control.InitializeAsync(_package);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Package is being disposed
                }
            });
        }
    }
}
