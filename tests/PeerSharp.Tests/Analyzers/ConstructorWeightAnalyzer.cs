using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace PeerSharp.Tests.Analyzers;

/// <summary>
/// Analyzes constructors for potentially heavy operations using IL inspection.
/// Uses System.Reflection.Metadata for portable, dependency-free analysis.
/// </summary>
public sealed class ConstructorWeightAnalyzer : IDisposable
{
    private readonly PEReader _peReader;
    private readonly MetadataReader _metadataReader;
    private readonly Dictionary<string, HeavyOperationCategory> _heavyMethodPatterns;

    /// <summary>
    /// Categories of heavy operations that should not be in constructors.
    /// </summary>
    public enum HeavyOperationCategory
    {
        FileIO,
        NetworkIO,
        ThreadCreation,
        TaskBlocking,
        ProcessCreation,
        Cryptography,
        LargeAllocation,
        VirtualMethodCall,
        DatabaseAccess,
        Serialization
    }

    /// <summary>
    /// Represents a detected heavy operation in a constructor.
    /// </summary>
    public record HeavyOperationViolation(
        Type DeclaringType,
        ConstructorInfo Constructor,
        string MethodCalled,
        HeavyOperationCategory Category,
        string Description);

    public ConstructorWeightAnalyzer(Assembly assembly)
    {
        var assemblyPath = assembly.Location;
        if (string.IsNullOrEmpty(assemblyPath))
        {
            throw new ArgumentException("Assembly must have a file location", nameof(assembly));
        }

        var fileStream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _peReader = new PEReader(fileStream);
        _metadataReader = _peReader.GetMetadataReader();

        _heavyMethodPatterns = BuildHeavyMethodPatterns();
    }

    /// <summary>
    /// Analyzes constructors for the provided types and returns all violations.
    /// </summary>
    public List<HeavyOperationViolation> AnalyzeConstructors(IEnumerable<Type> types)
    {
        var violations = new List<HeavyOperationViolation>();

        foreach (var type in types)
        {
            var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var ctor in ctors)
            {
                violations.AddRange(AnalyzeConstructor(type, ctor));
            }
        }

