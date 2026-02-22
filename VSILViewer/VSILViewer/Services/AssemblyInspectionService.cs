using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;
using VSILViewer.Models;

namespace VSILViewer.Services
{
    public class AssemblyInspectionService : IDisposable
    {
        private const int MaxCachedAssemblies = 3;
        private const long MaxCacheBytes = 100 * 1024 * 1024; // 100 MB total cache limit
        private const long MaxSingleStreamBytes = 50 * 1024 * 1024; // 50 MB per stream

        private readonly ILExtractionService _ilService = new();
        private readonly DecompilationService _decompilationService = new();
        private readonly ConcurrentDictionary<string, (MemoryStream Stream, DateTime Timestamp)> _assemblyCache = new();
        private readonly object _cacheLock = new();

        private VisualStudioWorkspace? _workspace;
        private bool _disposed;

        public void SetWorkspace(VisualStudioWorkspace workspace)
        {
            // Unsubscribe from previous workspace if any
            if (_workspace != null)
            {
                _workspace.WorkspaceChanged -= OnWorkspaceChanged;
            }

            _workspace = workspace;
            workspace.WorkspaceChanged += OnWorkspaceChanged;
        }

        /// <summary>Raised when the workspace solution changes (e.g., solution switch, close).</summary>
        public event EventHandler? SolutionChanged;

        private void OnWorkspaceChanged(object? sender, WorkspaceChangeEventArgs e)
        {
            if (e.Kind == WorkspaceChangeKind.SolutionChanged ||
                e.Kind == WorkspaceChangeKind.SolutionRemoved ||
                e.Kind == WorkspaceChangeKind.SolutionCleared)
            {
                // Clear entire cache on solution change
                lock (_cacheLock)
                {
                    foreach (var kvp in _assemblyCache)
                        kvp.Value.Stream.Dispose();
                    _assemblyCache.Clear();
                }
                SolutionChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (e.Kind == WorkspaceChangeKind.DocumentChanged ||
                e.Kind == WorkspaceChangeKind.ProjectChanged)
            {
                // Invalidate cache for affected project
                if (e.ProjectId != null)
                {
                    var key = e.ProjectId.Id.ToString();
                    if (_assemblyCache.TryRemove(key, out var removed))
                    {
                        removed.Stream.Dispose();
                    }
                }
            }
        }

        public async Task<string?> GetMethodContentAsync(
            Document document,
            string typeName,
            string methodName,
            ViewMode viewMode,
            CancellationToken cancellationToken = default,
            string? fullSignature = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AssemblyInspectionService));
            var (stream, errorMessage) = await GetOrEmitAssemblyAsync(document.Project, cancellationToken);
            if (stream == null)
            {
                return errorMessage ?? "// Could not compile project.";
            }

            // Use a private copy of the stream to avoid concurrent access issues
            using var streamCopy = CreateStreamCopy(stream);
            var result = viewMode switch
            {
                ViewMode.IL => _ilService.ExtractMethodIL(streamCopy, $"{typeName}.{methodName}", fullSignature),
                ViewMode.DecompiledCSharp => _decompilationService.DecompileMethod(streamCopy, typeName, methodName, fullSignature),
                _ => null
            };

            if (result == null)
            {
                return $"// Method not found: {typeName}.{methodName}\n// Try rebuilding the project.";
            }

            return result;
        }

