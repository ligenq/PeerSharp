using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace PeerSharp.Tests.ArchitectureTests;

/// <summary>
/// Which methods call which, read out of the compiled IL.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that "is this method tested?" can be answered by what the tests actually run
/// rather than by what they are called. Matching on names would mean a test has to be named after
/// the method it covers, and the tests here are named after the behaviour they pin down -
/// <c>Worker_ReplenishesCompletedSlot_WhileAnotherSourceIsBlocked</c> says far more than
/// <c>WorkerLoopAsync_Replenish</c>, and there is no reason a convention should cost that.
/// </para>
/// <para>
/// A method is identified by its declaring type's full name and its own name, with overloads
/// treated as one. That is deliberate: the question being asked is whether anything exercises this
/// piece of logic, and resolving signatures precisely across assembly boundaries would add a great
/// deal of machinery to answer a question nobody asked.
/// </para>
/// </remarks>
internal sealed class CallGraph
{
    private readonly Dictionary<string, HashSet<string>> _callsFrom = new(StringComparer.Ordinal);

    /// <summary>Methods carrying a test attribute, which is where a reachability walk starts.</summary>
    public HashSet<string> TestEntryPoints { get; } = new(StringComparer.Ordinal);

    /// <summary>The key a method is known by: <c>Namespace.Type::Method</c>.</summary>
    public static string Key(string typeFullName, string methodName) => $"{typeFullName}::{methodName}";

