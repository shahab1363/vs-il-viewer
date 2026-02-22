# Visual Studio IL Viewer Extension Design

## Overview

A Visual Studio 2022 extension that displays IL (Intermediate Language) code and decompiled C# for the method the user's cursor is in. Based on the existing VS Code extension in `VSCodeILViewer/`.

## Goals

- Show IL or decompiled C# for the current method in a tool window panel
- Auto-update as cursor moves between methods (with manual mode option)
- Lazy-load IL/C# for collapsed methods to maintain performance
- Use VS's built-in Roslyn workspace for large project support
- Leverage VS's editor for proper C# syntax highlighting

## Architecture

### Project Structure

```
VSILViewer/
├── VSILViewer.sln
├── VSILViewer/
│   ├── VSILViewerPackage.cs           # Extension entry point
│   ├── ILViewerToolWindow.cs          # Tool window definition
│   ├── ILViewerControl.xaml/.cs       # Main WPF UI, hosts both view modes
│   ├── Views/
│   │   ├── ILTextView.cs              # WPF TextBlock for IL display
│   │   └── CSharpEditorHost.cs        # VS IWpfTextViewHost wrapper
│   ├── Services/
│   │   ├── AssemblyInspectionService.cs   # Emits compilation, caches assembly
│   │   ├── ILExtractionService.cs         # Reads IL via Mono.Cecil
│   │   ├── DecompilationService.cs        # Decompiles via ICSharpCode
│   │   ├── CaretPositionService.cs        # Tracks cursor position
│   │   └── MethodLocatorService.cs        # Finds method at cursor
│   ├── Models/
│   │   ├── MethodViewInfo.cs              # Method name + content
│   │   └── ViewMode.cs                    # Enum: IL, DecompiledCSharp
│   └── source.extension.vsixmanifest
```

### Dependencies

| Package | Purpose |
|---------|---------|
| Microsoft.VisualStudio.SDK (17.x) | VS extension APIs |
| Microsoft.VisualStudio.LanguageServices | Access Roslyn workspace |
| Microsoft.VisualStudio.Text | Editor hosting for C# view |
| Mono.Cecil | Read IL from emitted assembly |
| ICSharpCode.Decompiler | Decompile to C# |

## Core Workflow

1. **User opens IL Viewer panel** (View menu → Other Windows → IL Viewer)

2. **CaretPositionService** subscribes to VS editor events:
   - Listens for `ITextView.Caret.PositionChanged`
   - Debounces rapid cursor movements (300ms delay)
   - Triggers update when cursor settles in a new method

3. **MethodLocatorService** finds the method at cursor:
   - Uses Roslyn's `Document.GetSyntaxRootAsync()` from VS workspace
   - Walks syntax tree to find containing `MethodDeclarationSyntax`
   - Collects sibling methods in the same class (for collapsed sections)

4. **AssemblyInspectionService** manages compilation:
   - Gets `Compilation` from VS's `VisualStudioWorkspace`
   - Emits to `MemoryStream` (cached, invalidated on code changes)
   - Provides stream to IL or Decompilation services

5. **ILExtractionService** or **DecompilationService** extracts content:
   - IL: Uses Mono.Cecil to read instructions
   - C#: Uses ICSharpCode.Decompiler to decompile method

6. **ILViewerControl** displays:
   - Current method expanded
   - Other methods in class shown as collapsed headers
   - Expanding triggers lazy extraction for that method

## UI Design

```
┌─────────────────────────────────────────────────────────┐
│ IL Viewer           [IL ▼] [Auto ▼] [↻]              [×]│
├─────────────────────────────────────────────────────────┤
│ MyClass.cs                                              │
├─────────────────────────────────────────────────────────┤
│ ▶ .ctor()                                               │
│ ▶ GetValue(int)                                         │
│ ▼ ProcessData(string)  ◀── current method, expanded     │
│ ┌─────────────────────────────────────────────────────┐ │
│ │ IL_0000: ldarg.0                                    │ │
│ │ IL_0001: ldfld string MyClass::_data                │ │
│ │ IL_0006: ldarg.1                                    │ │
│ │ IL_0007: call string String::Concat(string, string) │ │
│ │ IL_000c: stloc.0                                    │ │
│ │ IL_000d: ldloc.0                                    │ │
│ │ IL_000e: ret                                        │ │
│ └─────────────────────────────────────────────────────┘ │
│ ▶ SaveData()                                            │
│ ▶ Dispose()                                             │
├─────────────────────────────────────────────────────────┤
│ Ready | Method: ProcessData | 7 instructions            │
└─────────────────────────────────────────────────────────┘
```

### Toolbar Elements

| Element | Options | Purpose |
|---------|---------|---------|
| View Mode dropdown | IL, Decompiled C# | Switch display format |
| Refresh Mode dropdown | Auto, Manual | Auto-update or manual refresh |
| Refresh button | - | Manual refresh trigger |

### View Mode Display

| View Mode | Display Component | Syntax Highlighting |
|-----------|-------------------|---------------------|
| IL | WPF TextBlock | Custom: opcodes blue, labels gray, strings green |
| Decompiled C# | VS IWpfTextViewHost | VS built-in C# classification (respects theme) |

### C# Editor Hosting

```csharp
var editorFactory = componentModel.GetService<ITextEditorFactoryService>();
var bufferFactory = componentModel.GetService<ITextBufferFactoryService>();
var contentTypeRegistry = componentModel.GetService<IContentTypeRegistryService>();

var contentType = contentTypeRegistry.GetContentType("CSharp");
var buffer = bufferFactory.CreateTextBuffer(decompiledCode, contentType);

var view = editorFactory.CreateTextViewHost(
    editorFactory.CreateTextView(buffer),
    setFocus: false);
view.TextView.Options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, true);
```

## Error Handling

| Scenario | Behavior |
|----------|----------|
| No file open | Show "Open a C# file to view IL" |
| Non-C# file | Show "IL Viewer only supports C# files" |
| Cursor outside method | Show class-level view, all methods collapsed |
| Compilation errors | Show "Project has errors. Fix and rebuild." |
| Project not built | Attempt emit; if fails, show "Build project to view IL" |
| Abstract/interface method | Show "Method has no body (abstract/interface)" |
| Emit takes too long | Show spinner, allow cancellation, 10s timeout |
| Method not found | Show "Method not found. Rebuild project." |

## Caching Strategy

- Cache emitted assembly per project
- Invalidate on `Workspace.WorkspaceChanged` with document changes
- LRU eviction: keep last 3 project assemblies
- Cache method IL/C# lookups within same assembly version

## Settings Persisted

- View mode (IL / Decompiled C#)
- Refresh mode (Auto / Manual)

## Key Implementation Notes

### Package Registration

```csharp
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideToolWindow(typeof(ILViewerToolWindow))]
[ProvideMenuResource("Menus.ctmenu", 1)]
public sealed class VSILViewerPackage : AsyncPackage
```

### Accessing Roslyn Workspace

```csharp
var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
var workspace = componentModel.GetService<VisualStudioWorkspace>();
```

### Emit and Read IL

```csharp
var compilation = await document.Project.GetCompilationAsync();
using var stream = new MemoryStream();
compilation.Emit(stream);
stream.Seek(0, SeekOrigin.Begin);
var assembly = AssemblyDefinition.ReadAssembly(stream);
```

### Decompile Method

```csharp
var decompiler = new CSharpDecompiler(assemblyStream, new DecompilerSettings {
    ThrowOnAssemblyResolveErrors = false
});
var method = FindMethod(decompiler.TypeSystem, methodFullName);
return decompiler.DecompileAsString(method);
```
