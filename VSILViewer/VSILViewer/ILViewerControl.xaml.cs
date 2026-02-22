using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using VSILViewer.Models;
using VSILViewer.Services;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Windows.Media;
using System.Xml;
using System.Reflection;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.VisualStudio.PlatformUI;

namespace VSILViewer
{
    /// <summary>
    /// Shared compiled regex for matching IL method references (Type::Method pattern).
    /// Used by MethodReferenceColorizer, ExtractMethodReference, and ExtractAllMethodReferences.
    /// Compiled + timeout to mitigate ReDoS on pathological inputs.
    /// </summary>
    internal static class MethodReferencePatterns
    {
        internal static readonly Regex CompiledRegex = new Regex(
            @"(?:\[[\w\.\-]+\])?[\w\./<>+\[\]&*]+(?:`\d+)?(?:<[^>]*>)?::\.?[\w<>]+(?:`\d+)?(?:<[^>]*>)?(?:\([^)]*\))?",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    }

    internal class MethodReferenceColorizer : DocumentColorizingTransformer
    {
        private static SolidColorBrush LinkBrush;

        static MethodReferenceColorizer()
        {
            LinkBrush = new SolidColorBrush(Color.FromRgb(30, 144, 255));
            LinkBrush.Freeze();
        }

        /// <summary>Update the link color to match the current VS theme.</summary>
        internal static void UpdateLinkBrush(SolidColorBrush brush)
        {
            brush.Freeze();
            LinkBrush = brush;
        }

        /// <summary>Get the current link brush for use by tree items and breadcrumbs.</summary>
        internal static SolidColorBrush GetLinkBrush() => LinkBrush;

        protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
        {
            var lineText = CurrentContext.Document.GetText(line);
            MatchCollection matches;
            try { matches = MethodReferencePatterns.CompiledRegex.Matches(lineText); }
            catch (RegexMatchTimeoutException) { return; }
            foreach (Match match in matches)
            {
                var startOffset = line.Offset + match.Index;
                var endOffset = startOffset + match.Length;
                ChangeLinePart(startOffset, endOffset, element =>
                {
                    element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                    element.TextRunProperties.SetForegroundBrush(LinkBrush);
                });
            }
        }
    }

    public partial class ILViewerControl : UserControl, IDisposable, IVsSelectionEvents
    {
        private const string VersionString = "IL Viewer v1.2.0";

        private readonly AssemblyInspectionService _assemblyService;
        private readonly MethodLocatorService _methodLocator = new();
        private readonly CaretPositionService _caretService = new();

        private VisualStudioWorkspace? _workspace;
        private IComponentModel? _componentModel;
        private IVsMonitorSelection? _monitorSelection;
        private uint _selectionEventsCookie;
        private AsyncPackage? _package;
        private IVsEditorAdaptersFactoryService? _editorAdaptersFactory;

        private IHighlightingDefinition? _csharpHighlighting;
        private IHighlightingDefinition? _ilHighlighting;

        private ViewMode _currentViewMode = ViewMode.DecompiledCSharp;
        private bool _autoRefresh = true;
        private bool _wordWrapEnabled = true;
        private CancellationTokenSource? _refreshCts;
        private readonly object _refreshLock = new();
        private bool _disposed;
        private bool _isInitialized = false;

        // Track displayed methods
        private readonly HashSet<string> _displayedMethods = new();
        private readonly StringBuilder _accumulatedContent = new();
        private string? _currentMainMethodKey = null;
        private string? _currentLoadedType = null; // Track when full class is loaded
        private Document? _currentDocument;

        // Navigation history for breadcrumb trail
        private readonly List<NavigationEntry> _navigationHistory = new();
        private int _currentNavIndex = -1;

        public ILViewerControl()
        {
            InitializeComponent();
            _assemblyService = new AssemblyInspectionService();
            _caretService.CaretPositionChanged += OnCaretPositionChanged;
            _isInitialized = true;

            // Wire up Ctrl+Click navigation for AvalonEdit
            CodeEditor.PreviewMouseLeftButtonDown += CodeEditor_PreviewMouseLeftButtonDown;
            CodeEditor.MouseMove += CodeEditor_MouseMove;
            CodeEditor.PreviewKeyDown += CodeEditor_PreviewKeyDown;
            CodeEditor.PreviewKeyUp += CodeEditor_PreviewKeyUp;

            // Underline method references so users know they're clickable
            CodeEditor.TextArea.TextView.LineTransformers.Add(new MethodReferenceColorizer());

            // Enable Ctrl+F search within the code viewer
            SearchPanel.Install(CodeEditor.TextArea);

            // Keyboard shortcuts
            PreviewKeyDown += ILViewerControl_PreviewKeyDown;

            // Version indicator to verify new builds load
            HeaderText.Text = $"{VersionString} - No file open";
        }

