using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

namespace VSILViewer.Services
{
    public class DecompilationService
    {
        private static readonly string TempDirectory = Path.Combine(Path.GetTempPath(), "VSILViewer");

        /// <summary>
        /// Creates a temp file with an unpredictable name in a dedicated subdirectory.
        /// </summary>
        private static string CreateSecureTempFile()
        {
            if (!Directory.Exists(TempDirectory))
            {
                Directory.CreateDirectory(TempDirectory);
            }
            return Path.Combine(TempDirectory, $"{Guid.NewGuid():N}.dll");
        }

        public string? DecompileMethod(Stream assemblyStream, string typeName, string methodName, string? fullSignature = null)
        {
            string? tempFile = null;
            try
            {
                assemblyStream.Seek(0, SeekOrigin.Begin);

                // CSharpDecompiler needs a file path — use GUID name in dedicated subdirectory
                tempFile = CreateSecureTempFile();
                using (var fileStream = File.Create(tempFile))
                {
                    assemblyStream.CopyTo(fileStream);
                }

                var settings = new DecompilerSettings
                {
                    ThrowOnAssemblyResolveErrors = false,
                    ShowXmlDocumentation = false
                };

                // Use the file path constructor
                var decompiler = new CSharpDecompiler(tempFile, settings);

                var typeDefinition = decompiler.TypeSystem.MainModule.TypeDefinitions
                    .FirstOrDefault(t => MatchesTypeName(t, typeName));

                if (typeDefinition == null)
                {
                    var availableTypes = decompiler.TypeSystem.MainModule.TypeDefinitions
                        .Take(10)
                        .Select(t => t.FullName);
                    return $"// Type '{typeName}' not found in assembly\n// Available types (including compiler-generated):\n// {string.Join("\n// ", availableTypes)}";
                }

                // Parse parameter types from full signature for overload disambiguation
                var signatureParamTypes = ParseParameterTypes(fullSignature);

                IMethod? bestMatch = null;
                foreach (var m in typeDefinition.Methods)
                {
                    if (MatchesMethodName(m, methodName))
                    {
                        if (signatureParamTypes == null)
                        {
                            bestMatch = m;
                            break;
                        }

                        if (MatchesParameterTypes(m, signatureParamTypes))
                        {
                            bestMatch = m;
                            break;
                        }

                        // Keep first name match as fallback
                        if (bestMatch == null)
                            bestMatch = m;
                    }
                }

                if (bestMatch == null)
                {
                    var availableMethods = typeDefinition.Methods
                        .Take(10)
                        .Select(m => m.Name);
                    return $"// Method '{methodName}' not found in type '{typeName}'\n// Available methods (including compiler-generated):\n// {string.Join("\n// ", availableMethods)}";
                }

                var methodHandle = (MethodDefinitionHandle)bestMatch.MetadataToken;
                var decompiledCode = decompiler.DecompileAsString(methodHandle);

                // List compiler-generated types as clickable links (Ctrl+Click to view on demand)
                var relatedTypes = FindRelatedCompilerGeneratedTypes(decompiler, typeDefinition, bestMatch.Name);
                if (relatedTypes.Any())
                {
                    decompiledCode += "\n\n// " + new string('=', 80);
                    decompiledCode += "\n// COMPILER-GENERATED TYPES — Ctrl+Click to view full source";
                    decompiledCode += "\n// " + new string('=', 80) + "\n";

                    foreach (var relatedType in relatedTypes)
                    {
                        decompiledCode += $"\n// Type: {relatedType.FullName}";
                        foreach (var relatedMethod in relatedType.Methods.Where(m => !m.IsAbstract))
                        {
                            decompiledCode += $"\n//   {relatedType.FullName}::{relatedMethod.Name}";
                        }
                    }
                }

                return decompiledCode;
            }
            catch (Exception ex)
            {
                return $"// Error decompiling: {ex.Message}\n// {ex.GetType().Name}";
            }
            finally
            {
                // Clean up temp file
                if (tempFile != null && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        /// <summary>
        /// Decompiles an entire type. Returns null if the type is not found or has too many methods.
        /// </summary>
        public string? DecompileType(Stream assemblyStream, string typeName, int maxMethods = 50)
        {
            string? tempFile = null;
            try
            {
                assemblyStream.Seek(0, SeekOrigin.Begin);

                tempFile = CreateSecureTempFile();
                using (var fileStream = File.Create(tempFile))
                {
                    assemblyStream.CopyTo(fileStream);
                }

                var settings = new DecompilerSettings
                {
                    ThrowOnAssemblyResolveErrors = false,
                    ShowXmlDocumentation = false
                };

                var decompiler = new CSharpDecompiler(tempFile, settings);

                var allTypes = decompiler.TypeSystem.MainModule.TypeDefinitions.ToList();
                System.Diagnostics.Debug.WriteLine($"VSILViewer DecompileType: Searching for '{typeName}' among {allTypes.Count} types");

                var typeDefinition = allTypes.FirstOrDefault(t => MatchesTypeName(t, typeName));

                if (typeDefinition == null)
                {
                    var typeNames = string.Join(", ", allTypes.Take(10).Select(t => t.FullName));
                    System.Diagnostics.Debug.WriteLine($"VSILViewer DecompileType: Type NOT found. First types: {typeNames}");
                    return null;
                }

                var methodCount = typeDefinition.Methods.Count(m => !m.IsAbstract);
                System.Diagnostics.Debug.WriteLine($"VSILViewer DecompileType: Found type '{typeDefinition.FullName}' with {methodCount} non-abstract methods");
                if (methodCount > maxMethods) return null; // Too large

                var decompiledCode = decompiler.DecompileTypeAsString(typeDefinition.FullTypeName);

                // List compiler-generated types as clickable links
                var relatedTypes = FindRelatedCompilerGeneratedTypes(decompiler, typeDefinition, null);
                if (relatedTypes.Any())
                {
                    decompiledCode += "\n\n// " + new string('=', 80);
                    decompiledCode += "\n// COMPILER-GENERATED TYPES — Ctrl+Click to view full source";
                    decompiledCode += "\n// " + new string('=', 80) + "\n";

                    foreach (var relatedType in relatedTypes)
                    {
                        decompiledCode += $"\n// Type: {relatedType.FullName}";
                        foreach (var relatedMethod in relatedType.Methods.Where(m => !m.IsAbstract))
                        {
                            decompiledCode += $"\n//   {relatedType.FullName}::{relatedMethod.Name}";
                        }
                    }
                }

                return decompiledCode;
            }
            catch (Exception ex)
            {
                return $"// Error decompiling type: {ex.Message}\n// {ex.GetType().Name}";
            }
            finally
            {
                if (tempFile != null && File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        private bool MatchesMethodName(IMethod method, string searchMethodName)
        {
            // Direct match
            if (method.Name == searchMethodName) return true;

            // Strip generic arity from both sides
            var methodName = method.Name;
            var backtickIndex = methodName.IndexOf('`');
            if (backtickIndex > 0)
            {
                if (methodName.Substring(0, backtickIndex) == searchMethodName) return true;
            }

            var searchBacktick = searchMethodName.IndexOf('`');
            if (searchBacktick > 0)
            {
                var searchWithoutArity = searchMethodName.Substring(0, searchBacktick);
                if (methodName == searchWithoutArity) return true;
                if (backtickIndex > 0 && methodName.Substring(0, backtickIndex) == searchWithoutArity) return true;
            }

            // Handle compiler-generated method names - normalize angle brackets
            var normalizedMethod = methodName.Replace("<", "").Replace(">", "");
            var normalizedSearch = searchMethodName.Replace("<", "").Replace(">", "");
            if (normalizedMethod == normalizedSearch) return true;

            return false;
        }

        private static List<string>? ParseParameterTypes(string? fullSignature)
        {
            if (string.IsNullOrEmpty(fullSignature)) return null;

            var parenStart = fullSignature.IndexOf('(');
            var parenEnd = fullSignature.LastIndexOf(')');
            if (parenStart < 0 || parenEnd <= parenStart) return null;

            var paramString = fullSignature.Substring(parenStart + 1, parenEnd - parenStart - 1).Trim();
            if (string.IsNullOrEmpty(paramString)) return new List<string>();

            return SplitGenericAwareParameters(paramString);
        }

        /// <summary>
        /// Splits parameter list by commas while respecting nested generic brackets.
        /// "string, Dictionary&lt;string, int&gt;, List&lt;int&gt;" → ["string", "Dictionary&lt;string, int&gt;", "List&lt;int&gt;"]
        /// </summary>
        private static List<string> SplitGenericAwareParameters(string paramString)
        {
            var result = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < paramString.Length; i++)
            {
                if (paramString[i] == '<') depth++;
                else if (paramString[i] == '>') depth--;
                else if (paramString[i] == ',' && depth == 0)
                {
                    result.Add(paramString.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            result.Add(paramString.Substring(start).Trim());
            return result;
        }

        private static bool MatchesParameterTypes(IMethod method, List<string> expectedTypes)
        {
            if (method.Parameters.Count != expectedTypes.Count) return false;

            for (int i = 0; i < expectedTypes.Count; i++)
            {
                var paramType = method.Parameters[i].Type;
                var expected = expectedTypes[i];

                if (expected.StartsWith("ref ")) expected = expected.Substring(4);
                if (expected.StartsWith("out ")) expected = expected.Substring(4);
                if (expected.StartsWith("in ")) expected = expected.Substring(3);

                var typeName = paramType.FullName;
                if (paramType is ByReferenceType byRef)
                    typeName = byRef.ElementType.FullName;

                if (!TypeNamesMatch(typeName, expected))
                    return false;
            }
            return true;
        }

        private static readonly Dictionary<string, string> RoslynToClrTypeMap = new Dictionary<string, string>
        {
            {"string", "System.String"}, {"int", "System.Int32"}, {"long", "System.Int64"},
            {"bool", "System.Boolean"}, {"double", "System.Double"}, {"float", "System.Single"},
            {"byte", "System.Byte"}, {"sbyte", "System.SByte"}, {"short", "System.Int16"},
            {"ushort", "System.UInt16"}, {"uint", "System.UInt32"}, {"ulong", "System.UInt64"},
            {"char", "System.Char"}, {"decimal", "System.Decimal"}, {"object", "System.Object"},
            {"void", "System.Void"}
        };

        private static bool TypeNamesMatch(string decompilerName, string roslynName)
        {
            if (decompilerName == roslynName) return true;

            if (RoslynToClrTypeMap.TryGetValue(roslynName, out var clrName) && decompilerName == clrName) return true;

            var dcShort = decompilerName.Contains('.') ? decompilerName.Substring(decompilerName.LastIndexOf('.') + 1) : decompilerName;
            var rnShort = roslynName.Contains('.') ? roslynName.Substring(roslynName.LastIndexOf('.') + 1) : roslynName;
            if (dcShort == rnShort) return true;

            if (decompilerName.EndsWith("[]") && roslynName.EndsWith("[]"))
                return TypeNamesMatch(decompilerName.TrimEnd('[', ']'), roslynName.TrimEnd('[', ']'));

            return false;
        }

        private bool MatchesTypeName(ITypeDefinition type, string searchTypeName)
        {
            // Normalize nested type separators: Cecil uses /, reflection uses +, ICSharpCode uses .
            var normalizedSearch = searchTypeName.Replace("/", ".").Replace("+", ".");
            var normalizedFullName = type.FullName.Replace("/", ".").Replace("+", ".");
            var normalizedName = type.Name;

            // Direct match
            if (normalizedName == searchTypeName || normalizedName == normalizedSearch)
                return true;
            if (normalizedFullName == normalizedSearch || type.FullName == searchTypeName)
                return true;

            // Match with namespace
            if (normalizedFullName.EndsWith("." + normalizedSearch))
                return true;

            // Handle nested types: search may contain parent type (e.g., "OuterClass.<Inner>d__0")
            // Check if the last segment matches
            var lastSep = normalizedSearch.LastIndexOf('.');
            if (lastSep >= 0)
            {
                var searchLastPart = normalizedSearch.Substring(lastSep + 1);
                if (normalizedName == searchLastPart && normalizedFullName.EndsWith(normalizedSearch))
                    return true;
            }

            // Handle generic types - remove arity from type name
            var typeName = type.Name;
            var backtickIndex = typeName.IndexOf('`');
            if (backtickIndex > 0)
            {
                var nameWithoutArity = typeName.Substring(0, backtickIndex);
                if (nameWithoutArity == searchTypeName || nameWithoutArity == normalizedSearch)
                    return true;
            }

            // Try full name without arity
            var fullName = type.FullName;
            backtickIndex = fullName.IndexOf('`');
            if (backtickIndex > 0)
            {
                var fullNameWithoutArity = fullName.Substring(0, backtickIndex).Replace("/", ".").Replace("+", ".");
                if (fullNameWithoutArity == normalizedSearch || fullNameWithoutArity.EndsWith("." + normalizedSearch))
                    return true;
            }

            return false;
        }

        private List<ITypeDefinition> FindRelatedCompilerGeneratedTypes(CSharpDecompiler decompiler, ITypeDefinition containingType, string? methodName)
        {
            var relatedTypes = new List<ITypeDefinition>();

            // Look for nested types that are compiler-generated and related to this method
            foreach (var nestedType in containingType.NestedTypes)
            {
                var typeName = nestedType.Name;

                // Compiler-generated types start with <
                if (typeName.StartsWith("<"))
                {
                    // If methodName is null, return all compiler-generated types (for full type decompilation)
                    if (methodName == null || typeName.Contains(methodName))
                    {
                        relatedTypes.Add(nestedType);
                    }
                }
            }

            return relatedTypes;
        }
    }
}
