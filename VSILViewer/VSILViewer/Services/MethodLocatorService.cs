using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Text;
using VSILViewer.Models;

namespace VSILViewer.Services
{
    public class MethodLocatorService
    {
        public async Task<(Document? Document, MethodViewInfo? MethodInfo)> GetMethodAtCaretAsync(
            IWpfTextView textView,
            VisualStudioWorkspace? workspace)
        {
            if (workspace == null || textView == null)
                return (null, null);

            // Get document from text view
            if (!textView.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDocument))
                return (null, null);

            var filePath = textDocument?.FilePath;
            if (string.IsNullOrEmpty(filePath))
                return (null, null);

            var document = workspace.CurrentSolution.GetDocumentIdsWithFilePath(filePath).Select(id => workspace.CurrentSolution.GetDocument(id)).FirstOrDefault();
            if (document == null)
                return (null, null);

            var position = textView.Caret.Position.BufferPosition.Position;

            // Get method with full signature
            var (typeName, methodName, fullSignature) = await GetMethodSignatureAtPositionAsync(document, position);

            if (string.IsNullOrEmpty(methodName))
                return (document, null);

            return (document, new MethodViewInfo
            {
                TypeName = typeName ?? "",
                MethodName = methodName ?? "",
                FullName = fullSignature ?? $"{typeName}.{methodName}",
                HasBody = true
            });
        }
        private async Task<(string? TypeName, string? MethodName, string? FullSignature)> GetMethodSignatureAtPositionAsync(
            Document document,
            int position,
            CancellationToken cancellationToken = default)
        {
            var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
            if (syntaxRoot == null)
                return (null, null, null);

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
                return (null, null, null);

            var token = syntaxRoot.FindToken(position);

            var typeNode = token.Parent?.AncestorsAndSelf()
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault();

            if (typeNode == null)
                return (null, null, null);

            var typeName = GetFullTypeName(typeNode);

            // Try to find the enclosing member: method, constructor, property accessor, operator, etc.
            var methodNode = token.Parent?.AncestorsAndSelf()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault();

            if (methodNode != null)
            {
                var methodName = methodNode.Identifier.Text;
                var methodSymbol = semanticModel.GetDeclaredSymbol(methodNode, cancellationToken);
                if (methodSymbol != null)
                {
                    var parameters = string.Join(", ", methodSymbol.Parameters.Select(p => p.Type.ToDisplayString()));
                    var fullSignature = $"{typeName}.{methodName}({parameters})";
                    System.Diagnostics.Debug.WriteLine($"VSILViewer MethodLocator: Got signature - {fullSignature}");
                    return (typeName, methodName, fullSignature);
                }
                return (typeName, methodName, $"{typeName}.{methodName}");
            }

            // Constructor
            var ctorNode = token.Parent?.AncestorsAndSelf()
                .OfType<ConstructorDeclarationSyntax>()
                .FirstOrDefault();
            if (ctorNode != null)
            {
                var ctorSymbol = semanticModel.GetDeclaredSymbol(ctorNode, cancellationToken);
                if (ctorSymbol != null)
                {
                    var parameters = string.Join(", ", ctorSymbol.Parameters.Select(p => p.Type.ToDisplayString()));
                    return (typeName, ".ctor", $"{typeName}..ctor({parameters})");
                }
                return (typeName, ".ctor", $"{typeName}..ctor");
            }

            // Property accessor (get/set)
            var accessorNode = token.Parent?.AncestorsAndSelf()
                .OfType<AccessorDeclarationSyntax>()
                .FirstOrDefault();
            if (accessorNode != null)
            {
                var propNode = accessorNode.Parent?.Parent as PropertyDeclarationSyntax;
                if (propNode != null)
                {
                    var prefix = accessorNode.Kind() == SyntaxKind.GetAccessorDeclaration ? "get_" : "set_";
                    var accessorName = prefix + propNode.Identifier.Text;
                    return (typeName, accessorName, $"{typeName}.{accessorName}");
                }
            }

            // Property (when cursor is on property name, not inside accessor)
            var propertyNode = token.Parent?.AncestorsAndSelf()
                .OfType<PropertyDeclarationSyntax>()
                .FirstOrDefault();
            if (propertyNode != null)
            {
                var propName = "get_" + propertyNode.Identifier.Text;
                return (typeName, propName, $"{typeName}.{propName}");
            }

            // Destructor/Finalizer (~ClassName → Finalize)
            var destructorNode = token.Parent?.AncestorsAndSelf()
                .OfType<DestructorDeclarationSyntax>()
                .FirstOrDefault();
            if (destructorNode != null)
            {
                return (typeName, "Finalize", $"{typeName}.Finalize");
            }

            // Operator (operator+ → op_Addition, etc.)
            var operatorNode = token.Parent?.AncestorsAndSelf()
                .OfType<OperatorDeclarationSyntax>()
                .FirstOrDefault();
            if (operatorNode != null)
            {
                var opSymbol = semanticModel.GetDeclaredSymbol(operatorNode, cancellationToken);
                var ilName = opSymbol?.MetadataName ?? $"op_{operatorNode.OperatorToken.Text}";
                return (typeName, ilName, $"{typeName}.{ilName}");
            }

            // Conversion operator (implicit/explicit)
            var conversionNode = token.Parent?.AncestorsAndSelf()
                .OfType<ConversionOperatorDeclarationSyntax>()
                .FirstOrDefault();
            if (conversionNode != null)
            {
                var convSymbol = semanticModel.GetDeclaredSymbol(conversionNode, cancellationToken);
                var ilName = convSymbol?.MetadataName ?? "op_Implicit";
                return (typeName, ilName, $"{typeName}.{ilName}");
            }

            // Indexer (this[...] → get_Item/set_Item)
            var indexerNode = token.Parent?.AncestorsAndSelf()
                .OfType<IndexerDeclarationSyntax>()
                .FirstOrDefault();
            if (indexerNode != null)
            {
                // Check if inside a get or set accessor
                var indexerAccessor = token.Parent?.AncestorsAndSelf()
                    .OfType<AccessorDeclarationSyntax>()
                    .FirstOrDefault();
                var prefix = indexerAccessor?.Kind() == SyntaxKind.SetAccessorDeclaration ? "set_Item" : "get_Item";
                return (typeName, prefix, $"{typeName}.{prefix}");
            }

            // Event (event → add_EventName/remove_EventName)
            var eventNode = token.Parent?.AncestorsAndSelf()
                .OfType<EventDeclarationSyntax>()
                .FirstOrDefault();
            if (eventNode != null)
            {
                var eventAccessor = token.Parent?.AncestorsAndSelf()
                    .OfType<AccessorDeclarationSyntax>()
                    .FirstOrDefault();
                var prefix = eventAccessor?.Kind() == SyntaxKind.RemoveAccessorDeclaration ? "remove_" : "add_";
                return (typeName, prefix + eventNode.Identifier.Text, $"{typeName}.{prefix}{eventNode.Identifier.Text}");
            }

            return (typeName, null, null);
        }

        private string GetFullTypeName(TypeDeclarationSyntax typeNode)
        {
            var names = new List<string>();
            SyntaxNode? current = typeNode;

            while (current != null)
            {
                if (current is TypeDeclarationSyntax tds)
                {
                    names.Insert(0, tds.Identifier.Text);
                }
                else if (current is NamespaceDeclarationSyntax nds)
                {
                    names.Insert(0, nds.Name.ToString());
                }
                else if (current is FileScopedNamespaceDeclarationSyntax fsn)
                {
                    names.Insert(0, fsn.Name.ToString());
                }
                current = current.Parent;
            }

            return string.Join(".", names);
        }
    }
}