        private void ILViewerControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                RefreshButton_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Left && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                BackButton_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Right && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                ForwardButton_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.C && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                CopyButton_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.F12)
            {
                NavigateToMethodAtCaret();
                e.Handled = true;
            }
            else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                SearchBox.Focus();
                e.Handled = true;
            }
        }

        private bool _servicesInitialized;

        public async Task InitializeAsync(AsyncPackage package)
        {
            if (_servicesInitialized) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _package = package;
            _componentModel = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            if (_componentModel == null) return;

            _workspace = _componentModel.GetService<VisualStudioWorkspace>();
            _editorAdaptersFactory = _componentModel.GetService<IVsEditorAdaptersFactoryService>();

            if (_workspace != null)
            {
                _assemblyService.SetWorkspace(_workspace);
                _assemblyService.SolutionChanged += (s, e) =>
                {
                    _currentDocument = null;
                    _currentLoadedType = null;
                    _currentMainMethodKey = null;
                };
            }

            _monitorSelection = await package.GetServiceAsync(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            if (_monitorSelection != null)
            {
                _monitorSelection.AdviseSelectionEvents(this, out _selectionEventsCookie);
            }

            _servicesInitialized = true;

            // Load syntax highlighting definitions
            LoadSyntaxHighlighting();

            // Set initial highlighting
            UpdateSyntaxHighlighting();

            // Sync editor colors with VS theme
            ApplyVsThemeColors();
            VSColorTheme.ThemeChanged += _ => ApplyVsThemeColors();

            await TryAttachWithRetriesAsync();
        }

        private void LoadSyntaxHighlighting()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();

                // Load C# syntax highlighting from embedded resource
                using (var stream = assembly.GetManifestResourceStream("VSILViewer.CSharp-Mode.xshd"))
                {
                    if (stream != null)
                    {
                        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                        {
                            _csharpHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                        }
                    }
                }

                // Load IL syntax highlighting from embedded resource
                using (var stream = assembly.GetManifestResourceStream("VSILViewer.IL-Mode.xshd"))
                {
                    if (stream != null)
                    {
                        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                        {
                            _ilHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VSILViewer: Error loading syntax highlighting: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads VS environment colors and applies them to the AvalonEdit editor
        /// so it matches the current VS theme (Dark, Light, Blue, etc.).
        /// </summary>
        private void ApplyVsThemeColors()
        {
            try
            {
                // Read VS editor background and foreground colors
                var bgColor = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
                var fgColor = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);

                var wpfBg = Color.FromRgb(bgColor.R, bgColor.G, bgColor.B);
                var wpfFg = Color.FromRgb(fgColor.R, fgColor.G, fgColor.B);

                CodeEditor.Background = new SolidColorBrush(wpfBg);
                CodeEditor.Foreground = new SolidColorBrush(wpfFg);

                // Line number colors
                var lineNumColor = VSColorTheme.GetThemedColor(EnvironmentColors.CommandBarTextInactiveColorKey);
                var wpfLineNum = Color.FromRgb(lineNumColor.R, lineNumColor.G, lineNumColor.B);
                CodeEditor.LineNumbersForeground = new SolidColorBrush(wpfLineNum);

                // Detect if dark theme (background luminance < 0.5)
                var luminance = (0.299 * wpfBg.R + 0.587 * wpfBg.G + 0.114 * wpfBg.B) / 255.0;
                _isDarkTheme = luminance < 0.5;

                // Update the link color brush based on theme
                UpdateLinkBrush();

                System.Diagnostics.Debug.WriteLine($"VSILViewer: Theme applied - BG=({wpfBg.R},{wpfBg.G},{wpfBg.B}) FG=({wpfFg.R},{wpfFg.G},{wpfFg.B}) Dark={_isDarkTheme}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VSILViewer: Theme sync error: {ex.Message}");
            }
        }

        private bool _isDarkTheme = true;

        private void UpdateLinkBrush()
        {
            // Use a theme-appropriate link color
            try
            {
                var linkColor = VSColorTheme.GetThemedColor(EnvironmentColors.ControlLinkTextColorKey);
                var wpfLink = Color.FromRgb(linkColor.R, linkColor.G, linkColor.B);
                MethodReferenceColorizer.UpdateLinkBrush(new SolidColorBrush(wpfLink));
            }
            catch
            {
                // Fallback to DodgerBlue / dark navy based on detected theme
                var fallback = _isDarkTheme
                    ? Color.FromRgb(30, 144, 255)  // DodgerBlue for dark themes
                    : Color.FromRgb(0, 102, 204);   // Dark blue for light themes
                MethodReferenceColorizer.UpdateLinkBrush(new SolidColorBrush(fallback));
            }
        }

        private void UpdateSyntaxHighlighting()
        {
            if (_currentViewMode == ViewMode.DecompiledCSharp && _csharpHighlighting != null)
            {
                CodeEditor.SyntaxHighlighting = _csharpHighlighting;
            }
            else
            {
                CodeEditor.SyntaxHighlighting = _ilHighlighting;
            }
        }

        // CreateTextEditor method removed - now using AvalonEdit which doesn't need this initialization

        private async Task TryAttachWithRetriesAsync()
        {
            // VS startup can take several seconds before editors are ready
            int[] delays = { 200, 500, 1000, 1500, 2000, 3000, 4000, 5000 };
            for (int i = 0; i < delays.Length; i++)
            {
                await AttachToActiveDocumentAsync();
                if (_caretService.HasTextView)
                {
                    await RefreshContentAsync();
                    return;
                }
                await Task.Delay(delays[i]);
            }
            HeaderText.Text = $"{VersionString}";
            ShowMessage("Click in a C# editor to start");
        }

        private void ShowMessage(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            StatusText.Text = message;
        }

        private async Task AttachToActiveDocumentAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_editorAdaptersFactory == null) return;

            var textManager = await _package?.GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager;
            if (textManager == null) return;

            textManager.GetActiveView(1, null, out IVsTextView textView);
            if (textView == null) return;

            var wpfTextView = _editorAdaptersFactory.GetWpfTextView(textView);
            if (wpfTextView != null)
            {
                _caretService.AttachToTextView(wpfTextView);
            }
        }

        private async void OnCaretPositionChanged(object? sender, EventArgs e)
        {
            if (_autoRefresh)
            {
                await RefreshContentAsync();
            }
        }

        private async Task RefreshContentAsync()
        {
            if (!_isInitialized) return;

            var cts = ResetCancellationToken();

            try
            {
                // Shorter delay when we already have content loaded (just checking for method change)
                var delay = _currentLoadedType != null ? 100 : 300;
                await Task.Delay(delay, cts.Token);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var (document, methodInfo) = await GetCurrentMethodInfoAsync(cts);
                if (document == null || methodInfo == null) return;

                // Use FullName as method key to distinguish between overloads
                var methodKey = !string.IsNullOrEmpty(methodInfo.FullName)
                    ? methodInfo.FullName.ToLowerInvariant()
                    : $"{methodInfo.TypeName}.{methodInfo.MethodName}".ToLowerInvariant();

                System.Diagnostics.Debug.WriteLine($"VSILViewer: Method detected - FullName: '{methodInfo.FullName}', Key: '{methodKey}', Previous: '{_currentMainMethodKey}', LoadedType: '{_currentLoadedType}'");

                if (cts.IsCancellationRequested) return;

                // Skip reload if this method's class is already fully loaded
                var currentTypeLower = methodInfo.TypeName?.ToLowerInvariant();
                if (_currentLoadedType != null && _currentLoadedType == currentTypeLower)
                {
                    System.Diagnostics.Debug.WriteLine($"VSILViewer: Type '{currentTypeLower}' already loaded, skipping");
                    return;
                }

                if (_currentMainMethodKey == methodKey) return;

                await LoadAndDisplayMethodAsync(document, methodInfo, methodKey, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private CancellationTokenSource ResetCancellationToken()
        {
            lock (_refreshLock)
            {
                _refreshCts?.Cancel();
                _refreshCts = new CancellationTokenSource();
                return _refreshCts;
            }
        }

        private async Task<(Document? document, MethodViewInfo? methodInfo)> GetCurrentMethodInfoAsync(CancellationTokenSource cts)
        {
            var textView = _caretService.GetCurrentTextView();
            if (textView == null)
            {
                ShowMessage("Open a C# file in the editor to start viewing IL");
                return (null, null);
            }

            var result = await _methodLocator.GetMethodAtCaretAsync(textView, _workspace);
            if (result.Document == null || result.MethodInfo == null)
            {
                ShowMessage("Place cursor inside a method, constructor, or property body");
                return (null, null);
            }

            _currentDocument = result.Document;
            return result;
        }

        private async Task LoadAndDisplayMethodAsync(Document document, MethodViewInfo methodInfo, string methodKey, CancellationToken token)
        {
            _displayedMethods.Clear();
            _accumulatedContent.Clear();
            ClearNavigationHistory();

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            StatusText.Text = $"Loading {methodInfo.TypeName}...";

            // Try loading the whole class first (if not too large)
            try
            {
                System.Diagnostics.Debug.WriteLine($"VSILViewer: Attempting full type load for '{methodInfo.TypeName}' in mode {_currentViewMode}");
                var typeContent = await _assemblyService.GetTypeContentAsync(
                    document, methodInfo.TypeName, _currentViewMode, token);

                if (!string.IsNullOrEmpty(typeContent) && !typeContent.StartsWith("// Error") && !token.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine($"VSILViewer: Full type loaded, {typeContent.Length} chars");
                    _currentLoadedType = methodInfo.TypeName?.ToLowerInvariant();
                    _currentMainMethodKey = null; // Type-level tracking takes precedence
                    HeaderText.Text = $"{VersionString} - {methodInfo.TypeName} (full class)";
                    DisplayContent(typeContent, methodInfo.TypeName, methodInfo.TypeName, methodInfo.TypeName, true);
                    return;
                }
                System.Diagnostics.Debug.WriteLine($"VSILViewer: Full type load returned null/empty/error, falling back to single method");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VSILViewer: Full type load failed: {ex.Message}, falling back to single method");
            }

            // Fall back to single method if class is too large, not found, or errored
            _currentLoadedType = null;
            _currentMainMethodKey = methodKey;
            var content = await _assemblyService.GetMethodContentAsync(
                document, methodInfo.TypeName, methodInfo.MethodName, _currentViewMode, token, methodInfo.FullName);

            if (!string.IsNullOrEmpty(content) && !token.IsCancellationRequested)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                HeaderText.Text = $"{VersionString} - {methodInfo.TypeName}.{methodInfo.MethodName}";
                DisplayContent(content, methodInfo.MethodName, methodInfo.TypeName, methodInfo.FullName, true);
            }
        }

        private void DisplayContent(string content, string methodName, string typeName, string fullName, bool isMainMethod)
        {
            // Use full name (with signature) as key to distinguish overloads
            var methodKey = !string.IsNullOrEmpty(fullName)
                ? fullName.ToLowerInvariant()
                : $"{typeName}.{methodName}".ToLowerInvariant();

            if (!isMainMethod && _displayedMethods.Contains(methodKey))
            {
                // Scroll to the already-displayed method instead of ignoring
                var existingEntry = _navigationHistory.FirstOrDefault(e =>
                    e.FullName?.ToLowerInvariant() == methodKey ||
                    $"{e.TypeName}.{e.MethodName}".ToLowerInvariant() == methodKey);
                if (existingEntry != null)
                {
                    ScrollToNavigationEntry(existingEntry);
                    StatusText.Text = $"Scrolled to: {typeName}.{methodName}";
                }
                else
                {
                    StatusText.Text = $"Already displayed: {typeName}.{methodName}";
                }
                return;
            }

            // Track the line number where this method's content starts
            var lineOffset = CountLines(_accumulatedContent);

            if (isMainMethod)
            {
                _displayedMethods.Clear();
                _accumulatedContent.Clear();
                lineOffset = 0;
            }
            else if (_accumulatedContent.Length > 0)
            {
                _accumulatedContent.AppendLine().AppendLine()
                    .AppendLine("// " + new string('=', 80)).AppendLine();
                lineOffset = CountLines(_accumulatedContent);
            }

            _displayedMethods.Add(methodKey);
            _accumulatedContent.Append(content);

            var fullContent = _accumulatedContent.ToString();
            UpdateEditorContent(fullContent);

            // Add navigation entry and update breadcrumbs
            AddNavigationEntry(typeName, methodName, fullName, lineOffset);

            // Only rebuild call hierarchy when main content changes (not on appended methods)
            // This prevents the tree from collapsing when clicking tree nodes
            if (isMainMethod && _isInitialized && CallTreeToggle.IsChecked == true)
            {
                BuildCallHierarchy();
            }

            StatusText.Text = $"Ready | {_displayedMethods.Count} method(s) | {fullContent.Count(c => c == '\n') + 1} lines";
        }

        /// <summary>
        /// Counts newlines in a StringBuilder without allocating a string copy.
        /// </summary>
        private static int CountLines(StringBuilder sb)
        {
            int count = 0;
            for (int i = 0; i < sb.Length; i++)
            {
                if (sb[i] == '\n') count++;
            }
            return count;
        }

        private void UpdateEditorContent(string content)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CodeEditor.Text = content ?? string.Empty;
        }

        private void CodeEditor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("VSILViewer: Mouse button down detected");

            if (!Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl))
            {
                System.Diagnostics.Debug.WriteLine("VSILViewer: Ctrl key not pressed, ignoring click");
                return;
            }

            System.Diagnostics.Debug.WriteLine("VSILViewer: Ctrl+Click detected, processing...");

            try
            {
                var position = e.GetPosition(CodeEditor);
                var textViewPosition = CodeEditor.GetPositionFromPoint(position);

                System.Diagnostics.Debug.WriteLine($"VSILViewer: Position: {position}, TextView Position: {textViewPosition}");

                if (textViewPosition == null || textViewPosition.Value.Line < 1)
                {
                    System.Diagnostics.Debug.WriteLine("VSILViewer: Invalid text view position");
                    return;
                }

                // Get the line and convert position to offset
                var line = CodeEditor.Document.GetLineByNumber(textViewPosition.Value.Line);
                var lineText = CodeEditor.Document.GetText(line);
                var offset = CodeEditor.Document.GetOffset(textViewPosition.Value.Line, textViewPosition.Value.Column);
                var positionInLine = offset - line.Offset;

                System.Diagnostics.Debug.WriteLine($"VSILViewer: Line text: '{lineText}', Position in line: {positionInLine}");

                // Try to find method reference at this position
                var methodReference = ExtractMethodReference(lineText, positionInLine);
                System.Diagnostics.Debug.WriteLine($"VSILViewer: Extracted method reference: '{methodReference}'");

                if (methodReference != null)
                {
                    System.Diagnostics.Debug.WriteLine($"VSILViewer: Navigating to method: {methodReference}");
                    OnMethodLinkClickedAsync(methodReference);
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VSILViewer: Error in Ctrl+Click handler: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void CodeEditor_MouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                var position = e.GetPosition(CodeEditor);
                var textViewPosition = CodeEditor.GetPositionFromPoint(position);

                if (textViewPosition != null && textViewPosition.Value.Line >= 1)
                {
                    var line = CodeEditor.Document.GetLineByNumber(textViewPosition.Value.Line);
                    var lineText = CodeEditor.Document.GetText(line);
                    var offset = CodeEditor.Document.GetOffset(textViewPosition.Value.Line, textViewPosition.Value.Column);
                    var positionInLine = offset - line.Offset;

                    var methodReference = ExtractMethodReference(lineText, positionInLine);
                    CodeEditor.Cursor = methodReference != null ? Cursors.Hand : Cursors.IBeam;
                    return;
                }
            }
            catch { CodeEditor.Cursor = Cursors.IBeam; return; }
            CodeEditor.Cursor = Cursors.IBeam;
        }

        private void CodeEditor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Cursor is managed by MouseMove handler continuously
        }

        private void CodeEditor_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            // Reset cursor when Ctrl is released (no longer in navigation mode)
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
            {
                CodeEditor.Cursor = Cursors.IBeam;
            }
        }

        private void NavigateToMethodAtCaret()
        {
            try
            {
                var offset = CodeEditor.CaretOffset;
                if (offset < 0 || offset >= CodeEditor.Document.TextLength) return;
                var location = CodeEditor.Document.GetLocation(offset);
                var line = CodeEditor.Document.GetLineByNumber(location.Line);
                var lineText = CodeEditor.Document.GetText(line);
                var positionInLine = offset - line.Offset;
                var methodReference = ExtractMethodReference(lineText, positionInLine);
                if (methodReference != null)
                {
                    OnMethodLinkClickedAsync(methodReference);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VSILViewer: F12 navigation error: {ex.Message}");
            }
        }

        private string? ExtractMethodReference(string lineText, int position)
        {
            try
            {
                return MethodReferencePatterns.CompiledRegex.Matches(lineText)
                    .Cast<Match>()
                    .FirstOrDefault(m => position >= m.Index && position < m.Index + m.Length)
                    ?.Value;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        public async void OnMethodLinkClickedAsync(string methodReference)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var (typeName, methodName) = ParseMethodReference(methodReference);
                if (typeName == null || methodName == null || _currentDocument == null)
                {
                    StatusText.Text = typeName == null ? "Invalid method reference format" : "Open a C# file to enable navigation";
                    return;
                }

                StatusText.Text = $"Loading {typeName}.{methodName}...";

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                // Try loading the entire class, appended below existing content
                try
                {
                    var typeContent = await _assemblyService.GetTypeContentAsync(
                        _currentDocument, typeName, _currentViewMode, cts.Token);

                    if (!string.IsNullOrEmpty(typeContent) && !typeContent.StartsWith("// Error"))
                    {
                        // Append the full class below existing content (isMainMethod: false)
                        // This preserves the original source and call tree
                        DisplayContent(typeContent, typeName, typeName, typeName, false);

                        // Scroll to the specific method within the appended content
                        // Pass full reference for signature-aware matching (handles overloads)
                        ScrollToMethodInEditor(typeName, methodName, methodReference);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"VSILViewer: Full type load for click failed: {ex.Message}, falling back to single method");
                }

                // Fall back to single method (also appended)
                var content = await _assemblyService.GetMethodContentAsync(
                    _currentDocument, typeName, methodName, _currentViewMode, cts.Token, methodReference);

                if (IsValidContent(content))
                {
                    DisplayContent(content!, methodName, typeName, methodReference, false);
                }
                else
                {
                    StatusText.Text = $"Could not load {typeName}.{methodName} — try rebuilding the project";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Scrolls to a method in the editor using signature-aware matching.
        /// Handles overloads by matching the full Type::Method(params) reference.
        /// Searches from the bottom up to find the most recently appended occurrence.
        /// Falls back to name-only match if exact signature not found.
        /// </summary>
        private void ScrollToMethodInEditor(string typeName, string methodName, string fullReference)
        {
            try
            {
                var doc = CodeEditor.Document;
                int fallbackLine = -1; // Name-only match as fallback

                // Extract the params portion from the full reference for matching
                // e.g. "System.String::Concat(string, string)" → "(string, string)"
                var paramsStart = fullReference.IndexOf('(');
                var paramsSuffix = paramsStart >= 0 ? fullReference.Substring(paramsStart) : null;

                // Also build a "Type::Method" pattern for IL mode
                var typeMethodPattern = $"{typeName}::{methodName}";

                // Search from bottom (most recently appended content) upward
                for (int i = doc.LineCount; i >= 1; i--)
                {
                    var line = doc.GetLineByNumber(i);
                    var lineText = doc.GetText(line);

                    if (_currentViewMode == ViewMode.IL)
                    {
                        // IL mode: method headers look like "// ReturnType FullType::MethodName(ParamTypes)"
                        if (lineText.Contains($"::{methodName}"))
                        {
                            // Check if the type also matches (for different classes with same method name)
                            var normalizedLine = lineText.Replace("/", ".");
                            var normalizedType = typeName.Replace("/", ".");

                            if (normalizedLine.Contains(normalizedType))
                            {
                                // Exact signature match: check params if available
                                if (paramsSuffix != null && lineText.Contains(paramsSuffix))
                                {
                                    ScrollToLine(i, line);
                                    return;
                                }

                                // Type+name match but params don't match — keep as fallback
                                if (fallbackLine < 0) fallbackLine = i;
                            }
                            else if (fallbackLine < 0)
                            {
                                // Name-only match — weakest fallback
                                fallbackLine = i;
                            }
                        }
                    }
                    else
                    {
                        // C# mode: look for method declaration
                        var trimmed = lineText.TrimStart();

                        // Match "ReturnType MethodName(" pattern
                        if (trimmed.Contains($" {methodName}(") || trimmed.StartsWith($"{methodName}("))
                        {
                            // For overloads, try to match parameter types from the reference
                            if (paramsSuffix != null)
                            {
                                // Extract param names from the decompiled line for comparison
                                var lineParamsStart = trimmed.IndexOf('(');
                                if (lineParamsStart >= 0)
                                {
                                    var lineParams = trimmed.Substring(lineParamsStart);
                                    // Check if parameter types match (simplified: check if key types appear)
                                    if (ParamsLikelyMatch(paramsSuffix, lineParams))
                                    {
                                        ScrollToLine(i, line);
                                        return;
                                    }
                                }
                            }

                            if (fallbackLine < 0) fallbackLine = i;
                        }
                    }
                }

                // Use best fallback if no exact match found
                if (fallbackLine > 0)
                {
                    var line = doc.GetLineByNumber(fallbackLine);
                    ScrollToLine(fallbackLine, line);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VSILViewer: ScrollToMethod error: {ex.Message}");
            }
        }

        private void ScrollToLine(int lineNumber, ICSharpCode.AvalonEdit.Document.DocumentLine line)
        {
            CodeEditor.ScrollToLine(lineNumber);
            CodeEditor.CaretOffset = line.Offset;
        }

        /// <summary>
        /// Heuristic check if parameter types from an IL reference likely match a C# declaration.
        /// Compares the short type names (last segment) to handle namespace differences.
        /// E.g. IL "(System.String, System.Int32)" matches C# "(string s, int count)"
        /// </summary>
        private static bool ParamsLikelyMatch(string ilParams, string csParams)
        {
            // Extract just the type names, stripping parameter names from C#
            // IL: "(System.String, System.Int32)"
            // C#: "(string s, int count)"

            // Quick check: same number of commas = same number of params
            var ilCommas = ilParams.Count(c => c == ',');
            var csCommas = csParams.Count(c => c == ',');
            if (ilCommas != csCommas) return false;

            // If no params, both empty = match
            if (ilCommas == 0 && ilParams.Contains("()") && csParams.Contains("()")) return true;

            // For each IL param type, check if a key part appears in the C# line
            var ilInner = ilParams.Trim('(', ')').Trim();
            if (string.IsNullOrEmpty(ilInner)) return csParams.Contains("()");

            var ilTypes = ilInner.Split(',');
            foreach (var ilType in ilTypes)
            {
                var shortType = ilType.Trim();
                var lastDot = shortType.LastIndexOf('.');
                if (lastDot >= 0) shortType = shortType.Substring(lastDot + 1);
                shortType = shortType.Trim();

                // Check if the short type name appears in the C# params
                // Also check C# keyword aliases
                var csAlias = shortType switch
                {
                    "String" => "string", "Int32" => "int", "Int64" => "long",
                    "Boolean" => "bool", "Double" => "double", "Single" => "float",
                    "Byte" => "byte", "Char" => "char", "Object" => "object",
                    "Void" => "void", "Decimal" => "decimal",
                    _ => null
                };

                if (!csParams.Contains(shortType) && (csAlias == null || !csParams.Contains(csAlias)))
                    return false;
            }

            return true;
        }

        private (string? typeName, string? methodName) ParseMethodReference(string methodReference)
        {
            var parts = methodReference.Split(new[] { "::" }, StringSplitOptions.None);
            if (parts.Length != 2) return (null, null);

            var typeRaw = parts[0].Trim();
            var methodRaw = parts[1].Trim();

            // IL format may have return type prefix: "returntype Namespace.Type::Method(params)"
            // Take the last space-separated token as the actual type
            var lastSpaceIndex = typeRaw.LastIndexOf(' ');
            if (lastSpaceIndex >= 0)
            {
                typeRaw = typeRaw.Substring(lastSpaceIndex + 1);
            }

            // Strip assembly reference prefix: [mscorlib]System.String -> System.String
            typeRaw = Regex.Replace(typeRaw, @"^\[[\w\.\-]+\]", "");

            // Strip generic arity markers (`1, `2) but preserve <> in compiler-generated names
            var typeName = Regex.Replace(typeRaw, @"`\d+", "");

            // Strip trailing array/ref/pointer markers from type name for lookup
            typeName = typeName.TrimEnd('[', ']', '&', '*');

            // Strip trailing generic type arguments like <System.String> or <string, List<int>>
            // But preserve compiler-generated names like <ProcessAsync>d__0
            // Use bracket-depth-aware stripping to handle nested generics
            typeName = StripTrailingGenericArgs(typeName);

            // Extract method name - strip parameter list, preserve <> for compiler-generated methods
            var parenIndex = methodRaw.IndexOf('(');
            var methodName = parenIndex >= 0 ? methodRaw.Substring(0, parenIndex) : methodRaw;
            methodName = Regex.Replace(methodName, @"`\d+", "");

            // Handle .ctor and .cctor
            if (methodName == ".ctor" || methodName == ".cctor")
            {
                return (typeName, methodName);
            }

            return (typeName, methodName);
        }

        /// <summary>
        /// Strips trailing generic type arguments handling nested brackets.
        /// "Dict&lt;string, List&lt;int&gt;&gt;" → "Dict"
        /// But preserves compiler-generated: "&lt;ProcessAsync&gt;d__0" (has suffix after &gt;)
        /// </summary>
        private static string StripTrailingGenericArgs(string typeName)
        {
            if (!typeName.EndsWith(">")) return typeName;

            // Walk backwards counting bracket depth to find matching <
            int depth = 0;
            for (int i = typeName.Length - 1; i >= 0; i--)
            {
                if (typeName[i] == '>') depth++;
                else if (typeName[i] == '<') depth--;

                if (depth == 0)
                {
                    // Found the matching < — only strip if it's at the end (generic args)
                    // Compiler-generated names like <Name>d__0 have content after >
                    return typeName.Substring(0, i);
                }
            }
            return typeName; // Unbalanced brackets, return as-is
        }

        private bool IsValidContent(string? content) =>
            content != null && !content.StartsWith("// Method not found") && !content.StartsWith("// Could not");

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // Force refresh - clear the current key so the same method can be reloaded
            _currentMainMethodKey = null;
            _currentLoadedType = null;

            _ = Task.Run(async () =>
            {
                // Re-attach if no text view (e.g., after VS startup or clear)
                if (!_caretService.HasTextView)
                {
                    await AttachToActiveDocumentAsync();
                }
                await RefreshContentAsync();
            });
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _displayedMethods.Clear();
            _accumulatedContent.Clear();
            _currentMainMethodKey = null;
            _currentLoadedType = null;
            ClearNavigationHistory();
            UpdateEditorContent("");
            HeaderText.Text = $"{VersionString} - No file open";
            StatusText.Text = "Cleared";
        }

        private int _lastSearchOffset = 0;

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var searchText = SearchBox.Text;
                if (string.IsNullOrEmpty(searchText)) return;

                var doc = CodeEditor.Document;
                var text = doc.Text;
                var backwards = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                int foundIndex;
                if (backwards)
                {
                    // Search backwards from current position
                    var searchFrom = _lastSearchOffset > 0 ? _lastSearchOffset - 1 : text.Length - 1;
                    foundIndex = text.LastIndexOf(searchText, searchFrom, StringComparison.OrdinalIgnoreCase);
                    if (foundIndex < 0)
                        foundIndex = text.LastIndexOf(searchText, text.Length - 1, StringComparison.OrdinalIgnoreCase); // wrap
                }
                else
                {
                    // Search forward from current position
                    var searchFrom = _lastSearchOffset + 1;
                    if (searchFrom >= text.Length) searchFrom = 0;
                    foundIndex = text.IndexOf(searchText, searchFrom, StringComparison.OrdinalIgnoreCase);
                    if (foundIndex < 0)
                        foundIndex = text.IndexOf(searchText, 0, StringComparison.OrdinalIgnoreCase); // wrap
                }

                if (foundIndex >= 0)
                {
                    _lastSearchOffset = foundIndex;
                    CodeEditor.CaretOffset = foundIndex;
                    CodeEditor.Select(foundIndex, searchText.Length);
                    var line = doc.GetLineByOffset(foundIndex);
                    CodeEditor.ScrollToLine(line.LineNumber);
                    StatusText.Text = $"Found at line {line.LineNumber}";
                }
                else
                {
                    StatusText.Text = $"Not found: {searchText}";
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                SearchBox.Text = "";
                _lastSearchOffset = 0;
                CodeEditor.Focus();
                e.Handled = true;
            }
        }

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            SearchBox.SelectAll();
            _lastSearchOffset = CodeEditor.CaretOffset;
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            var content = CodeEditor.Text;
            if (!string.IsNullOrEmpty(content))
            {
                System.Windows.Clipboard.SetText(content);
                StatusText.Text = "Copied to clipboard";
            }
        }

        private void ViewModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            _currentViewMode = ViewModeCombo.SelectedIndex == 0 ? ViewMode.IL : ViewMode.DecompiledCSharp;
            _displayedMethods.Clear();
            _accumulatedContent.Clear();
            _currentMainMethodKey = null;
            _currentLoadedType = null;
            ClearNavigationHistory();

            // Update syntax highlighting for new mode
            UpdateSyntaxHighlighting();

            StatusText.Text = "Switching mode...";
            _ = RefreshContentAsync();
        }

        private void RefreshModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            _autoRefresh = RefreshModeCombo.SelectedIndex == 0;
        }

        private void LineWrapCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            _wordWrapEnabled = LineWrapCombo.SelectedIndex == 0;
            CodeEditor.WordWrap = _wordWrapEnabled;
        }

        public int OnSelectionChanged(IVsHierarchy pHierOld, uint itemidOld, IVsMultiItemSelect pMISOld,
            ISelectionContainer pSCOld, IVsHierarchy pHierNew, uint itemidNew, IVsMultiItemSelect pMISNew,
            ISelectionContainer pSCNew)
        {
            if (!_servicesInitialized) return VSConstants.S_OK;

            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                await AttachToActiveDocumentAsync();
                // Trigger refresh when switching documents (attaching fires caret event,
                // but also explicitly refresh in case auto-refresh is off or initial load)
                if (_caretService.HasTextView && _autoRefresh)
                {
                    await RefreshContentAsync();
                }
            });
            return VSConstants.S_OK;
        }

        public int OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
        {
            return VSConstants.S_OK;
        }

        public int OnCmdUIContextChanged(uint dwCmdUICookie, int fActive)
        {
            return VSConstants.S_OK;
        }

        #region Call Hierarchy Tree

        private void CallTreeToggle_Click(object sender, RoutedEventArgs e)
        {
            var isVisible = CallTreeToggle.IsChecked == true;
            CallTreePanel.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            CallTreeSplitter.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            CallTreeColumn.Width = isVisible ? new GridLength(220) : new GridLength(0);

            if (isVisible)
            {
                BuildCallHierarchy();
            }
        }

        private void BuildCallHierarchy()
        {
            CallTree.Items.Clear();

            var content = _accumulatedContent.ToString();
            if (string.IsNullOrEmpty(content)) return;

            // Extract all method references from the displayed content
            var methodRefs = ExtractAllMethodReferences(content);
            if (methodRefs.Count == 0) return;

            // Get the root method name from navigation history
            var rootName = _navigationHistory.Count > 0
                ? GetShortMethodName(_navigationHistory[0].TypeName, _navigationHistory[0].MethodName)
                : "Current Method";

            var rootItem = CreateTreeItem(rootName, null, true);
            CallTree.Items.Add(rootItem);

            // Group method references by type
            var grouped = new Dictionary<string, List<(string methodRef, string shortName)>>();
            foreach (var methodRef in methodRefs)
            {
                var (typeName, methodName) = ParseMethodReference(methodRef);
                if (typeName == null || methodName == null) continue;

                var shortType = GetShortTypeName(typeName);
                if (!grouped.ContainsKey(shortType))
                {
                    grouped[shortType] = new List<(string, string)>();
                }

                var shortName = $"{shortType}.{methodName}";
                // Deduplicate within the same type
                if (!grouped[shortType].Any(m => m.shortName == shortName))
                {
                    grouped[shortType].Add((methodRef, shortName));
                }
            }

            // Add to tree grouped by type
            foreach (var group in grouped.OrderBy(g => g.Key))
            {
                if (group.Value.Count == 1)
                {
                    // Single method from this type - add directly
                    var (methodRef, shortName) = group.Value[0];
                    var item = CreateTreeItem(shortName, methodRef, false);
                    rootItem.Items.Add(item);
                }
                else
                {
                    // Multiple methods from this type - group under type node
                    var typeItem = CreateTreeItem(group.Key, null, false);
                    typeItem.IsExpanded = true; // Keep type groups expanded
                    foreach (var (methodRef, shortName) in group.Value)
                    {
                        var (_, methodName) = ParseMethodReference(methodRef);
                        var item = CreateTreeItem(methodName ?? shortName, methodRef, false);
                        typeItem.Items.Add(item);
                    }
                    rootItem.Items.Add(typeItem);
                }
            }

            rootItem.IsExpanded = true;
        }

        private TreeViewItem CreateTreeItem(string displayText, string? methodReference, bool isRoot)
        {
            var item = new TreeViewItem
            {
                Tag = methodReference,
                IsExpanded = isRoot,
                Cursor = methodReference != null ? Cursors.Hand : Cursors.Arrow
            };

            var textBlock = new TextBlock
            {
                Text = displayText,
                FontSize = 11,
                Foreground = methodReference != null
                    ? (Brush)MethodReferenceColorizer.GetLinkBrush()
                    : (Brush)SystemColors.ControlTextBrush
            };

            if (isRoot)
            {
                textBlock.FontWeight = FontWeights.Bold;
                textBlock.Foreground = SystemColors.ControlTextBrush;
            }

            // Direct click handler on the text so it always fires (not just on selection change)
            if (methodReference != null)
            {
                textBlock.TextDecorations = TextDecorations.Underline;
                textBlock.MouseLeftButtonUp += (s, e) =>
                {
                    OnMethodLinkClickedAsync(methodReference);
                    e.Handled = true;
                };
            }

            item.Header = textBlock;
            return item;
        }

        private const int MaxMethodReferences = 200;

        private List<string> ExtractAllMethodReferences(string content)
        {
            MatchCollection matches;
            try { matches = MethodReferencePatterns.CompiledRegex.Matches(content); }
            catch (RegexMatchTimeoutException) { return new List<string>(); }

            var refs = new HashSet<string>();

            foreach (Match match in matches)
            {
                var value = match.Value;
                // Skip self-references and common framework noise
                if (!value.Contains("System.Diagnostics.Debug") &&
                    !value.Contains("System.Runtime.CompilerServices.AsyncTaskMethodBuilder"))
                {
                    refs.Add(value);
                    if (refs.Count >= MaxMethodReferences) break;
                }
            }

            return refs.ToList();
        }

        private string GetShortTypeName(string typeName)
        {
            var lastDot = typeName.LastIndexOf('.');
            var lastSlash = typeName.LastIndexOf('/');
            var lastSep = Math.Max(lastDot, lastSlash);
            if (lastSep >= 0 && lastSep < typeName.Length - 1)
            {
                return typeName.Substring(lastSep + 1);
            }
            return typeName;
        }

        private void CallTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // Navigation is handled by direct click handlers on tree item TextBlocks.
            // This event is kept for XAML binding compatibility but no longer triggers navigation.
        }

        #endregion

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNavIndex <= 0 || _navigationHistory.Count == 0) return;

            _currentNavIndex--;
            ScrollToNavigationEntry(_navigationHistory[_currentNavIndex]);
            UpdateBreadcrumbs();
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNavIndex >= _navigationHistory.Count - 1) return;

            _currentNavIndex++;
            ScrollToNavigationEntry(_navigationHistory[_currentNavIndex]);
            UpdateBreadcrumbs();
        }

        private void NavigateToEntry(NavigationEntry entry)
        {
            var index = _navigationHistory.IndexOf(entry);
            if (index < 0) return;

            _currentNavIndex = index;
            ScrollToNavigationEntry(entry);
            UpdateBreadcrumbs();
        }

        private void ScrollToNavigationEntry(NavigationEntry entry)
        {
            if (entry.LineOffset >= 0 && entry.LineOffset < CodeEditor.Document.LineCount)
            {
                var line = CodeEditor.Document.GetLineByNumber(entry.LineOffset + 1);
                CodeEditor.ScrollToLine(entry.LineOffset + 1);
                CodeEditor.CaretOffset = line.Offset;
            }
        }

        private void AddNavigationEntry(string typeName, string methodName, string fullName, int lineOffset)
        {
            // Truncate forward history if we navigated back and now go forward
            if (_currentNavIndex < _navigationHistory.Count - 1)
            {
                _navigationHistory.RemoveRange(_currentNavIndex + 1, _navigationHistory.Count - _currentNavIndex - 1);
            }

            var entry = new NavigationEntry
            {
                TypeName = typeName,
                MethodName = methodName,
                FullName = fullName,
                LineOffset = lineOffset
            };

            _navigationHistory.Add(entry);
            _currentNavIndex = _navigationHistory.Count - 1;
            UpdateBreadcrumbs();
        }

        private void ClearNavigationHistory()
        {
            _navigationHistory.Clear();
            _currentNavIndex = -1;
            UpdateBreadcrumbs();
        }

        private void UpdateBreadcrumbs()
        {
            BreadcrumbItems.Items.Clear();

            if (_navigationHistory.Count == 0)
            {
                BreadcrumbBar.Visibility = Visibility.Collapsed;
                return;
            }

            BreadcrumbBar.Visibility = Visibility.Visible;
            BackButton.IsEnabled = _currentNavIndex > 0;
            ForwardButton.IsEnabled = _currentNavIndex < _navigationHistory.Count - 1;

            for (int i = 0; i < _navigationHistory.Count; i++)
            {
                var entry = _navigationHistory[i];
                var isCurrent = (i == _currentNavIndex);

                // Add separator arrow between items
                if (i > 0)
                {
                    BreadcrumbItems.Items.Add(new TextBlock
                    {
                        Text = " \u25B8 ",
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = SystemColors.GrayTextBrush,  // Respects VS theme
                        FontSize = 11
                    });
                }

                var displayName = GetShortMethodName(entry.TypeName, entry.MethodName);
                var button = new Button
                {
                    Content = displayName,
                    Tag = entry,
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(0),
                    FontSize = 11,
                    FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal,
                    Cursor = Cursors.Hand,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = isCurrent
                        ? SystemColors.ControlTextBrush
                        : MethodReferenceColorizer.GetLinkBrush(),
                };

                if (!isCurrent)
                {
                    button.Click += (s, args) =>
                    {
                        if (s is Button b && b.Tag is NavigationEntry navEntry)
                        {
                            NavigateToEntry(navEntry);
                        }
                    };
                }

                BreadcrumbItems.Items.Add(button);
            }
        }

        private string GetShortMethodName(string typeName, string methodName)
        {
            // Get just the last part of the type name for brevity
            var shortType = typeName;
            var lastDot = typeName.LastIndexOf('.');
            var lastSlash = typeName.LastIndexOf('/');
            var lastSep = Math.Max(lastDot, lastSlash);
            if (lastSep >= 0 && lastSep < typeName.Length - 1)
            {
                shortType = typeName.Substring(lastSep + 1);
            }

            return $"{shortType}.{methodName}";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ThreadHelper.ThrowIfNotOnUIThread();

            if (_monitorSelection != null && _selectionEventsCookie != 0)
            {
                _monitorSelection.UnadviseSelectionEvents(_selectionEventsCookie);
            }

            _refreshCts?.Cancel();
            _refreshCts?.Dispose();

            if (_caretService != null)
            {
                _caretService.CaretPositionChanged -= OnCaretPositionChanged;
                _caretService.Dispose();
            }

            _assemblyService?.Dispose();
        }
    }

    public class NavigationEntry
    {
        public string TypeName { get; set; } = string.Empty;
        public string MethodName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public int LineOffset { get; set; }
    }
}