        return violations;
    }

    /// <summary>
    /// Builds the dictionary of method patterns that indicate heavy operations.
    /// </summary>
    private static Dictionary<string, HeavyOperationCategory> BuildHeavyMethodPatterns()
    {
        var patterns = new Dictionary<string, HeavyOperationCategory>(StringComparer.Ordinal);

        // File I/O operations
        AddPatterns(patterns, HeavyOperationCategory.FileIO,
            "System.IO.File::Open",
            "System.IO.File::Create",
            "System.IO.File::ReadAllText",
            "System.IO.File::ReadAllBytes",
            "System.IO.File::ReadAllLines",
            "System.IO.File::WriteAllText",
            "System.IO.File::WriteAllBytes",
            "System.IO.File::WriteAllLines",
            "System.IO.File::Copy",
            "System.IO.File::Move",
            "System.IO.File::Delete",
            "System.IO.File::OpenRead",
            "System.IO.File::OpenWrite",
            "System.IO.File::OpenText",
            "System.IO.File::OpenHandle",
            "System.IO.Directory::CreateDirectory",
            "System.IO.Directory::Delete",
            "System.IO.Directory::GetFiles",
            "System.IO.Directory::GetDirectories",
            "System.IO.Directory::EnumerateFiles",
            "System.IO.Directory::EnumerateDirectories",
            "System.IO.FileStream::.ctor",
            "System.IO.StreamReader::.ctor",
            "System.IO.StreamWriter::.ctor",
            "System.IO.BinaryReader::.ctor",
            "System.IO.BinaryWriter::.ctor",
            "Microsoft.Win32.SafeHandles.SafeFileHandle::.ctor"
        );

        // Network I/O operations
        AddPatterns(patterns, HeavyOperationCategory.NetworkIO,
            "System.Net.Http.HttpClient::Send",
            "System.Net.Http.HttpClient::Get",
            "System.Net.Http.HttpClient::Post",
            "System.Net.Http.HttpClient::Put",
            "System.Net.Http.HttpClient::Delete",
            "System.Net.Sockets.Socket::Connect",
            "System.Net.Sockets.Socket::Bind",
            "System.Net.Sockets.Socket::Listen",
            "System.Net.Sockets.Socket::Accept",
            "System.Net.Sockets.Socket::Send",
            "System.Net.Sockets.Socket::Receive",
            "System.Net.Sockets.TcpClient::Connect",
            "System.Net.Sockets.TcpListener::Start",
            "System.Net.Sockets.UdpClient::Send",
            "System.Net.Sockets.UdpClient::Receive",
            "System.Net.WebClient::Download",
            "System.Net.WebClient::Upload",
            "System.Net.Dns::GetHostEntry",
            "System.Net.Dns::GetHostAddresses"
        );

        // Thread creation
        AddPatterns(patterns, HeavyOperationCategory.ThreadCreation,
            "System.Threading.Thread::Start",
            "System.Threading.Thread::.ctor",
            "System.Threading.ThreadPool::QueueUserWorkItem",
            "System.Threading.Tasks.Task::Run",
            "System.Threading.Tasks.Task::Factory",
            "System.Threading.Timer::.ctor",
            "System.Timers.Timer::Start"
        );

        // Task blocking operations (should never be in constructor)
        AddPatterns(patterns, HeavyOperationCategory.TaskBlocking,
            "System.Threading.Tasks.Task::Wait",
            "System.Threading.Tasks.Task::get_Result",
            "System.Threading.Tasks.Task::GetAwaiter",
            "System.Threading.Tasks.ValueTask::GetAwaiter",
            "System.Threading.SemaphoreSlim::Wait",
            "System.Threading.ManualResetEvent::WaitOne",
            "System.Threading.AutoResetEvent::WaitOne",
            "System.Threading.Mutex::WaitOne",
            "System.Threading.Monitor::Enter",
            "System.Threading.SpinWait::SpinOnce"
        );

        // Process creation
        AddPatterns(patterns, HeavyOperationCategory.ProcessCreation,
            "System.Diagnostics.Process::Start",
            "System.Diagnostics.Process::.ctor",
            "System.Diagnostics.ProcessStartInfo::.ctor"
        );

        // Cryptographic operations
        AddPatterns(patterns, HeavyOperationCategory.Cryptography,
            "System.Security.Cryptography.SHA1::Create",
            "System.Security.Cryptography.SHA256::Create",
            "System.Security.Cryptography.SHA384::Create",
            "System.Security.Cryptography.SHA512::Create",
            "System.Security.Cryptography.MD5::Create",
            "System.Security.Cryptography.Aes::Create",
            "System.Security.Cryptography.RSA::Create",
            "System.Security.Cryptography.RandomNumberGenerator::Create",
            "System.Security.Cryptography.SHA1::ComputeHash",
            "System.Security.Cryptography.SHA256::ComputeHash"
        );

        // Database access
        AddPatterns(patterns, HeavyOperationCategory.DatabaseAccess,
            "System.Data.SqlClient.SqlConnection::Open",
            "System.Data.SqlClient.SqlCommand::ExecuteReader",
            "System.Data.SqlClient.SqlCommand::ExecuteNonQuery",
            "Microsoft.Data.SqlClient.SqlConnection::Open",
            "Microsoft.EntityFrameworkCore.DbContext::SaveChanges"
        );

        // Serialization (can be slow for large objects)
        AddPatterns(patterns, HeavyOperationCategory.Serialization,
            "System.Text.Json.JsonSerializer::Deserialize",
            "System.Text.Json.JsonSerializer::Serialize",
            "Newtonsoft.Json.JsonConvert::DeserializeObject",
            "Newtonsoft.Json.JsonConvert::SerializeObject",
            "System.Xml.Serialization.XmlSerializer::Deserialize",
            "System.Xml.Serialization.XmlSerializer::Serialize"
        );

        return patterns;
    }

    private static void AddPatterns(Dictionary<string, HeavyOperationCategory> dict, HeavyOperationCategory category, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            dict[pattern] = category;
        }
    }

    /// <summary>
    /// Analyzes a single constructor for heavy operations.
    /// </summary>
    private IEnumerable<HeavyOperationViolation> AnalyzeConstructor(Type declaringType, ConstructorInfo ctor)
    {
        var violations = new List<HeavyOperationViolation>();

        try
        {
            // Find the method definition in metadata
            var methodCalls = GetMethodCallsFromConstructor(declaringType, ctor);

            foreach (var methodCall in methodCalls)
            {
                // Check against heavy method patterns
                foreach (var pattern in _heavyMethodPatterns)
                {
                    if (MatchesPattern(methodCall, pattern.Key))
                    {
                        violations.Add(new HeavyOperationViolation(
                            declaringType,
                            ctor,
                            methodCall,
                            pattern.Value,
                            GetCategoryDescription(pattern.Value)));
                        break; // One match per method call is enough
                    }
                }

                // Check for virtual method calls on 'this' (dangerous in constructors)
                if (IsVirtualMethodCallOnThis(declaringType, methodCall))
                {
                    violations.Add(new HeavyOperationViolation(
                        declaringType,
                        ctor,
                        methodCall,
                        HeavyOperationCategory.VirtualMethodCall,
                        "Virtual method call in constructor can cause issues with derived classes"));
                }
            }
        }
        catch (Exception)
        {
            // If we can't analyze, skip this constructor
            // This can happen for dynamic types or types from other assemblies
        }

        return violations;
    }

    /// <summary>
    /// Gets all method calls from a constructor's IL.
    /// </summary>
    private List<string> GetMethodCallsFromConstructor(Type declaringType, ConstructorInfo ctor)
    {
        var methodCalls = new List<string>();

        try
        {
            // Find the type definition
            var typeDefinitionHandle = FindTypeDefinition(declaringType);
            if (typeDefinitionHandle.IsNil)
            {
                return methodCalls;
            }

            var typeDefinition = _metadataReader.GetTypeDefinition(typeDefinitionHandle);

            // Find the constructor method definition
            foreach (var methodHandle in typeDefinition.GetMethods())
            {
                var methodDef = _metadataReader.GetMethodDefinition(methodHandle);
                var methodName = _metadataReader.GetString(methodDef.Name);

                // Match constructor by name and parameter count
                if (methodName == ".ctor" || methodName == ".cctor")
                {
                    var signature = methodDef.DecodeSignature(new SignatureTypeProvider(), null);
                    if (signature.ParameterTypes.Length == ctor.GetParameters().Length)
                    {
                        // Found matching constructor, analyze its IL
                        var methodBody = _peReader.GetMethodBody(methodDef.RelativeVirtualAddress);
                        if (methodBody != null)
                        {
                            var calls = ExtractMethodCallsFromIL(methodBody.GetILBytes());
                            methodCalls.AddRange(calls);
                        }
                        break;
                    }
                }
            }
        }
        catch
        {
            // Ignore analysis errors
        }

        return methodCalls;
    }

    /// <summary>
    /// Finds the type definition handle for a given type.
    /// </summary>
    private TypeDefinitionHandle FindTypeDefinition(Type type)
    {
        var targetNamespace = type.Namespace ?? "";
        var targetName = type.Name;

        // Handle nested types
        if (type.IsNested && type.DeclaringType != null)
        {
            // For nested types, we need to find the declaring type first
            var declaringHandle = FindTypeDefinition(type.DeclaringType);
            if (!declaringHandle.IsNil)
            {
                var declaringDef = _metadataReader.GetTypeDefinition(declaringHandle);
                foreach (var nestedHandle in declaringDef.GetNestedTypes())
                {
                    var nestedDef = _metadataReader.GetTypeDefinition(nestedHandle);
                    if (_metadataReader.GetString(nestedDef.Name) == targetName)
                    {
                        return nestedHandle;
                    }
                }
            }
            return default;
        }

        foreach (var typeHandle in _metadataReader.TypeDefinitions)
        {
            var typeDef = _metadataReader.GetTypeDefinition(typeHandle);
            var ns = _metadataReader.GetString(typeDef.Namespace);
            var name = _metadataReader.GetString(typeDef.Name);

            if (ns == targetNamespace && name == targetName)
            {
                return typeHandle;
            }
        }

        return default;
    }

    /// <summary>
    /// Extracts method call references from IL bytes.
    /// </summary>
    private List<string> ExtractMethodCallsFromIL(byte[]? ilBytes)
    {
        var calls = new List<string>();
        if (ilBytes == null || ilBytes.Length == 0)
        {
            return calls;
        }

        int pos = 0;
        while (pos < ilBytes.Length)
        {
            var opcode = ilBytes[pos];

            // Call opcodes: call (0x28), callvirt (0x6F), newobj (0x73)
            if (opcode == 0x28 || opcode == 0x6F || opcode == 0x73)
            {
                if (pos + 4 < ilBytes.Length)
                {
                    // Read the method token (4 bytes, little-endian)
                    int token = ilBytes[pos + 1] |
                               (ilBytes[pos + 2] << 8) |
                               (ilBytes[pos + 3] << 16) |
                               (ilBytes[pos + 4] << 24);

                    var methodName = ResolveMethodToken(token);
                    if (methodName != null)
                    {
                        calls.Add(methodName);
                    }
                    pos += 5;
                    continue;
                }
            }

            // Skip to next instruction
            pos += GetInstructionSize(opcode, ilBytes, pos);
        }

        return calls;
    }

    /// <summary>
    /// Resolves a metadata token to a method name string.
    /// </summary>
    private string? ResolveMethodToken(int token)
    {
        try
        {
            var handle = MetadataTokens.EntityHandle(token);

            if (handle.Kind == HandleKind.MethodDefinition)
            {
                var methodDef = _metadataReader.GetMethodDefinition((MethodDefinitionHandle)handle);
                var methodName = _metadataReader.GetString(methodDef.Name);
                var declaringType = _metadataReader.GetTypeDefinition(methodDef.GetDeclaringType());
                var typeName = _metadataReader.GetString(declaringType.Name);
                var ns = _metadataReader.GetString(declaringType.Namespace);
                return $"{ns}.{typeName}::{methodName}";
            }
            else if (handle.Kind == HandleKind.MemberReference)
            {
                var memberRef = _metadataReader.GetMemberReference((MemberReferenceHandle)handle);
                var methodName = _metadataReader.GetString(memberRef.Name);
                var parentHandle = memberRef.Parent;

                string typeName = ResolveTypeReference(parentHandle);
                return $"{typeName}::{methodName}";
            }
        }
        catch
        {
            // Token resolution failed
        }

        return null;
    }

    /// <summary>
    /// Resolves a type reference handle to a full type name.
    /// </summary>
    private string ResolveTypeReference(EntityHandle handle)
    {
        try
        {
            if (handle.Kind == HandleKind.TypeReference)
            {
                var typeRef = _metadataReader.GetTypeReference((TypeReferenceHandle)handle);
                var ns = _metadataReader.GetString(typeRef.Namespace);
                var name = _metadataReader.GetString(typeRef.Name);
                return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }
            else if (handle.Kind == HandleKind.TypeDefinition)
            {
                var typeDef = _metadataReader.GetTypeDefinition((TypeDefinitionHandle)handle);
                var ns = _metadataReader.GetString(typeDef.Namespace);
                var name = _metadataReader.GetString(typeDef.Name);
                return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }
            else if (handle.Kind == HandleKind.TypeSpecification)
            {
                // Generic type instantiation - simplified handling
                return "<generic>";
            }
        }
        catch
        {
            // Resolution failed
        }

        return "<unknown>";
    }

    /// <summary>
    /// Gets the size of an IL instruction.
    /// </summary>
    private static int GetInstructionSize(byte opcode, byte[] il, int pos)
    {
        // Two-byte opcodes (0xFE prefix)
        if (opcode == 0xFE && pos + 1 < il.Length)
        {
            return 2 + GetExtendedOpcodeOperandSize(il[pos + 1]);
        }

        // Single-byte opcodes
        return 1 + GetOpcodeOperandSize(opcode);
    }

    /// <summary>
    /// Gets the operand size for a single-byte opcode.
    /// Simplified version that handles the most common opcodes.
    /// </summary>
    private static int GetOpcodeOperandSize(byte opcode)
    {
        // Common opcodes with their operand sizes
        // Full reference: ECMA-335 Partition III
        return opcode switch
        {
            // 1-byte operand (int8, uint8)
            >= 0x0E and <= 0x13 => 1, // ldarg.s, ldarga.s, starg.s, ldloc.s, ldloca.s, stloc.s
            0x1F => 1, // ldc.i4.s
            >= 0x2B and <= 0x37 => 1, // br.s, brfalse.s, brtrue.s, beq.s, bge.s, etc.
            0xDE or 0xDF => 1, // leave.s, unaligned.

            // 4-byte operand (int32, token, etc.)
            0x20 => 4, // ldc.i4
            0x22 => 4, // ldc.r4
            >= 0x38 and <= 0x44 => 4, // br, brfalse, brtrue, beq, bge, bgt, ble, blt, bne.un, bge.un, bgt.un, ble.un, blt.un
            0x28 => 4, // call
            0x29 => 4, // calli
            0x6F => 4, // callvirt
            0x70 => 4, // cpobj
            0x71 => 4, // ldobj
            0x72 => 4, // ldstr
            0x73 => 4, // newobj
            0x74 => 4, // castclass
            0x75 => 4, // isinst
            0x79 => 4, // unbox
            0x7B => 4, // ldfld
            0x7C => 4, // ldflda
            0x7D => 4, // stfld
            0x7E => 4, // ldsfld
            0x7F => 4, // ldsflda
            0x80 => 4, // stsfld
            0x81 => 4, // stobj
            0x8C => 4, // box
            0x8D => 4, // newarr
            0x8F => 4, // ldelema
            0xA3 => 4, // ldelem
            0xA4 => 4, // stelem
            0xA5 => 4, // unbox.any
            0xC2 => 4, // refanyval
            0xC6 => 4, // mkrefany
            0xD0 => 4, // ldtoken
            0x76 => 4, // conv.ovf.i.un
            0xFE => 4, // Two byte opcodes - handled separately

            // 8-byte operand (int64, float64)
            0x21 => 8, // ldc.i8
            0x23 => 8, // ldc.r8

            // Switch instruction - variable length (4 + 4*n bytes)
            0x45 => 0, // Will be handled specially

            // Default: no operand
            _ => 0
        };
    }

    /// <summary>
    /// Gets the operand size for extended opcodes (0xFE prefix).
    /// </summary>
    private static int GetExtendedOpcodeOperandSize(byte opcode2)
    {
        return opcode2 switch
        {
            // 2-byte operand (uint16)
            0x09 => 2, // ldarg
            0x0A => 2, // ldarga
            0x0B => 2, // starg
            0x0C => 2, // ldloc
            0x0D => 2, // ldloca
            0x0E => 2, // stloc

            // 4-byte operand
            0x06 => 4, // ldftn
            0x07 => 4, // ldvirtftn
            0x15 => 4, // initobj
            0x16 => 4, // constrained.
            0x1C => 4, // sizeof

            // No operand for most extended opcodes
            _ => 0
        };
    }

    /// <summary>
    /// Checks if a method call matches a pattern.
    /// </summary>
    private static bool MatchesPattern(string methodCall, string pattern)
    {
        // Exact match
        if (methodCall.Equals(pattern, StringComparison.Ordinal))
        {
            return true;
        }

        // Pattern matching: "System.IO.File::Open" matches "System.IO.File::OpenRead", etc.
        if (pattern.Contains("::"))
        {
            var patternParts = pattern.Split("::");
            var callParts = methodCall.Split("::");

            if (callParts.Length == 2 && patternParts.Length == 2)
            {
                // Type must match exactly or be a subtype
                if (callParts[0].Equals(patternParts[0], StringComparison.Ordinal) ||
                    callParts[0].StartsWith(patternParts[0] + ".", StringComparison.Ordinal))
                {
                    // Method name can match exactly or start with pattern
                    if (callParts[1].Equals(patternParts[1], StringComparison.Ordinal) ||
                        callParts[1].StartsWith(patternParts[1], StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a method call is a virtual method call on 'this'.
    /// This is dangerous in constructors as derived class overrides aren't fully initialized.
    /// </summary>
    private static bool IsVirtualMethodCallOnThis(Type declaringType, string methodCall)
    {
        // Check if the method is defined on the same type hierarchy and is virtual
        var parts = methodCall.Split("::");
        if (parts.Length != 2)
        {
            return false;
        }

        var typeName = parts[0];
        var methodName = parts[1];

        // Skip common safe virtual methods
        if (methodName == "ToString" || methodName == "GetHashCode" || methodName == "Equals")
        {
            return false;
        }

        // Check if calling a virtual method on self
        var fullTypeName = declaringType.FullName ?? declaringType.Name;
        if (typeName == fullTypeName || typeName == declaringType.Name)
        {
            try
            {
                var method = declaringType.GetMethod(methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method?.IsVirtual == true && !method.IsFinal)
                {
                    return true;
                }
            }
            catch
            {
                // Method lookup failed
            }
        }

        return false;
    }

    /// <summary>
    /// Gets a human-readable description for a heavy operation category.
    /// </summary>
    private static string GetCategoryDescription(HeavyOperationCategory category)
    {
        return category switch
        {
            HeavyOperationCategory.FileIO => "File I/O operations should not be performed in constructors",
            HeavyOperationCategory.NetworkIO => "Network I/O operations should not be performed in constructors",
            HeavyOperationCategory.ThreadCreation => "Thread/Task creation should not be performed in constructors",
            HeavyOperationCategory.TaskBlocking => "Blocking on tasks in constructors can cause deadlocks",
            HeavyOperationCategory.ProcessCreation => "Process creation should not be performed in constructors",
            HeavyOperationCategory.Cryptography => "Cryptographic operations can be slow and should be deferred",
            HeavyOperationCategory.LargeAllocation => "Large allocations should be deferred from constructors",
            HeavyOperationCategory.VirtualMethodCall => "Virtual method calls in constructors are dangerous",
            HeavyOperationCategory.DatabaseAccess => "Database access should not be performed in constructors",
            HeavyOperationCategory.Serialization => "Serialization can be slow and should be deferred from constructors",
            _ => "Heavy operation detected"
        };
    }

    public void Dispose()
    {
        _peReader.Dispose();
    }

    /// <summary>
    /// Simple signature type provider for decoding method signatures.
    /// </summary>
    private sealed class SignatureTypeProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape)
        {
            return $"{elementType}[]";
        }

        public string GetByReferenceType(string elementType)
        {
            return $"{elementType}&";
        }

        public string GetFunctionPointerType(MethodSignature<string> signature)
        {
            return "fnptr";
        }

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        {
            return $"{genericType}<{string.Join(", ", typeArguments)}>";
        }

        public string GetGenericMethodParameter(object? genericContext, int index)
        {
            return $"!!{index}";
        }

        public string GetGenericTypeParameter(object? genericContext, int index)
        {
            return $"!{index}";
        }

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
        {
            return unmodifiedType;
        }

        public string GetPinnedType(string elementType)
        {
            return elementType;
        }

        public string GetPointerType(string elementType)
        {
            return $"{elementType}*";
        }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode)
        {
            return typeCode.ToString();
        }

        public string GetSZArrayType(string elementType)
        {
            return $"{elementType}[]";
        }

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var typeDef = reader.GetTypeDefinition(handle);
            return reader.GetString(typeDef.Name);
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var typeRef = reader.GetTypeReference(handle);
            return reader.GetString(typeRef.Name);
        }
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            return "spec";
        }
    }
}





