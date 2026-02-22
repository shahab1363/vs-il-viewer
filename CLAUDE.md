# VSILViewer - Development Guide

## Build System

**CRITICAL**: This is an old-style .csproj (NOT SDK-style). You MUST use MSBuild directly.

```bash
"<path-to-MSBuild>\MSBuild.exe" VSILViewer\VSILViewer\VSILViewer.csproj -t:Build -p:Configuration=Debug -verbosity:minimal
```

- **Do NOT use** `dotnet build` — it will fail with 123+ errors
- Use dash prefix (`-t:Build`) not slash (`/t:Build`) when running from bash/terminal
- VSIX output: `VSILViewer\VSILViewer\bin\Debug\VSILViewer.vsix`
- Target framework: .NET Framework 4.7.2

## Project Structure

```
VSILViewer/VSILViewer/
├── ILViewerControl.xaml.cs      # Main UI control (~1500 lines) - ALL enhancement logic
├── Services/
│   ├── AssemblyInspectionService.cs  # Roslyn compile + cache + stream copy + SolutionChanged
│   ├── DecompilationService.cs       # ICSharpCode decompiler (C# mode)
│   ├── ILExtractionService.cs        # Mono.Cecil (IL mode) + rich metrics + exception annotations
│   ├── MethodLocatorService.cs       # Roslyn: methods, ctors, properties, operators, events, indexers
│   └── CaretPositionService.cs       # VS text view tracking (300ms debounce)
├── Models/                           # DTOs: MethodViewInfo, ViewMode
└── ILViewerToolWindow.cs             # ToolWindowPane host
```

## Critical Patterns

1. **Nested type separators differ across libraries**: Cecil uses `/`, ICSharpCode uses `.` or `+`, Roslyn uses `.`, Reflection uses `+`. All `MatchesType`/`MatchesTypeName` methods normalize these.

2. **Assembly stream copies**: `AssemblyInspectionService` caches MemoryStreams and returns independent copies via `CreateStreamCopy`. Cecil calls still use `ReadingMode.Immediate`.

3. **Method reference regex**: Single shared instance at `MethodReferencePatterns.CompiledRegex` with 200ms timeout. Used by colorizer, ExtractMethodReference, and ExtractAllMethodReferences.

4. **ParseMethodReference** uses `StripTrailingGenericArgs` (bracket-depth-aware) to handle nested generics. Preserves `<>` in compiler-generated names like `<ProcessAsync>d__0`.

5. **VS theme sync**: `ApplyVsThemeColors()` reads VS environment colors and applies to AvalonEdit. Subscribes to `VSColorTheme.ThemeChanged`. Link colors shared via `MethodReferenceColorizer.GetLinkBrush()`.

6. **Type-level vs method-level tracking**: `_currentLoadedType` tracks full class loads, `_currentMainMethodKey` tracks single method loads. Both must be cleared on Clear/Refresh/ViewMode change.

## Pre-existing Warnings (Safe to Ignore)

- CS8602/CS8604: Nullable reference warnings
- VSTHRD100/200: Async void methods (required for WPF event handlers)
- VSTHRD010: Main thread access (code is correct, analyzer can't track SwitchToMainThreadAsync)
- VSIXCompatibility1001: BrowserLink compatibility (irrelevant)

## Handoff Protocol

When finishing a session, create a handoff file in `docs/handoff/` with:
- Date prefix: `YYYY-MM-DD-vX.Y.Z-description.md`
- All changes made, approaches taken, issues found
- Pending items that need user testing
- Update this CLAUDE.md if the build system or project structure changes

Note: Handoff files in `docs/handoff/` are gitignored (local-only development notes).
