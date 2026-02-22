using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace VSILViewer.Services
{
    public class ILExtractionService
    {
        public string? ExtractMethodIL(Stream assemblyStream, string methodFullName, string? fullSignature = null)
        {
            try
            {
                assemblyStream.Seek(0, SeekOrigin.Begin);
                var readerParams = new ReaderParameters { ReadingMode = ReadingMode.Immediate };
                using var assembly = AssemblyDefinition.ReadAssembly(assemblyStream, readerParams);

                // Parse the input - expected format: "TypeName.MethodName"
                var lastDot = methodFullName.LastIndexOf('.');
                string? searchTypeName = null;
                string? searchMethodName = null;

                if (lastDot > 0)
                {
                    searchTypeName = methodFullName.Substring(0, lastDot);
                    searchMethodName = methodFullName.Substring(lastDot + 1);
                }
                else
                {
                    searchMethodName = methodFullName;
                }

                // Extract parameter types from full signature for overload disambiguation
                var signatureParamTypes = ParseParameterTypes(fullSignature);

                MethodDefinition? bestMatch = null;
                TypeDefinition? bestMatchType = null;

                foreach (var type in assembly.MainModule.GetTypes())
                {
                    if (searchTypeName != null)
                    {
                        if (!MatchesType(type, searchTypeName))
                            continue;
                    }

                    foreach (var method in type.Methods)
                    {
                        if (MatchesMethod(method, searchMethodName!, methodFullName))
                        {
                            if (signatureParamTypes == null)
                            {
                                // No parameter info - return first match (original behavior)
                                return FormatMethodILWithRelatedTypes(method, assembly, type);
                            }

                            if (MatchesParameterTypes(method, signatureParamTypes))
                            {
                                // Exact parameter match - return immediately
                                return FormatMethodILWithRelatedTypes(method, assembly, type);
                            }

                            // Name matches but params don't - keep as fallback
                            if (bestMatch == null)
                            {
                                bestMatch = method;
                                bestMatchType = type;
                            }
                        }
                    }
                }

                // If we had parameter info but no exact match, use the name-only fallback
                if (bestMatch != null && bestMatchType != null)
                {
                    return FormatMethodILWithRelatedTypes(bestMatch, assembly, bestMatchType);
                }

                // Method not found - return helpful info
                var sb = new StringBuilder();
                sb.AppendLine($"// Method not found: {methodFullName}");
                sb.AppendLine($"// Searched for type: {searchTypeName ?? "(any)"}, method: {searchMethodName}");
                sb.AppendLine("//");
                sb.AppendLine("// Available types in assembly (including compiler-generated):");

                var types = assembly.MainModule.GetTypes()
                    .Take(20)
                    .Select(t => $"//   {t.FullName}");
                sb.AppendLine(string.Join("\n", types));

                if (assembly.MainModule.GetTypes().Count() > 20)
                {
                    sb.AppendLine($"//   ... and {assembly.MainModule.GetTypes().Count() - 20} more");
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"// Error extracting IL: {ex.Message}";
            }
        }

        /// <summary>
        /// Extracts IL for all methods in a type. Returns null if type not found or too large.
        /// </summary>
        public string? ExtractTypeIL(Stream assemblyStream, string typeName, int maxMethods = 50)
        {
            try
            {
                assemblyStream.Seek(0, SeekOrigin.Begin);
                var readerParams = new ReaderParameters { ReadingMode = ReadingMode.Immediate };
                using var assembly = AssemblyDefinition.ReadAssembly(assemblyStream, readerParams);

                var allTypes = assembly.MainModule.GetTypes().ToList();
                System.Diagnostics.Debug.WriteLine($"VSILViewer ExtractTypeIL: Searching for '{typeName}' among {allTypes.Count} types");

                var type = allTypes.FirstOrDefault(t => MatchesType(t, typeName));

                if (type == null)
                {
                    var typeNames = string.Join(", ", allTypes.Take(10).Select(t => t.FullName));
                    System.Diagnostics.Debug.WriteLine($"VSILViewer ExtractTypeIL: Type NOT found. First types: {typeNames}");
                    return null;
                }

                var methods = type.Methods.Where(m => m.HasBody).ToList();
                System.Diagnostics.Debug.WriteLine($"VSILViewer ExtractTypeIL: Found type '{type.FullName}' with {methods.Count} methods (max: {maxMethods})");
                if (methods.Count > maxMethods) return null; // Too large, caller should fall back to single method

                var sb = new StringBuilder();
                sb.AppendLine($"// ========== Type: {type.FullName} ({methods.Count} methods) ==========");
                sb.AppendLine();

                foreach (var method in methods)
                {
                    sb.Append(FormatMethodIL(method));
                    sb.AppendLine();
                }

                // Also include compiler-generated types related to this type
                try
                {
                    var relatedTypes = assembly.MainModule.GetTypes()
                        .Where(t => t != type && IsCompilerGeneratedType(t) &&
                               t.FullName.Contains(type.Name))
                        .ToList();

                    if (relatedTypes.Any())
                    {
                        sb.AppendLine("// " + new string('=', 80));
                        sb.AppendLine("// COMPILER-GENERATED TYPES — Ctrl+Click to view full source");
                        sb.AppendLine("// " + new string('=', 80));
                        sb.AppendLine();

                        foreach (var relatedType in relatedTypes)
                        {
                            sb.AppendLine($"// Type: {relatedType.FullName}");
                            foreach (var relatedMethod in relatedType.Methods.Where(m => m.HasBody))
                            {
                                sb.AppendLine($"//   {relatedType.FullName}::{relatedMethod.Name}");
                            }
                        }
                    }
                }
                catch { }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"// Error extracting type IL: {ex.Message}";
            }
        }

        private string FormatMethodILWithRelatedTypes(MethodDefinition method, AssemblyDefinition assembly, TypeDefinition containingType)
        {
            var sb = new StringBuilder();

            // Add the main method IL
            sb.Append(FormatMethodIL(method));

            try
            {
                // Find compiler-generated types related to this method
                var relatedTypes = FindRelatedCompilerGeneratedTypes(method, containingType, assembly);

                if (relatedTypes != null && relatedTypes.Any())
                {
                    sb.AppendLine();
                    sb.AppendLine("// " + new string('=', 80));
                    sb.AppendLine("// COMPILER-GENERATED TYPES — Ctrl+Click to view full source");
                    sb.AppendLine("// " + new string('=', 80));
                    sb.AppendLine();

                    foreach (var relatedType in relatedTypes)
                    {
                        sb.AppendLine($"// Type: {relatedType.FullName}");
                        foreach (var relatedMethod in relatedType.Methods.Where(m => m.HasBody))
                        {
                            // Emit in Type::Method format so Ctrl+Click navigation works
                            sb.AppendLine($"//   {relatedType.FullName}::{relatedMethod.Name}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine();
                sb.AppendLine($"// Note: Could not detect compiler-generated types: {ex.Message}");
            }

            return sb.ToString();
        }

        private List<TypeDefinition> FindRelatedCompilerGeneratedTypes(MethodDefinition method, TypeDefinition containingType, AssemblyDefinition assembly)
        {
            var relatedTypes = new List<TypeDefinition>();

            try
            {
                var methodName = method.Name;

                // Pattern 1: Async state machines - <MethodName>d__N
                // Pattern 2: Iterators - <MethodName>d__N
                // Pattern 3: Display classes (closures) - <>c__DisplayClass_N or <>c__DisplayClassN_0
                // Pattern 4: Cached anonymous methods - <>c

                // Look for nested types in the containing type
                if (containingType?.NestedTypes != null)
                {
                    foreach (var nestedType in containingType.NestedTypes)
                    {
                        try
                        {
                            if (IsRelatedCompilerGeneratedType(nestedType, methodName))
                            {
                                relatedTypes.Add(nestedType);
                            }
                        }
                        catch
                        {
                            // Skip problematic types
                        }
                    }
                }

                // Also check the IL for references to compiler-generated types
                if (method.HasBody && method.Body?.Instructions != null)
                {
                    foreach (var instruction in method.Body.Instructions)
                    {
                        try
                        {
                            if (instruction.Operand is TypeReference typeRef)
                            {
                                var typeDef = typeRef.Resolve();
                                if (typeDef != null && IsCompilerGeneratedType(typeDef) && !relatedTypes.Contains(typeDef))
                                {
                                    relatedTypes.Add(typeDef);
                                }
                            }
                            else if (instruction.Operand is MethodReference methodRef)
                            {
                                var typeDef = methodRef?.DeclaringType?.Resolve();
                                if (typeDef != null && IsCompilerGeneratedType(typeDef) && !relatedTypes.Contains(typeDef))
                                {
                                    relatedTypes.Add(typeDef);
                                }
                            }
                        }
                        catch
                        {
                            // Skip problematic instructions
                        }
                    }
                }
            }
            catch
            {
                // Return empty list on any error
            }

            return relatedTypes;
        }

        private bool IsRelatedCompilerGeneratedType(TypeDefinition type, string methodName)
        {
            var typeName = type.Name;

            // Check if it's compiler-generated
            if (!typeName.StartsWith("<"))
                return false;

            // Check if it contains the method name
            return typeName.Contains(methodName);
        }

        private bool IsCompilerGeneratedType(TypeDefinition type)
        {
            // Compiler-generated types start with < or <>
            return type.Name.StartsWith("<");
        }

        private bool MatchesType(TypeDefinition type, string searchTypeName)
        {
            // Normalize nested type separators: Cecil uses /, reflection uses +
            var normalizedSearch = searchTypeName.Replace("+", "/");
            var normalizedFullName = type.FullName; // Cecil already uses /

            // Try various matching strategies
            if (type.Name == searchTypeName || type.Name == normalizedSearch) return true;
            if (normalizedFullName == searchTypeName || normalizedFullName == normalizedSearch) return true;

            // Handle nested types and namespaces
            if (normalizedFullName.EndsWith("." + normalizedSearch)) return true;
            if (normalizedFullName.EndsWith("/" + normalizedSearch)) return true;

            // Also try with . as nested separator (from ICSharpCode decompiler output)
            var dotNormalizedSearch = searchTypeName.Replace("/", ".");
            var dotNormalizedFullName = normalizedFullName.Replace("/", ".");
            if (dotNormalizedFullName == dotNormalizedSearch) return true;
            if (dotNormalizedFullName.EndsWith("." + dotNormalizedSearch)) return true;

            // Handle generic types - remove arity marker from type name
            var genericIndex = type.Name.IndexOf('`');
            if (genericIndex > 0)
            {
                var nameWithoutArity = type.Name.Substring(0, genericIndex);
                if (nameWithoutArity == searchTypeName) return true;

                var fullName = type.FullName;
                var fullGenericIndex = fullName.IndexOf('`');
                if (fullGenericIndex > 0)
                {
                    var fullNameWithoutArity = fullName.Substring(0, fullGenericIndex);
                    if (fullNameWithoutArity == searchTypeName || fullNameWithoutArity == normalizedSearch) return true;
                    if (fullNameWithoutArity.EndsWith("." + searchTypeName)) return true;
                }
            }

            // Try removing generic arity from search name
            var searchGenericIndex = searchTypeName.IndexOf('`');
            if (searchGenericIndex > 0)
            {
                var searchWithoutArity = searchTypeName.Substring(0, searchGenericIndex);
                return MatchesType(type, searchWithoutArity);
            }

            return false;
        }

        private bool MatchesMethod(MethodDefinition method, string searchMethodName, string fullSearch)
        {
            // Direct name match
            if (method.Name == searchMethodName) return true;

            // Full name match
            if (method.FullName == fullSearch) return true;

            // Type.Method format
            if ($"{method.DeclaringType.Name}.{method.Name}" == fullSearch) return true;

            // Handle generic methods - strip arity from both sides
            var genericIndex = method.Name.IndexOf('`');
            if (genericIndex > 0)
            {
                var nameWithoutArity = method.Name.Substring(0, genericIndex);
                if (nameWithoutArity == searchMethodName) return true;
            }

            // Strip arity from search name too
            var searchGenericIndex = searchMethodName.IndexOf('`');
            if (searchGenericIndex > 0)
            {
                var searchWithoutArity = searchMethodName.Substring(0, searchGenericIndex);
                if (method.Name == searchWithoutArity) return true;
                if (genericIndex > 0 && method.Name.Substring(0, genericIndex) == searchWithoutArity) return true;
            }

            // Handle compiler-generated method names like <Method>b__0
            // Strip angle brackets for comparison
            var normalizedMethodName = method.Name.Replace("<", "").Replace(">", "");
            var normalizedSearch = searchMethodName.Replace("<", "").Replace(">", "");
            if (normalizedMethodName == normalizedSearch) return true;

            return false;
        }

        /// <summary>
        /// Parses parameter type names from a full signature like "TypeName.MethodName(string, System.Net.IPAddress)".
        /// Returns null if no parameter info is available.
        /// </summary>
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

        /// <summary>
        /// Checks whether a Cecil MethodDefinition's parameters match the expected parameter types
        /// from a Roslyn signature. Uses short name matching (e.g., "string" matches "System.String").
        /// </summary>
        private static bool MatchesParameterTypes(MethodDefinition method, List<string> expectedTypes)
        {
            if (method.Parameters.Count != expectedTypes.Count) return false;

            for (int i = 0; i < expectedTypes.Count; i++)
            {
                var cecilType = method.Parameters[i].ParameterType;
                var expected = expectedTypes[i];

                // Remove ref/out/in modifiers for comparison
                var cecilTypeName = cecilType.FullName ?? cecilType.Name;
                if (cecilType.IsByReference)
                    cecilTypeName = cecilType.GetElementType()?.FullName ?? cecilTypeName.TrimEnd('&');

                if (expected.StartsWith("ref ")) expected = expected.Substring(4);
                if (expected.StartsWith("out ")) expected = expected.Substring(4);
                if (expected.StartsWith("in ")) expected = expected.Substring(3);

                // Check full name match, short name match, or last segment match
                if (!TypeNamesMatch(cecilTypeName, expected))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Compares Cecil type name (e.g., "System.String") with Roslyn display name (e.g., "string").
        /// Handles C# keyword aliases, array types, and generic types.
        /// </summary>
        private static readonly Dictionary<string, string> RoslynToClrTypeMap = new Dictionary<string, string>
        {
            {"string", "System.String"}, {"int", "System.Int32"}, {"long", "System.Int64"},
            {"bool", "System.Boolean"}, {"double", "System.Double"}, {"float", "System.Single"},
            {"byte", "System.Byte"}, {"sbyte", "System.SByte"}, {"short", "System.Int16"},
            {"ushort", "System.UInt16"}, {"uint", "System.UInt32"}, {"ulong", "System.UInt64"},
            {"char", "System.Char"}, {"decimal", "System.Decimal"}, {"object", "System.Object"},
            {"void", "System.Void"}
        };

        private static bool TypeNamesMatch(string cecilName, string roslynName)
        {
            if (cecilName == roslynName) return true;

            if (RoslynToClrTypeMap.TryGetValue(roslynName, out var clrName) && cecilName == clrName) return true;

            // Compare last segment (e.g., "IPAddress" matches "System.Net.IPAddress")
            var cecilShort = cecilName.Contains('.') ? cecilName.Substring(cecilName.LastIndexOf('.') + 1) : cecilName;
            var roslynShort = roslynName.Contains('.') ? roslynName.Substring(roslynName.LastIndexOf('.') + 1) : roslynName;
            if (cecilShort == roslynShort) return true;

            // Handle arrays
            if (cecilName.EndsWith("[]") && roslynName.EndsWith("[]"))
                return TypeNamesMatch(cecilName.TrimEnd('[', ']'), roslynName.TrimEnd('[', ']'));

            return false;
        }

        private string FormatMethodIL(MethodDefinition method)
        {
            if (!method.HasBody)
            {
                return "// Method has no body (abstract, extern, or interface method)";
            }

            var sb = new StringBuilder();
            var body = method.Body;
            var instructions = body.Instructions;

            // Method header with rich metrics
            sb.AppendLine($"// {method.FullName}");
            sb.AppendLine($"// Max stack: {body.MaxStackSize}, Code size: {body.CodeSize} bytes, Locals: {(body.HasVariables ? body.Variables.Count : 0)}");

            // Exception handler summary
            if (body.HasExceptionHandlers)
            {
                var tryCatch = body.ExceptionHandlers.Count(h => h.HandlerType == ExceptionHandlerType.Catch);
                var tryFinally = body.ExceptionHandlers.Count(h => h.HandlerType == ExceptionHandlerType.Finally);
                sb.AppendLine($"// Exception handlers: {body.ExceptionHandlers.Count} ({tryCatch} catch, {tryFinally} finally)");
            }

            // Branch complexity
            var branchCount = instructions.Count(i => i.OpCode.FlowControl == FlowControl.Cond_Branch);
            var callCount = instructions.Count(i => i.OpCode.FlowControl == FlowControl.Call);
            if (branchCount > 0 || callCount > 0)
            {
                sb.AppendLine($"// Branches: {branchCount}, Calls: {callCount}");
            }

            sb.AppendLine();

            // Local variable declarations
            if (body.HasVariables)
            {
                sb.AppendLine("// === Local Variables ===");
                foreach (var variable in body.Variables)
                {
                    sb.AppendLine($"//   [{variable.Index}] {variable.VariableType.FullName}");
                }
                sb.AppendLine();
            }

            // Build exception handler ranges for inline annotations
            var ehStarts = new Dictionary<int, string>();
            var ehEnds = new Dictionary<int, string>();
            if (body.HasExceptionHandlers)
            {
                foreach (var eh in body.ExceptionHandlers)
                {
                    var tryStart = eh.TryStart?.Offset ?? -1;
                    var handlerStart = eh.HandlerStart?.Offset ?? -1;
                    var handlerEnd = eh.HandlerEnd?.Offset ?? -1;

                    if (tryStart >= 0 && !ehStarts.ContainsKey(tryStart))
                        ehStarts[tryStart] = "// ---- try {";
                    if (handlerStart >= 0)
                    {
                        var label = eh.HandlerType == ExceptionHandlerType.Catch
                            ? $"// ---- catch ({eh.CatchType?.Name ?? "?"}) {{"
                            : $"// ---- {eh.HandlerType.ToString().ToLowerInvariant()} {{";
                        ehStarts[handlerStart] = label;
                    }
                    if (handlerEnd >= 0 && !ehEnds.ContainsKey(handlerEnd))
                        ehEnds[handlerEnd] = "// ---- }";
                }
            }

            foreach (var instruction in instructions)
            {
                if (ehEnds.TryGetValue(instruction.Offset, out var endLabel))
                    sb.AppendLine(endLabel);
                if (ehStarts.TryGetValue(instruction.Offset, out var startLabel))
                    sb.AppendLine(startLabel);
                sb.AppendLine(FormatInstruction(instruction));
            }

            return sb.ToString();
        }

        private string FormatInstruction(Instruction instruction)
        {
            var operand = instruction.Operand switch
            {
                null => string.Empty,
                Instruction target => $"IL_{target.Offset:X4}",
                Instruction[] targets => string.Join(", ", targets.Select(t => $"IL_{t.Offset:X4}")),
                MethodReference mr => mr.FullName,
                FieldReference fr => fr.FullName,
                TypeReference tr => tr.FullName,
                string s => $"\"{s}\"",
                _ => instruction.Operand.ToString() ?? string.Empty
            };

            return $"IL_{instruction.Offset:X4}: {instruction.OpCode.Name,-10} {operand}";
        }
    }
}
