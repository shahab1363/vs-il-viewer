using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace VSILViewer.Editor
{
    [Export]
    public class ILTextViewFactory
    {
        [Import]
        private ITextBufferFactoryService TextBufferFactory { get; set; } = null!;

        [Import]
        private ITextEditorFactoryService TextEditorFactory { get; set; } = null!;

        [Import]
        private IContentTypeRegistryService ContentTypeRegistry { get; set; } = null!;

        public IWpfTextViewHost CreateTextView(bool isCSharp)
        {
            // Get the appropriate content type
            // For C#, use "CSharp" which has Roslyn classifiers registered
            // For IL/text, use "text"
            var contentTypeName = isCSharp ? "CSharp" : "text";
            var contentType = ContentTypeRegistry.GetContentType(contentTypeName);

            if (contentType == null)
            {
                // Fallback to text
                contentType = ContentTypeRegistry.GetContentType("text")!;
            }

            // Create text buffer with the content type
            var textBuffer = TextBufferFactory.CreateTextBuffer("", contentType);

            // Create text view with appropriate roles
            // These roles are important for getting proper editor behavior
            var roles = TextEditorFactory.CreateTextViewRoleSet(
                PredefinedTextViewRoles.Document,
                PredefinedTextViewRoles.Editable,
                PredefinedTextViewRoles.Interactive,
                PredefinedTextViewRoles.Structured,
                PredefinedTextViewRoles.Analyzable);

            // Create the text view
            var textView = TextEditorFactory.CreateTextView(textBuffer, roles);

            // Create text view host
            var textViewHost = TextEditorFactory.CreateTextViewHost(textView, setFocus: false);

            // Configure the view
            textView.Options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, true);

            return textViewHost;
        }
    }
}
