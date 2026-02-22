using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;

namespace VSILViewer
{
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("CSharp")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal class TextViewCreationListener : IVsTextViewCreationListener
    {
        [Import]
        internal IVsEditorAdaptersFactoryService? EditorAdaptersFactory { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            if (EditorAdaptersFactory == null) return;

            var wpfTextView = EditorAdaptersFactory.GetWpfTextView(textViewAdapter);
            if (wpfTextView == null) return;

            // Store reference for the tool window to pick up
            wpfTextView.Properties.GetOrCreateSingletonProperty(
                typeof(TextViewCreationListener),
                () => wpfTextView);
        }
    }
}
