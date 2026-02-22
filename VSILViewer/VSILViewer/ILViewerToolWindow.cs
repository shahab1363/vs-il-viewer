using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace VSILViewer
{
    [Guid("d3b3e8a1-5678-4abc-9def-0123456789ab")]
    public class ILViewerToolWindow : ToolWindowPane
    {
        public ILViewerToolWindow() : base(null)
        {
            Caption = "IL Viewer";
            Content = new ILViewerControl();
        }

        public ILViewerControl Control => (ILViewerControl)Content;

        protected override void Initialize()
        {
            base.Initialize();

            // When VS restores this tool window from layout, Package is available here.
            // Trigger async initialization so the control works without needing to close/reopen.
            if (Package is AsyncPackage asyncPackage)
            {
                _ = asyncPackage.JoinableTaskFactory.RunAsync(async () =>
                {
                    await Control.InitializeAsync(asyncPackage);
                });
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Control?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