        /// <summary>
        /// Tries to get content for the entire type. Returns null if the type is too large or not found.
        /// </summary>
        public async Task<string?> GetTypeContentAsync(
            Document document,
            string typeName,
            ViewMode viewMode,
            CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AssemblyInspectionService));
            var (stream, errorMessage) = await GetOrEmitAssemblyAsync(document.Project, cancellationToken);
            if (stream == null)
            {
                System.Diagnostics.Debug.WriteLine($"VSILViewer GetTypeContent: Stream is null, error: {errorMessage}");
                return null;
            }

            System.Diagnostics.Debug.WriteLine($"VSILViewer GetTypeContent: Stream OK ({stream.Length} bytes), looking for type '{typeName}' in mode {viewMode}");

            // Use a private copy of the stream to avoid concurrent access issues
            using var streamCopy = CreateStreamCopy(stream);
            var result = viewMode switch
            {
                ViewMode.IL => _ilService.ExtractTypeIL(streamCopy, typeName),
                ViewMode.DecompiledCSharp => _decompilationService.DecompileType(streamCopy, typeName),
                _ => null
            };

            System.Diagnostics.Debug.WriteLine($"VSILViewer GetTypeContent: Result is {(result == null ? "null" : $"{result.Length} chars")}");
            return result;
        }

        /// <summary>
        /// Creates an independent copy of the cached stream so callers don't race on Seek/Read.
        /// </summary>
        private static MemoryStream CreateStreamCopy(MemoryStream source)
        {
            lock (source)
            {
                return new MemoryStream(source.ToArray());
            }
        }

        private async Task<(MemoryStream? Stream, string? ErrorMessage)> GetOrEmitAssemblyAsync(Project project, CancellationToken cancellationToken)
        {
            var cacheKey = project.Id.Id.ToString();

            // Try to get from cache first (thread-safe read)
            if (_assemblyCache.TryGetValue(cacheKey, out var cached))
            {
                lock (_cacheLock)
                {
                    // Verify it's still in cache after acquiring lock
                    if (_assemblyCache.TryGetValue(cacheKey, out cached))
                    {
                        cached.Stream.Seek(0, SeekOrigin.Begin);
                        return (cached.Stream, null);
                    }
                }
            }

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                return (null, "// Could not get compilation for project.");
            }

            var stream = new MemoryStream();
            var emitResult = compilation.Emit(stream, cancellationToken: cancellationToken);

            if (!emitResult.Success)
            {
                stream.Dispose();
                var errors = emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Take(5)
                    .Select(d => $"// {d.GetMessage()}");
                var errorMsg = $"// Compilation failed with {emitResult.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} error(s):\n{string.Join("\n", errors)}";
                return (null, errorMsg);
            }

            stream.Seek(0, SeekOrigin.Begin);

            // Skip caching if this single stream is too large
            if (stream.Length > MaxSingleStreamBytes)
            {
                return (stream, null);
            }

            // Thread-safe cache insertion with eviction
            lock (_cacheLock)
            {
                // Check if another thread already cached this while we were compiling
                if (_assemblyCache.TryGetValue(cacheKey, out var existing))
                {
                    // Someone else cached it, dispose our copy and use theirs
                    stream.Dispose();
                    existing.Stream.Seek(0, SeekOrigin.Begin);
                    return (existing.Stream, null);
                }

                // Evict oldest if at capacity (count or total size)
                var totalSize = _assemblyCache.Values.Sum(v => v.Stream.Length);
                while (_assemblyCache.Count >= MaxCachedAssemblies || totalSize + stream.Length > MaxCacheBytes)
                {
                    DateTime oldest = DateTime.MaxValue;
                    string? oldestKey = null;

                    foreach (var kvp in _assemblyCache)
                    {
                        if (kvp.Value.Timestamp < oldest)
                        {
                            oldest = kvp.Value.Timestamp;
                            oldestKey = kvp.Key;
                        }
                    }

                    if (oldestKey != null && _assemblyCache.TryRemove(oldestKey, out var removed))
                    {
                        removed.Stream.Dispose();
                    }
                    else
                    {
                        break; // Safety: avoid infinite loop
                    }
                }

                _assemblyCache[cacheKey] = (stream, DateTime.UtcNow);
            }

            return (stream, null);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                // Unsubscribe from workspace events - store in local variable to avoid race condition
                var workspace = _workspace;
                if (workspace != null)
                {
                    workspace.WorkspaceChanged -= OnWorkspaceChanged;
                    _workspace = null;
                }

                // Dispose all cached streams
                lock (_cacheLock)
                {
                    foreach (var kvp in _assemblyCache)
                    {
                        kvp.Value.Stream.Dispose();
                    }
                    _assemblyCache.Clear();
                }
            }

            _disposed = true;
        }
    }
}