    /// <summary>
    /// Everything reachable from <paramref name="roots"/>, following calls as far as they go.
    /// </summary>
    /// <remarks>
    /// Transitive on purpose. A test that calls <c>AddTorrentFileAsync</c> exercises the
    /// <c>AddTorrentAsync</c> it delegates to, and a test that drives a transfer through a helper
    /// exercises what the helper drives - neither is a direct call from the test method itself.
    /// </remarks>
    public HashSet<string> Reachable(IEnumerable<string> roots)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(roots);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current) || !_callsFrom.TryGetValue(current, out var callees))
            {
                continue;
            }

            foreach (var callee in callees)
            {
                if (!seen.Contains(callee))
                {
                    queue.Enqueue(callee);
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// Reads one assembly's IL into the graph.
    /// </summary>
    public void Add(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();

        foreach (var handle in metadata.MethodDefinitions)
        {
            var definition = metadata.GetMethodDefinition(handle);
            var declaringType = TypeName(metadata, definition.GetDeclaringType());
            var caller = Key(declaringType, metadata.GetString(definition.Name));

            if (HasTestAttribute(metadata, definition))
            {
                TestEntryPoints.Add(caller);
            }

            // An async method, an iterator, a lambda and a local function all compile to members of
            // a generated type, leaving the method the author wrote as a stub that starts a state
            // machine. Without joining those back to their owner, every async test appears to call
            // nothing at all - and a great many tests here are async.
            if (TryGetGeneratedOwner(declaringType, metadata.GetString(definition.Name), out var owner))
            {
                Link(owner, caller);
            }

            // Abstract, extern and interface methods have no body to read.
            if (definition.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var body = peReader.GetMethodBody(definition.RelativeVirtualAddress);
            var il = body.GetILBytes() ?? [];
            foreach (var callee in CalledMethods(metadata, il))
            {
                Link(caller, callee);
            }
        }
    }

    private void Link(string caller, string callee)
    {
        if (!_callsFrom.TryGetValue(caller, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _callsFrom[caller] = set;
        }

        set.Add(callee);
    }

    /// <summary>
    /// The method a compiler-generated member was produced for, if it was produced for one.
    /// </summary>
    /// <remarks>
    /// The two shapes that matter both spell the original method's name inside angle brackets:
    /// a state machine is a nested type called <c>&lt;DoAsync&gt;d__4</c> whose work is in
    /// <c>MoveNext</c>, and a lambda or local function is a method called
    /// <c>&lt;DoAsync&gt;b__4_0</c> on the owning type or on a display class beside it.
    /// </remarks>
    internal static bool TryGetGeneratedOwner(string declaringType, string methodName, out string ownerKey)
    {
        ownerKey = string.Empty;

        var segments = declaringType.Split('+');
        string? owner = NameInBrackets(methodName);

        // Walk out through the generated nesting to the type the author actually wrote.
        int last = segments.Length - 1;
        while (last > 0 && segments[last].StartsWith('<'))
        {
            owner ??= NameInBrackets(segments[last]);
            last--;
        }

        if (owner is null || owner.Length == 0)
        {
            return false;
        }

        ownerKey = Key(string.Join('+', segments[..(last + 1)]), owner);
        return true;
    }

    private static string? NameInBrackets(string value)
    {
        if (!value.StartsWith('<'))
        {
            return null;
        }

        int end = value.IndexOf('>', StringComparison.Ordinal);
        return end > 1 ? value[1..end] : null;
    }

    /// <summary>
    /// The simple names of every type an assembly declares, read from its metadata.
    /// </summary>
    /// <remarks>
    /// Read from the file rather than through <see cref="Assembly.GetTypes"/> so that an assembly
    /// this project does not reference can still be asked. Loading one would mean resolving its
    /// dependencies out of a directory that does not contain them.
    /// </remarks>
    public static IEnumerable<string> TypeNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();

        foreach (var handle in metadata.TypeDefinitions)
        {
            yield return metadata.GetString(metadata.GetTypeDefinition(handle).Name);
        }
    }

    /// <summary>
    /// The methods in an assembly whose bodies cannot return - they only throw.
    /// </summary>
    /// <remarks>
    /// A member that does nothing but throw is a declaration that it is not implemented, usually to
    /// satisfy an interface the type only half wants. There is nothing in one to test but the throw,
    /// and a test asserting that would pin the gap rather than any behaviour.
    /// </remarks>
    public static HashSet<string> MethodsThatOnlyThrow(string assemblyPath)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();

        foreach (var handle in metadata.MethodDefinitions)
        {
            var definition = metadata.GetMethodDefinition(handle);
            if (definition.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var il = peReader.GetMethodBody(definition.RelativeVirtualAddress).GetILBytes();
            if (il is { Length: > 0 } && !CanReturn(il))
            {
                result.Add(Key(
                    TypeName(metadata, definition.GetDeclaringType()),
                    metadata.GetString(definition.Name)));
            }
        }

        return result;
    }

    /// <summary>Whether a method body contains any <c>ret</c> at all.</summary>
    private static bool CanReturn(byte[] il)
    {
        int offset = 0;

        while (offset < il.Length)
        {
            short opCodeValue = il[offset];
            offset++;

            if (opCodeValue == 0xFE && offset < il.Length)
            {
                opCodeValue = (short)(0xFE00 | il[offset]);
                offset++;
            }

            if (!OperandSizes.TryGetValue(opCodeValue, out var operandType))
            {
                // Lost the thread of the instruction stream, so nothing can be concluded. Saying it
                // can return is the answer that keeps the member in scope rather than excusing it.
                return true;
            }

            if (opCodeValue == OpCodes.Ret.Value)
            {
                return true;
            }

            offset += OperandLength(operandType, il, offset);
        }

        return false;
    }

    private static bool HasTestAttribute(MetadataReader metadata, MethodDefinition definition)
    {
        foreach (var attributeHandle in definition.GetCustomAttributes())
        {
            var name = AttributeTypeName(metadata, metadata.GetCustomAttribute(attributeHandle));
            if (name is "FactAttribute" or "TheoryAttribute")
            {
                return true;
            }
        }

        return false;
    }

    private static string? AttributeTypeName(MetadataReader metadata, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var reference = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                return reference.Parent.Kind == HandleKind.TypeReference
                    ? metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)reference.Parent).Name)
                    : null;

            case HandleKind.MethodDefinition:
                var definition = metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                return metadata.GetString(metadata.GetTypeDefinition(definition.GetDeclaringType()).Name);

            default:
                return null;
        }
    }

    /// <summary>
    /// Walks a method body and returns what it calls.
    /// </summary>
    /// <remarks>
    /// A real instruction walk rather than a scan for call opcodes, because operand bytes are
    /// indistinguishable from opcodes when read out of step - a branch offset can contain 0x28, and
    /// a token read from the wrong place resolves to something unrelated. Getting that wrong would
    /// silently credit a method nobody calls, which is the one failure mode this whole test exists
    /// to avoid.
    /// </remarks>
    private static IEnumerable<string> CalledMethods(MetadataReader metadata, byte[] il)
    {
        var results = new List<string>();
        int offset = 0;

        while (offset < il.Length)
        {
            short opCodeValue = il[offset];
            offset++;

            if (opCodeValue == 0xFE && offset < il.Length)
            {
                opCodeValue = (short)(0xFE00 | il[offset]);
                offset++;
            }

            if (!OperandSizes.TryGetValue(opCodeValue, out var operandType))
            {
                // An opcode this runtime does not know: the walk has lost its place and anything
                // read after it would be guesswork.
                break;
            }

            if (operandType is OperandType.InlineMethod or OperandType.InlineTok && offset + 4 <= il.Length)
            {
                int token = BitConverter.ToInt32([il[offset], il[offset + 1], il[offset + 2], il[offset + 3]], 0);
                var name = ResolveMethod(metadata, token);
                if (name != null)
                {
                    results.Add(name);
                }
            }

            offset += OperandLength(operandType, il, offset);
        }

        return results;
    }

    private static int OperandLength(OperandType operandType, byte[] il, int offset)
    {
        switch (operandType)
        {
            case OperandType.InlineNone:
                return 0;

            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                return 1;

            case OperandType.InlineVar:
                return 2;

            case OperandType.InlineI8:
            case OperandType.InlineR:
                return 8;

            case OperandType.InlineSwitch:
                if (offset + 4 > il.Length)
                {
                    return il.Length - offset;
                }

                int count = BitConverter.ToInt32([il[offset], il[offset + 1], il[offset + 2], il[offset + 3]], 0);
                return 4 + (count * 4);

            default:
                return 4;
        }
    }

    private static string? ResolveMethod(MetadataReader metadata, int token)
    {
        EntityHandle handle;
        try
        {
            handle = MetadataTokens.EntityHandle(token);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (handle.IsNil)
        {
            return null;
        }

        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                var definition = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
                return Key(TypeName(metadata, definition.GetDeclaringType()), metadata.GetString(definition.Name));

            case HandleKind.MemberReference:
                var reference = metadata.GetMemberReference((MemberReferenceHandle)handle);
                var parent = ParentTypeName(metadata, reference.Parent);
                return parent is null ? null : Key(parent, metadata.GetString(reference.Name));

            case HandleKind.MethodSpecification:
                // A generic method call. The interesting part is the method it instantiates.
                var specification = metadata.GetMethodSpecification((MethodSpecificationHandle)handle);
                return ResolveMethod(metadata, MetadataTokens.GetToken(specification.Method));

            default:
                return null;
        }
    }

    private static string? ParentTypeName(MetadataReader metadata, EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                var reference = metadata.GetTypeReference((TypeReferenceHandle)parent);
                var space = metadata.GetString(reference.Namespace);
                var name = metadata.GetString(reference.Name);
                return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";

            case HandleKind.TypeDefinition:
                return TypeName(metadata, (TypeDefinitionHandle)parent);

            case HandleKind.TypeSpecification:
                // A constructed generic, e.g. Selection<Peer>. Static members of one are invisible
                // without decoding the signature, and a type this repository declares as generic
                // would then look untested however many tests called it.
                return DecodeTypeSpecification(metadata, (TypeSpecificationHandle)parent);

            default:
                return null;
        }
    }

    /// <summary>
    /// The generic type definition behind a constructed generic - <c>Selection`1</c> for
    /// <c>Selection&lt;Peer&gt;</c> - which is the name every other part of this graph uses.
    /// </summary>
    private static string? DecodeTypeSpecification(MetadataReader metadata, TypeSpecificationHandle handle)
    {
        try
        {
            return metadata.GetTypeSpecification(handle).DecodeSignature(new TypeNameProvider(metadata), null!);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes a type signature to the name this graph keys on, discarding the parts it does not
    /// need: type arguments, array and pointer shapes, and modifiers.
    /// </summary>
    private sealed class TypeNameProvider(MetadataReader metadata) : ISignatureTypeProvider<string, object?>
    {
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            TypeName(reader, handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var reference = reader.GetTypeReference(handle);
            var space = reader.GetString(reference.Namespace);
            var name = reader.GetString(reference.Name);
            return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            DecodeTypeSpecification(reader, handle) ?? string.Empty;

        public string GetArrayType(string elementType, ArrayShape shape) => elementType;
        public string GetByReferenceType(string elementType) => elementType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => string.Empty;
        public string GetGenericMethodParameter(object? genericContext, int index) => string.Empty;
        public string GetGenericTypeParameter(object? genericContext, int index) => string.Empty;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType;
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType;

        private string TypeName(MetadataReader reader, TypeDefinitionHandle handle)
        {
            _ = metadata;
            var definition = reader.GetTypeDefinition(handle);
            var name = reader.GetString(definition.Name);

            if (definition.IsNested)
            {
                return $"{TypeName(reader, definition.GetDeclaringType())}+{name}";
            }

            var space = reader.GetString(definition.Namespace);
            return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
        }
    }

    private static string TypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var definition = metadata.GetTypeDefinition(handle);
        var name = metadata.GetString(definition.Name);

        if (definition.IsNested)
        {
            return $"{TypeName(metadata, definition.GetDeclaringType())}+{name}";
        }

        var space = metadata.GetString(definition.Namespace);
        return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
    }

    /// <summary>
    /// Operand shape per opcode, taken from the runtime's own table rather than written out here.
    /// </summary>
    private static readonly Dictionary<short, OperandType> OperandSizes = BuildOperandTable();

    private static Dictionary<short, OperandType> BuildOperandTable()
    {
        var table = new Dictionary<short, OperandType>();

        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opCode)
            {
                table[opCode.Value] = opCode.OperandType;
            }
        }

        return table;
    }
}
