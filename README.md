# VSILViewer - IL Viewer for Visual Studio

A Visual Studio 2022 extension that displays IL (Intermediate Language) bytecode and decompiled C# for the method at your cursor position. Features real-time auto-refresh, Ctrl+Click navigation, full class decompilation, call hierarchy, and breadcrumb navigation.

## Features

### Core Viewing
- **Auto-refresh** IL/C# as your cursor moves between methods (300ms debounce)
- **Full class decompilation** (up to 50 methods), falls back to single method
- **Two modes**: IL bytecode and Decompiled C#
- **Type-level tracking**: Skips reload when moving within the same class

### Navigation
- **Ctrl+Click**: Navigate to method references in IL — loads full class, scrolls to method
- **F12**: Keyboard navigation to method reference at caret
- **Back/Forward**: Alt+Left / Alt+Right breadcrumb navigation
- **Breadcrumb trail**: Click any entry to jump back
- **Call hierarchy tree**: Toggle sidebar showing all method references (resizable via splitter)
- **Compiler-generated types**: Listed as clickable links, decompiled on-demand

### Member Detection
Supports methods, constructors, property accessors, destructors/finalizers, operators, conversion operators, indexers, and event accessors.

### IL Mode Enhancements
- Rich method headers: max stack, code size, locals count, exception handlers, branch/call counts
- Local variable declarations with types
- Inline try/catch/finally boundary annotations
- Compiler-generated types listed as clickable references

### Overload Resolution
- Full parameter signature matching from Roslyn through the entire chain
- Bracket-aware generic parameter splitting
- C# alias matching (`string` ↔ `System.String`, etc.)

### UX
- **Search box**: Enter=next, Shift+Enter=prev, Esc=close; case-insensitive, wraps around
- **Ctrl+F**: Focuses the toolbar search box
- **Copy**: Toolbar button (Ctrl+Shift+C) copies all content to clipboard
- **F5**: Refresh
- **Tooltips**: On all toolbar controls
- **GridSplitter**: Resizable call tree panel

### Visual Studio Theme Integration
- Editor colors synced from VS environment
- Auto-updates on theme change (live dark/light switching)
- All UI elements use shared theme-aware brushes

### Security
- Stream copies from cache (no shared stream race condition)
- Compiled regex with 200ms timeout (ReDoS mitigation)
- GUID-based temp files in dedicated subdirectory
- Cache limits: 50MB per stream, 100MB total, max 3 entries

## Technology Stack

| Component | Technology |
|-----------|-----------|
| Host | Visual Studio 2022 (17.x–18.x) |
| Framework | .NET Framework 4.7.2 |
| IL Extraction | Mono.Cecil |
| C# Decompilation | ICSharpCode.Decompiler |
| Code Editor | AvalonEdit (WPF) |
| Roslyn Integration | Microsoft.CodeAnalysis + LanguageServices |

## Building

**Important**: This is an old-style .csproj. You must use MSBuild directly — `dotnet build` will not work.

```bash
MSBuild.exe VSILViewer\VSILViewer\VSILViewer.csproj -t:Build -p:Configuration=Debug -verbosity:minimal
```

Output: `VSILViewer\VSILViewer\bin\Debug\VSILViewer.vsix`

## Installation

1. Build the project (see above)
2. Double-click the generated `.vsix` file to install
3. Restart Visual Studio
4. Open **View > Other Windows > IL Viewer**

## Usage

1. Open any C# file in Visual Studio
2. Place your cursor inside a method
3. The IL Viewer panel automatically shows IL or decompiled C#
4. Use the toolbar dropdown to switch between IL and C# modes
5. Ctrl+Click any method reference in IL to navigate to it
6. Use the breadcrumb trail to navigate back

## License

MIT
