using System.Reflection;
using PeerSharp.Tests.Analyzers;

namespace PeerSharp.Tests.ArchitectureTests;

/// <summary>
/// Architecture tests to enforce coding conventions and design patterns
/// across the PeerSharp codebase.
/// </summary>
public class ArchitectureTests
{
    // --- PORTABILITY CONFIGURATION ---
    private static readonly Assembly CoreAssembly = ArchitectureHelper.CoreAssembly;

    private static readonly string RootNamespace = ArchitectureHelper.RootNamespace;

    private static readonly string[] ForbiddenNamespacesInCore =
    {
        "System.Windows",
        "Microsoft.Win32",
        "System.Drawing",
        "Avalonia",
        "WinForms"
    };
    // --------------------------------

    private static readonly IEnumerable<Type> AllTypes = ArchitectureHelper.AllTypes;

    private static readonly string[] PiecePickingForbiddenNamespaces =
    {
        ".Internals.Trackers",
        ".Internals.Dht",
        ".Internals.Network",
        ".Internals.Utp",
        ".Internals.Seeding",
        ".Internals.Streaming"
    };

    private static readonly string[] PieceWriterForbiddenNamespaces =
    {
        ".Internals.Trackers",
        ".Internals.Dht",
        ".Internals.Network",
        ".Internals.Utp",
        ".Internals.Seeding",
        ".Internals.Streaming",
        ".Internals.Peers"
    };

    /// <summary>
    /// Enforces the use of AtomicDisposal for thread-safe disposal
    /// when a type owns disposable resources.
    /// Motivation: Ensures thread-safe disposal only where it is actually required.
    /// </summary>
    [Fact]
    public void Disposables_With_Owned_Resources_Must_Use_ThreadSafe_Disposal_Pattern()
    {
        var errors = new List<string>();
        var atomicDisposalType = typeof(AtomicDisposal);

        var disposableTypes = AllTypes
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => typeof(IDisposable).IsAssignableFrom(t))
            .Where(t => t.Namespace?.StartsWith(RootNamespace) ?? false);

        foreach (var type in disposableTypes)
        {
            var ownsDisposableFields = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(f => typeof(IDisposable).IsAssignableFrom(f.FieldType));

            if (!ownsDisposableFields)
            {
                continue;
            }

            var boolDisposedFields = type
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.FieldType == typeof(bool) &&
                            f.Name.Contains("disposed", StringComparison.OrdinalIgnoreCase));

            if (boolDisposedFields.Any())
            {
                errors.Add($"Type {type.FullName} uses a 'bool' for disposal tracking. Use AtomicDisposal instead.");
            }

            var intDisposedFields = type
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.FieldType == typeof(int) &&
                            f.Name.Contains("disposed", StringComparison.OrdinalIgnoreCase));

            if (intDisposedFields.Any())
            {
                errors.Add($"Type {type.FullName} uses a raw 'int' for disposal tracking. Use AtomicDisposal instead.");
            }

            var hasAtomicDisposal = type
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Any(f => f.FieldType == atomicDisposalType);

            if (!hasAtomicDisposal)
            {
                errors.Add($"Type {type.FullName} owns disposable resources but is missing an AtomicDisposal field.");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail("Thread-safe disposal violations found:" +
                        Environment.NewLine +
                        string.Join(Environment.NewLine, errors));
        }
    }

    /// <summary>
    /// Enforces strict layering: Core must not depend on UI libraries.
    /// Motivation: Ensures the domain logic remains platform-independent.
    /// </summary>
    [Fact]
    public void Core_Project_Must_Not_Reference_UI_Libraries()
    {
        var referencedAssemblies = CoreAssembly.GetReferencedAssemblies();
        var errors = new List<string>();

        foreach (var forbidden in ForbiddenNamespacesInCore)
        {
            if (referencedAssemblies.Any(a =>
                    a.Name!.Contains(forbidden, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Core project must not reference UI/Platform library: {forbidden}");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail("Layering violations found:" +
                        Environment.NewLine +
                        string.Join(Environment.NewLine, errors));
        }
    }

    /// <summary>
    /// Ensures async methods return Task or Task&lt;T&gt;, not void.
    /// Motivation: Async void methods cannot be awaited, their exceptions cannot be caught,
    /// and they cause issues with error handling and testing. Only event handlers may use async void.
    /// </summary>
    [Fact]
    public void Async_Methods_Must_Not_Return_Void()
    {
        var errors = new List<string>();

        foreach (var type in AllTypes.Where(t => t.Namespace?.StartsWith(RootNamespace) ?? false))
        {
            var asyncVoidMethods = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == typeof(void))
                .Where(m => m.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>() != null)
                .Where(m => !m.Name.StartsWith("On") && !m.Name.Contains("Handler") && !m.Name.Contains("_")); // Allow event handlers and compiler-generated

            foreach (var method in asyncVoidMethods)
            {
                errors.Add($"{type.FullName}.{method.Name}");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Async void methods found (use Task instead):{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures CancellationToken is always the last parameter in method signatures.
    /// Motivation: This is a .NET convention that improves API consistency and allows
    /// optional CancellationToken parameters with default values.
    /// </summary>
    [Fact]
    public void CancellationToken_Must_Be_Last_Parameter()
    {
        var errors = new List<string>();

        foreach (var type in AllTypes.Where(t => t.Namespace?.StartsWith(RootNamespace) ?? false))
        {
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var parameters = method.GetParameters();
                for (int i = 0; i < parameters.Length - 1; i++)
                {
                    if (parameters[i].ParameterType == typeof(CancellationToken))
                    {
                        errors.Add($"{type.FullName}.{method.Name} - CancellationToken is not last parameter");
                        break;
                    }
                }
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"CancellationToken position violations:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures classes use properties instead of public mutable fields.
    /// Motivation: Properties provide encapsulation, allow validation logic,
    /// enable data binding, and support interface contracts.
    /// Compiler-generated types (async state machines, closures) are excluded.
    /// </summary>
    [Fact]
    public void Classes_Must_Not_Have_Public_Mutable_Fields()
    {
        bool IsCompilerGenerated(Type t) =>
            t.Name.Contains('<') || t.Name.Contains('>') ||
            t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false);

        var errors = AllTypes
            .Where(t => t.IsClass && (t.Namespace?.StartsWith(RootNamespace) ?? false))
            .Where(t => !IsCompilerGenerated(t))
            .SelectMany(t => t.GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Where(f => !f.IsInitOnly && !f.IsLiteral) // Allow readonly and const
                .Select(f => $"{t.FullName}.{f.Name}"))
            .ToList();

        if (errors.Count != 0)
        {
            Assert.Fail($"Public mutable instance fields found (use properties instead):{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures thread synchronization primitives are readonly.
    /// Motivation: Replacing a lock object during runtime causes race conditions
    /// and defeats the purpose of synchronization.
    /// </summary>
    [Fact]
    public void Synchronization_Primitives_Must_Be_Readonly()
    {
        var syncTypes = new[] { typeof(ReaderWriterLockSlim), typeof(SemaphoreSlim), typeof(Mutex), typeof(Lock) };
        var errors = new List<string>();

        foreach (var type in AllTypes.Where(t => t.Namespace?.StartsWith(RootNamespace) ?? false))
        {
            // Ignore compiler-generated types (async state machines, lambdas)
            if (type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            {
                continue;
            }

            // Only check fields declared in this type, not inherited from base classes
            var mutableSyncFields = type
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(f => syncTypes.Any(s => s.IsAssignableFrom(f.FieldType)))
                .Where(f => !f.IsInitOnly);

            foreach (var field in mutableSyncFields)
            {
                errors.Add($"{type.FullName}.{field.Name} should be readonly");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Mutable synchronization primitives found:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures interfaces follow the I-prefix naming convention.
    /// Motivation: Consistent naming makes interfaces immediately recognizable
    /// and is a universal .NET convention.
    /// </summary>
    [Fact]
    public void Interfaces_Must_Start_With_I_Prefix()
    {
        var interfaces = AllTypes
            .Where(t => t.IsInterface)
            .Where(t => t.Namespace?.StartsWith(RootNamespace) ?? false);

        var errors = interfaces
            .Where(iface => !iface.Name.StartsWith("I"))
            .Select(iface => iface.FullName)
            .ToList();

        if (errors.Count != 0)
        {
            Assert.Fail($"Interfaces must start with 'I':{Environment.NewLine}{string.Join(Environment.NewLine, errors!)}");
        }
    }

    /// <summary>
    /// Ensures public async methods follow the "Async" suffix naming convention.
    /// Motivation: Makes it immediately clear that a method is asynchronous
    /// and should be awaited. This is a widely adopted .NET convention.
    /// </summary>
    [Fact]
    public void Public_Async_Methods_Should_Have_Async_Suffix()
    {
        var errors = new List<string>();

        // Exempt common overrides from Stream, etc.
        var exemptNames = new HashSet<string>
        {
            "ReadAsync", "WriteAsync", "FlushAsync", "DisposeAsync",
            "CopyToAsync", "Main"
        };

        foreach (var type in AllTypes.Where(t => t.Namespace?.StartsWith(RootNamespace) ?? false))
        {
            if (typeof(MulticastDelegate).IsAssignableFrom(type))
            {
                continue;
            }

            var asyncMethods = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => typeof(Task).IsAssignableFrom(m.ReturnType) ||
                           (m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>)) ||
                           m.ReturnType == typeof(ValueTask))
                .Where(m => !m.Name.EndsWith("Async"))
                .Where(m => !exemptNames.Contains(m.Name))
                .Where(m => !m.IsSpecialName); // Exclude property getters/setters

            foreach (var method in asyncMethods)
            {
                errors.Add($"{type.FullName}.{method.Name} returns {method.ReturnType.Name} but doesn't end with 'Async'");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Async naming convention violations:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures types owning disposable fields implement IDisposable.
    /// Motivation: Prevents resource leaks by ensuring types that own
    /// disposable resources provide a way to clean them up.
    ///
    /// Ownership heuristic: A disposable field is considered "owned" if its type
    /// is NOT present as a constructor parameter (i.e., not injected via DI).
    /// Fields whose types appear in constructor parameters are assumed to be
    /// injected and thus not owned by the type.
    /// </summary>
    [Fact]
    public void Types_With_Disposable_Fields_Must_Implement_IDisposable()
    {
        bool IsCompilerGenerated(Type t) =>
            t.Name.Contains('<') || t.Name.Contains('>') ||
            t.Name.Contains("+<") || // Nested async state machines
            t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false);

        var errors = new List<string>();

        foreach (var type in AllTypes.Where(t => t.IsClass && !t.IsAbstract && (t.Namespace?.StartsWith(RootNamespace) ?? false)))
        {
            // Skip compiler-generated types (async state machines, closures, etc.)
            if (IsCompilerGenerated(type))
            {
                continue;
            }

            // Get all disposable fields (excluding value types and backing fields)
            var disposableFields = type
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => typeof(IDisposable).IsAssignableFrom(f.FieldType) &&
                           !f.FieldType.IsValueType &&
                           !f.Name.Contains("k__BackingField")) // Exclude auto-property backing fields
                .ToList();

            if (disposableFields.Count == 0)
            {
                continue;
            }

            // Get all constructor parameter types across all constructors
            var ctorParamTypes = type
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(c => c.GetParameters())
                .Select(p => p.ParameterType)
                .ToHashSet();

            // Also collect property types from constructor parameter types (options pattern)
            var ctorParamPropertyTypes = ctorParamTypes
                .SelectMany(t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                .Select(p => p.PropertyType)
                .ToHashSet();

            // A field is "owned" if its type is NOT in any constructor parameter
            // and NOT reachable via properties on constructor parameter types (options pattern)
            var ownedDisposableFields = disposableFields
                .Where(f => !ctorParamTypes.Contains(f.FieldType) &&
                            !ctorParamPropertyTypes.Contains(f.FieldType))
                .ToList();

            // If the type owns any disposable field but doesn't implement IDisposable, flag it
            if (ownedDisposableFields.Count > 0 && !typeof(IDisposable).IsAssignableFrom(type) && !typeof(IAsyncDisposable).IsAssignableFrom(type))
            {
                var fieldNames = string.Join(", ", ownedDisposableFields.Select(f => f.Name));
                errors.Add($"{type.FullName} owns disposable fields ({fieldNames}) but doesn't implement IDisposable");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Missing IDisposable implementations:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures exception types follow the "Exception" suffix naming convention.
    /// Motivation: Makes exception types immediately recognizable in code.
    /// </summary>
    [Fact]
    public void Exception_Types_Must_End_With_Exception()
    {
        var errors = AllTypes
            .Where(t => t.IsClass && typeof(Exception).IsAssignableFrom(t))
            .Where(t => t.Namespace?.StartsWith(RootNamespace) ?? false)
            .Where(t => !t.Name.EndsWith("Exception"))
            .Select(t => t.FullName)
            .ToList();

        if (errors.Count != 0)
        {
            Assert.Fail($"Exception types must end with 'Exception':{Environment.NewLine}{string.Join(Environment.NewLine, errors!)}");
        }
    }

    /// <summary>
    /// Ensures static fields are readonly to prevent thread-safety issues.
    /// Motivation: Mutable static fields are a common source of threading bugs
    /// and make code harder to test. Configuration should use proper patterns.
    /// Note: Allows common patterns like singletons, configuration, and lazy-init flags.
    /// </summary>
    [Fact]
    public void Static_Fields_Should_Be_Readonly()
    {
        // Allow certain known patterns (case-insensitive, supports _prefix)
        var allowedPatterns = new[]
        {
            "instance", "singleton", "default", "empty", "shared", // Singleton patterns
            "external", "internal", "config", "settings",           // Configuration
            "loaded", "initialized", "enabled", "buffer",           // Lazy-init flags and caches
            "shutdown", "disposed"                                  // Interlocked state flags
        };

        bool IsAllowed(string fieldName)
        {
            var name = fieldName.TrimStart('_').ToLowerInvariant();
            return allowedPatterns.Any(name.Contains);
        }

        var errors = new List<string>();

        foreach (var type in AllTypes.Where(t => t.Namespace?.StartsWith(RootNamespace) ?? false))
        {
            var mutableStaticFields = type
                .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(f => !f.IsInitOnly && !f.IsLiteral)
                .Where(f => !IsAllowed(f.Name))
                .Where(f => !f.Name.StartsWith("<")); // Exclude compiler-generated

            foreach (var field in mutableStaticFields)
            {
                errors.Add($"{type.FullName}.{field.Name}");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Mutable static fields found (consider making readonly):{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures Timer fields are readonly to prevent accidental replacement.
    /// Motivation: Replacing a running timer without disposing the old one
    /// causes resource leaks. Timers should be created once and controlled
    /// via Start/Stop methods.
    /// </summary>
    [Fact]
    public void Timer_Fields_Must_Be_Readonly()
    {
        var timerTypes = new[]
        {
            typeof(System.Timers.Timer),
            typeof(Timer),
            typeof(PeriodicTimer)
        };

        var errors = new List<string>();

        foreach (var type in AllTypes.Where(t => t.Namespace?.StartsWith(RootNamespace) ?? false))
        {
            var mutableTimerFields = type
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(f => timerTypes.Any(t => t.IsAssignableFrom(f.FieldType)))
                .Where(f => !f.IsInitOnly)
                .Where(f => !f.Name.Contains("k__BackingField")); // Exclude auto-property backing fields

            foreach (var field in mutableTimerFields)
            {
                errors.Add($"{type.FullName}.{field.Name} should be readonly");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Mutable Timer fields found:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures CancellationTokenSource fields are disposed properly.
    /// Motivation: CTS instances hold unmanaged resources and should be disposed.
    /// Types owning CTS fields should implement IDisposable.
    /// </summary>
    [Fact]
    public void Types_With_CancellationTokenSource_Should_Implement_IDisposable()
    {
        bool IsCompilerGenerated(Type t) =>
            t.Name.Contains('<') || t.Name.Contains('>') ||
            t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false);

        var errors = new List<string>();

        foreach (var type in AllTypes.Where(t => t.IsClass && !t.IsAbstract && (t.Namespace?.StartsWith(RootNamespace) ?? false)))
        {
            // Skip compiler-generated types (async state machines use CTS internally)
            if (IsCompilerGenerated(type))
            {
                continue;
            }

            var hasCtsField = type
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Any(f => f.FieldType == typeof(CancellationTokenSource));

            if (hasCtsField && !typeof(IDisposable).IsAssignableFrom(type) && !typeof(IAsyncDisposable).IsAssignableFrom(type))
            {
                errors.Add($"{type.FullName} owns CancellationTokenSource but doesn't implement IDisposable or IAsyncDisposable");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"CancellationTokenSource ownership violations:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures collection properties are not publicly settable.
    /// Motivation: Allowing collection replacement can break references held by
    /// other code. Collections should be get-only with Add/Remove methods.
    /// Note: DTOs, state classes, and data transfer objects are excluded as they
    /// often need settable properties for serialization/deserialization.
    /// </summary>
    [Fact]
    public void Collection_Properties_Should_Be_Get_Only()
    {
        var collectionTypes = new[]
        {
            typeof(IList<>),
            typeof(ICollection<>),
            typeof(IDictionary<,>),
            typeof(List<>),
            typeof(Dictionary<,>),
            typeof(HashSet<>)
        };

        // Types that are DTOs or data classes where settable collections are expected
        var exemptTypePatterns = new[]
        {
            "Data", "Info", "State", "Response", "Settings", "Handshake", "Metadata", "List"
        };

        bool IsCollectionType(Type t)
        {
            // Exclude byte[] - commonly used for protocol data buffers
            if (t == typeof(byte[]))
            {
                return false;
            }

            if (t.IsGenericType)
            {
                var genericDef = t.GetGenericTypeDefinition();
                return collectionTypes.Any(ct => ct == genericDef);
            }
            return false; // Don't flag other arrays
        }

        bool IsCompilerGenerated(Type t) =>
            t.Name.Contains('<') || t.Name.Contains('>') ||
            t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false);

        bool IsExemptType(Type t) =>
            exemptTypePatterns.Any(t.Name.Contains);

        var errors = new List<string>();

        foreach (var type in AllTypes.Where(t => t.IsClass && (t.Namespace?.StartsWith(RootNamespace) ?? false)))
        {
            // Skip compiler-generated types
            if (IsCompilerGenerated(type))
            {
                continue;
            }

            // Skip DTO/data classes
            if (IsExemptType(type))
            {
                continue;
            }

            var settableCollectionProps = type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(p => IsCollectionType(p.PropertyType))
                .Where(p => p.CanWrite && p.GetSetMethod()?.IsPublic == true);

            foreach (var prop in settableCollectionProps)
            {
                errors.Add($"{type.FullName}.{prop.Name}");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Publicly settable collection properties found:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures GetHashCode is overridden when Equals is overridden.
    /// Motivation: Objects that are equal must return the same hash code.
    /// Failing to override GetHashCode when Equals is overridden causes
    /// hash-based collections (Dictionary, HashSet) to malfunction.
    /// </summary>
    [Fact]
    public void Types_Overriding_Equals_Must_Override_GetHashCode()
    {
        var errors = new List<string>();

        foreach (var type in AllTypes.Where(t => (t.IsClass || t.IsValueType) && !t.IsEnum && (t.Namespace?.StartsWith(RootNamespace) ?? false)))
        {
            var equalsMethod = type.GetMethod("Equals", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, new[] { typeof(object) }, null);
            var getHashCodeMethod = type.GetMethod("GetHashCode", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);

            if (equalsMethod != null && getHashCodeMethod == null)
            {
                errors.Add($"{type.FullName} overrides Equals but not GetHashCode");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"GetHashCode/Equals violations:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures types with finalizers implement IDisposable.
    /// Motivation: Finalizers indicate unmanaged resource cleanup. The proper
    /// dispose pattern requires IDisposable to allow deterministic cleanup
    /// and suppress finalization when Dispose is called.
    /// </summary>
    [Fact]
    public void Types_With_Finalizers_Must_Implement_IDisposable()
    {
        var errors = new List<string>();

        foreach (var type in AllTypes.Where(t => t.IsClass && (t.Namespace?.StartsWith(RootNamespace) ?? false)))
        {
            var finalizer = type.GetMethod("Finalize", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            if (finalizer != null && !typeof(IDisposable).IsAssignableFrom(type))
            {
                errors.Add($"{type.FullName} has a finalizer but doesn't implement IDisposable");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Finalizer without IDisposable:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures sealed classes don't have protected members.
    /// Motivation: Protected members in sealed classes are pointless since
    /// no derived class can access them. This indicates design confusion.
    /// </summary>
    [Fact]
    public void Sealed_Classes_Should_Not_Have_Protected_Members()
    {
        var errors = new List<string>();

        foreach (var type in AllTypes.Where(t => t.IsClass && t.IsSealed && (t.Namespace?.StartsWith(RootNamespace) ?? false)))
        {
            // Skip compiler-generated types
            if (type.Name.Contains('<') || type.Name.Contains('>'))
            {
                continue;
            }

            var protectedMembers = type
                .GetMembers(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => (m is MethodInfo mi && mi.IsFamily) || // protected method
                           (m is FieldInfo fi && fi.IsFamily) ||   // protected field
                           (m is PropertyInfo pi && (pi.GetMethod?.IsFamily == true || pi.SetMethod?.IsFamily == true)))
                .Where(m =>
                {
                    if (m is MethodInfo mi)
                    {
                        return mi.GetBaseDefinition().DeclaringType == mi.DeclaringType;
                    }

                    if (m is PropertyInfo pi)
                    {
                        var getter = pi.GetMethod;
                        var setter = pi.SetMethod;
                        bool getterOverride = getter?.IsFamily == true && getter.GetBaseDefinition().DeclaringType != getter.DeclaringType;
                        bool setterOverride = setter?.IsFamily == true && setter.GetBaseDefinition().DeclaringType != setter.DeclaringType;
                        return !getterOverride && !setterOverride;
                    }

                    return true;
                })
                .Where(m => !m.Name.StartsWith("<") &&
                           !m.Name.Contains("EqualityContract") &&
                           !m.Name.Contains("PrintMembers")) // Exclude compiler-generated and record-specific members
                .ToList();

            foreach (var member in protectedMembers)
            {
                errors.Add($"{type.FullName}.{member.Name}");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Protected members in sealed classes:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures structs are immutable (fields are readonly).
    /// Motivation: Mutable structs cause subtle bugs due to value-type copy
    /// semantics. Modifying a copy doesn't affect the original, leading to
    /// confusing behavior especially with properties and collections.
    /// Note: Excludes ref structs (stack-only, special semantics) and
    /// StructLayout structs (interop/protocol data structures).
    /// </summary>
    [Fact]
    public void Structs_Should_Be_Immutable()
    {
        var errors = new List<string>();

        foreach (var type in AllTypes.Where(t => t.IsValueType && !t.IsEnum && !t.IsPrimitive && (t.Namespace?.StartsWith(RootNamespace) ?? false)))
        {
            // Skip compiler-generated types (async state machines, etc.)
            if (type.Name.Contains('<') || type.Name.Contains('>'))
            {
                continue;
            }

            // Skip AtomicDisposal - mutability is intentional (Interlocked.Exchange)
            if (type == typeof(AtomicDisposal))
            {
                continue;
            }

            // Skip ref structs - they have special stack-only semantics and are often
            // designed for high-performance scenarios where mutability is intentional
            if (type.IsByRefLike)
            {
                continue;
            }

            // Skip structs with explicit StructLayout - these are typically interop or protocol
            // data structures where field layout matters and mutability is required
            // Check both explicit attribute and if Pack is specified (indicates interop usage)
            var structLayout = type.StructLayoutAttribute;
            if (structLayout != null && (structLayout.Pack != 0 || structLayout.Size != 0))
            {
                continue;
            }

            // Skip types marked as readonly struct (they're already enforced)
            if (type.GetCustomAttribute<System.Runtime.CompilerServices.IsReadOnlyAttribute>() != null)
            {
                continue;
            }

            var mutableFields = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => !f.IsInitOnly && !f.IsLiteral)
                .Where(f => !f.Name.Contains("k__BackingField")) // Exclude auto-property backing fields for now
                .ToList();

            if (mutableFields.Count > 0)
            {
                var fieldNames = string.Join(", ", mutableFields.Select(f => f.Name));
                errors.Add($"{type.FullName} has mutable fields: {fieldNames}");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Mutable structs found (consider making readonly struct):{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures methods with "Async" suffix return Task or ValueTask.
    /// Motivation: The Async suffix indicates an asynchronous method. Methods
    /// named *Async that don't return Task/ValueTask are misleading and violate
    /// .NET naming conventions.
    /// </summary>
    [Fact]
    public void Methods_Named_Async_Must_Return_Task_Or_ValueTask()
    {
        var errors = new List<string>();

        foreach (var type in AllTypes.Where(t => t.Namespace?.StartsWith(RootNamespace) ?? false))
        {
            var misleadingMethods = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.EndsWith("Async"))
                .Where(m => !typeof(Task).IsAssignableFrom(m.ReturnType) &&
                           m.ReturnType != typeof(ValueTask) &&
                           !(m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>)) &&
                           !(m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>)))
                .Where(m => !m.Name.StartsWith("<")); // Exclude compiler-generated

            foreach (var method in misleadingMethods)
            {
                errors.Add($"{type.FullName}.{method.Name} returns {method.ReturnType.Name}, not Task/ValueTask/IAsyncEnumerable");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Methods named *Async must return Task, ValueTask, or IAsyncEnumerable:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures that public and internal constructors have a reasonable number of parameters.
    ///
    /// Motivation:
    /// A high number of constructor parameters is a strong heuristic indicating that a class
    /// may have too many responsibilities, excessive coupling, or unclear boundaries.
    /// Such classes are often harder to understand, test, and evolve.
    ///
    /// This test acts as an architectural guardrail rather than a strict correctness rule.
    /// Failing the test should trigger a design discussion, not an automatic refactor.
    ///
    /// Scope:
    /// - Public constructors: Checked.
    ///   These form part of the public API and require external consumers to supply all
    ///   dependencies and configuration explicitly.
    /// - Internal constructors: Checked.
    ///   These are typically used for dependency injection within the assembly and directly
    ///   affect internal composition, testability, and maintainability.
    /// - Private constructors: Excluded.
    ///   Private constructors are commonly used together with static factory methods or
    ///   builders where object creation and wiring complexity is intentionally encapsulated
    ///   as an implementation detail.
    ///
    /// Exceptions:
    /// Some class roles are expected to coordinate many collaborators by design.
    /// Classes following these naming conventions are excluded from this rule:
    /// - *Orchestrator*, *Coordinator*, *Facade*, *Controller*, *Handler*, *ViewModel*
    ///
    /// In such cases, constructor complexity is considered an explicit architectural choice
    /// rather than an indicator of poor design.
    ///
    /// Guidance:
    /// When this test fails, consider:
    /// - Splitting the class into smaller, more focused components
    /// - Introducing a parameter object (only when it represents a cohesive and meaningful concept, not merely to bundle unrelated dependencies)
    /// - Using a factory or builder to encapsulate construction logic
    /// - Re-evaluating whether the class has a clear and cohesive responsibility
    /// </summary>
    [Fact]
    public void Constructors_Should_Not_Have_Too_Many_Parameters()
    {
        const int MaxParameters = 7;
        var errors = new List<string>();

        foreach (var type in AllTypes.Where(t => t.IsClass && !t.IsAbstract && (t.Namespace?.StartsWith(RootNamespace) ?? false)))
        {
            // Skip compiler-generated types
            if (type.Name.Contains('<') || type.Name.Contains('>'))
            {
                continue;
            }

            // Skip records and DTOs as they are primarily for data transfer and often have many properties/parameters
            if (IsDtoOrRecord(type))
            {
                continue;
            }

            // Check public and internal constructors, but exclude private ones.
            // Private constructors are used with factory patterns where DI concerns don't apply.
            var constructorsWithTooManyParams = type
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(c => !c.IsPrivate && c.GetParameters().Length > MaxParameters);

            foreach (var ctor in constructorsWithTooManyParams)
            {
                errors.Add($"{type.FullName} has constructor with {ctor.GetParameters().Length} parameters (max: {MaxParameters})");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Constructors with too many parameters (SRP violation?):{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures required constructor parameters are not nullable.
    ///
    /// Motivation: Constructor parameters without default values represent required dependencies, i.e.
    /// things the object cannot exist without. These should never be nullable.
    ///
    /// Pattern:
    /// - Required parameters (no default) = non-nullable
    /// - Optional parameters (with default value) = can be nullable
    /// - Optional dependencies without defaults = use properties or setter methods
    ///
    /// Exclusions:
    /// - Private constructors: Used with factory pattern, internal wiring allowed
    /// - Exception classes: Inner exceptions are nullable by .NET convention
    /// - Parameters with default values: These are intentionally optional
    /// </summary>
    [Fact]
    public void Required_Constructor_Parameters_Should_Not_Be_Nullable()
    {
        var errors = new List<string>();
        var nullabilityContext = new NullabilityInfoContext();

        foreach (var type in AllTypes.Where(t => (t.IsClass || IsRecord(t)) && !t.IsAbstract && (t.Namespace?.StartsWith(RootNamespace) ?? false)))
        {
            // Skip compiler-generated types
            if (type.Name.Contains('<') || type.Name.Contains('>'))
            {
                continue;
            }

            // Skip records and DTOs - they often have many optional parameters via primary constructor
            if (IsDtoOrRecord(type))
            {
                continue;
            }

            // Skip exception classes - inner exceptions are nullable by convention
            if (typeof(Exception).IsAssignableFrom(type))
            {
                continue;
            }

            // Skip IDisposable types - nullable constructor parameters are valid for optional resources
            if (typeof(IDisposable).IsAssignableFrom(type) || typeof(IAsyncDisposable).IsAssignableFrom(type))
            {
                continue;
            }

            // Check public and internal constructors (exclude private - used with factory pattern)
            var constructors = type
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(c => !c.IsPrivate);

            foreach (var ctor in constructors)
            {
                foreach (var param in ctor.GetParameters())
                {
                    // Skip parameters with default values - these are intentionally optional
                    if (param.HasDefaultValue)
                    {
                        continue;
                    }

                    // Check for Nullable<T> value types (int?, bool?, etc.)
                    if (Nullable.GetUnderlyingType(param.ParameterType) != null)
                    {
                        errors.Add($"{type.Name} constructor has nullable parameter '{param.Name}' of type '{param.ParameterType.Name}'. " +
                                   $"Required constructor parameters should be non-nullable.");
                        continue;
                    }

                    // Check for nullable reference types (string?, IService?, etc.)
                    try
                    {
                        var nullabilityInfo = nullabilityContext.Create(param);
                        if (nullabilityInfo.WriteState == NullabilityState.Nullable ||
                            nullabilityInfo.ReadState == NullabilityState.Nullable)
                        {
                            errors.Add($"{type.Name} constructor has nullable parameter '{param.Name}' of type '{param.ParameterType.Name}'. " +
                                       $"Required constructor parameters should be non-nullable.");
                        }
                    }
                    catch
                    {
                        // If we can't determine nullability, skip this parameter
                    }
                }
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Constructor parameters should not be nullable (use properties for optional dependencies):{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Detects nullable parameters with default null that are then null-checked in constructor body.
    ///
    /// This pattern indicates behavior branching based on null, which should be moved
    /// to a static factory method. Ask yourself:
    /// - Does behavior change if it's null?
    /// - Would two different classes make more sense?
    /// - Is this actually a domain concept?
    /// - Am I encoding a state machine with null?
    ///
    /// If yes to any → model it explicitly in a factory method.
    ///
    /// Bad:
    /// <code>
    /// public MyClass(string? path = null)
    /// {
    ///     _path = path ?? GetDefault();  // Decision in constructor!
    /// }
    /// </code>
    ///
    /// Good:
    /// <code>
    /// public static MyClass Create(string? customPath = null)
    /// {
    ///     var path = customPath ?? GetDefault();  // Decision in factory
    ///     return new MyClass(path);
    /// }
    /// private MyClass(string path) => _path = path;  // Just stores
    /// </code>
    /// </summary>
    [Fact]
    public void Nullable_Default_Parameters_Should_Not_Be_Null_Checked_In_Constructor()
    {
        var sourceDirectory = ArchitectureHelper.SourceDirectory;

        if (!Directory.Exists(sourceDirectory))
        {
            // Skip if source not available (e.g., in CI without source)
            return;
        }

        var analyzer = new NullDefaultParameterAnalyzer();
        var violations = analyzer.AnalyzeDirectory(sourceDirectory);

        if (violations.Count != 0)
        {
            var errorMessages = violations.Select(v =>
                $"{Path.GetFileName(v.FilePath)}:{v.LineNumber} - {v.ClassName} constructor null-checks parameter '{v.ParameterName}' ({v.ParameterType}). " +
                $"Pattern: {v.NullCheckPattern}. Move decision logic to a static Create method.");

            Assert.Fail($"Nullable default parameters should not be null-checked in constructors (use factory methods for decisions):{Environment.NewLine}{string.Join(Environment.NewLine, errorMessages)}");
        }
    }

    /// <summary>
    /// Ensures non-sealed classes that override Equals also seal the method or are abstract.
    /// Motivation: If a non-sealed class overrides Equals without sealing it,
    /// derived classes might not properly override it, breaking the equality contract.
    /// Either seal the Equals method or make the class sealed/abstract.
    /// </summary>
    [Fact]
    public void Non_Sealed_Classes_Overriding_Equals_Should_Seal_Or_Be_Abstract()
    {
        var errors = new List<string>();

        foreach (var type in AllTypes.Where(t => t.IsClass && !t.IsSealed && !t.IsAbstract && (t.Namespace?.StartsWith(RootNamespace) ?? false)))
        {
            var equalsMethod = type.GetMethod("Equals", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, new[] { typeof(object) }, null);

            if (equalsMethod?.IsFinal == false)
            {
                errors.Add($"{type.FullName} overrides Equals but doesn't seal it (derived classes may break equality)");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Unsealed Equals in non-sealed classes:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Ensures that types do not declare events.
    /// Motivation: Events hide dependencies, lifetimes, and ownership,
    /// which conflicts with explicit architecture and DI conventions.
    /// </summary>
    [Fact]
    public void Types_Must_Not_Declare_Events()
    {
        var typesWithEvents = AllTypes
            .Where(t => t.Namespace?.StartsWith($"{RootNamespace}") == true)
            .Where(t => t.GetEvents(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Length > 0)
            .Select(t => $"{t.FullName}: {string.Join(", ", t.GetEvents(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Select(e => e.Name))}")
            .ToList();

        if (typesWithEvents.Count != 0)
        {
            Assert.Fail($"Event declarations detected (use callback interfaces instead):{Environment.NewLine}{string.Join(Environment.NewLine, typesWithEvents)}");
        }
    }

    /// <summary>
    /// Ensures types don't dispose injected dependencies.
    /// Motivation: Disposing a dependency that was injected via constructor can cause
    /// issues for other code that still holds references to it. Only dispose resources
    /// that the type owns (creates internally).
    ///
    /// Detection approach: Combines reflection (to identify injected fields) with
    /// source code parsing (to detect disposal calls in Dispose method).
    ///
    /// Note: Types with "leaveOpen" or "ownsResource" constructor parameters are excluded
    /// as they properly handle ownership via explicit flags.
    /// </summary>
    [Fact]
    public void Disposable_Types_Should_Not_Dispose_Injected_Dependencies()
    {
        var errors = new List<string>();
        var sourceDirectory = ArchitectureHelper.SourceDirectory;

        // Patterns that indicate proper ownership handling (checked as word parts, not exact substring)
        var ownershipParamPatterns = new[] { "leave", "owns", "takeownership", "disposeinner" };

        foreach (var type in AllTypes.Where(t => t.IsClass && !t.IsAbstract && (t.Namespace?.StartsWith(RootNamespace) ?? false)))
        {
            // Skip types that don't implement IDisposable
            if (!typeof(IDisposable).IsAssignableFrom(type))
            {
                continue;
            }

            // Skip compiler-generated types
            if (type.Name.Contains('<') || type.Name.Contains('>'))
            {
                continue;
            }

            // Get all constructor parameters
            var ctorParams = type
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(c => c.GetParameters())
                .ToList();

            // Skip types that have explicit ownership parameters (they handle this correctly)
            var hasOwnershipParam = ctorParams.Any(p =>
                ownershipParamPatterns.Any(pattern => p.Name?.ToLowerInvariant().Contains(pattern) == true));
            if (hasOwnershipParam)
            {
                continue;
            }

            var ctorParamTypes = ctorParams.Select(p => p.ParameterType).ToHashSet();

            // Get all disposable instance fields
            var disposableFields = type
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => typeof(IDisposable).IsAssignableFrom(f.FieldType) && !f.FieldType.IsValueType)
                .ToList();

            if (disposableFields.Count == 0)
            {
                continue;
            }

            // Find injected fields (type appears in constructor parameters)
            var injectedFields = disposableFields
                .Where(f => ctorParamTypes.Contains(f.FieldType))
                .ToList();

            if (injectedFields.Count == 0)
            {
                continue;
            }

            // Find source file and check Dispose method
            var sourceFile = FindSourceFile(sourceDirectory, type);
            if (sourceFile == null)
            {
                continue;
            }

            var disposeBody = ExtractDisposeMethodBody(sourceFile, type.Name);
            if (disposeBody == null)
            {
                continue;
            }

            // Check if any injected field is disposed
            foreach (var field in injectedFields)
            {
                if (IsFieldDisposedInMethod(disposeBody, field.Name))
                {
                    errors.Add($"{type.FullName} disposes injected field '{field.Name}' (type: {field.FieldType.Name})");
                }
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Injected dependencies being disposed (don't dispose what you don't own):{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
    }

    /// <summary>
    /// Finds the source file for a given type by searching for the class definition.
    /// </summary>
    private static string? FindSourceFile(string sourceDirectory, Type type)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return null;
        }

        // Search for files containing the class definition
        var searchPattern = $"class {type.Name}";

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(file);
                if (content.Contains(searchPattern) &&
                    (content.Contains($"class {type.Name} ") ||
                     content.Contains($"class {type.Name}:") ||
                     content.Contains($"class {type.Name}\r") ||
                     content.Contains($"class {type.Name}\n")))
                {
                    return file;
                }
            }
            catch
            {
                // Skip files that can't be read
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the body of the Dispose method from source code using brace counting.
    /// Handles both direct Dispose() and Dispose(bool) patterns.
    /// </summary>
    private static string? ExtractDisposeMethodBody(string filePath, string typeName)
    {
        try
        {
            var content = File.ReadAllText(filePath);

            // Find the class definition first to scope our search
            var classPattern = $@"class\s+{System.Text.RegularExpressions.Regex.Escape(typeName)}[\s:<]";
            var classMatch = System.Text.RegularExpressions.Regex.Match(content, classPattern);
            if (!classMatch.Success)
            {
                return null;
            }

            var classStart = classMatch.Index;

            // Find Dispose method within the class
            // Look for: public void Dispose() or void Dispose() or private void Dispose(bool
            var disposePatterns = new[]
            {
                @"void\s+Dispose\s*\(\s*\)",           // void Dispose()
                @"void\s+Dispose\s*\(\s*bool\s+\w+\s*\)" // void Dispose(bool disposing)
            };

            string? methodBody = null;

            foreach (var pattern in disposePatterns)
            {
                var regex = new System.Text.RegularExpressions.Regex(pattern);
                var match = regex.Match(content, classStart);

                if (match.Success)
                {
                    // Find the opening brace
                    var braceStart = content.IndexOf('{', match.Index + match.Length);
                    if (braceStart == -1)
                    {
                        continue;
                    }

                    // Count braces to find the matching closing brace
                    var braceCount = 1;
                    var pos = braceStart + 1;

                    while (pos < content.Length && braceCount > 0)
                    {
                        if (content[pos] == '{')
                        {
                            braceCount++;
                        }
                        else if (content[pos] == '}')
                        {
                            braceCount--;
                        }

                        pos++;
                    }

                    if (braceCount == 0)
                    {
                        var body = content.Substring(braceStart + 1, pos - braceStart - 2);
                        // Combine all dispose method bodies found
                        methodBody = methodBody == null ? body : methodBody + "\n" + body;
                    }
                }
            }

            return methodBody;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if a field is disposed within a method body.
    /// Looks for patterns like: _field.Dispose(), _field?.Dispose(), field.Dispose()
    /// </summary>
    private static bool IsFieldDisposedInMethod(string methodBody, string fieldName)
    {
        // Handle both _fieldName and fieldName patterns
        var patterns = new[]
        {
            $@"\b{System.Text.RegularExpressions.Regex.Escape(fieldName)}\s*\?\s*\.\s*Dispose\s*\(",  // _field?.Dispose(
            $@"\b{System.Text.RegularExpressions.Regex.Escape(fieldName)}\s*\.\s*Dispose\s*\(",      // _field.Dispose(
            $@"\b{System.Text.RegularExpressions.Regex.Escape(fieldName)}\s*\?\s*\.\s*Close\s*\(",   // _field?.Close(
            $@"\b{System.Text.RegularExpressions.Regex.Escape(fieldName)}\s*\.\s*Close\s*\(",        // _field.Close(
        };

        foreach (var pattern in patterns)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(methodBody, pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ensures all event subscriptions (+=) have corresponding unsubscriptions (-=).
    /// Motivation: Event handlers that are subscribed but never unsubscribed cause memory leaks
    /// because the event source holds a reference to the handler delegate, preventing the
    /// subscriber from being garbage collected.
    ///
    /// Detection approach: Uses source code parsing to find event subscription patterns
    /// and verifies that each subscription has a corresponding unsubscription in the same class.
    ///
    /// Note: Anonymous lambda subscriptions cannot be unsubscribed directly, so this test
    /// checks that handler delegates are stored in fields for proper lifecycle management.
    /// </summary>
    [Fact]
    public void Event_Subscriptions_Must_Have_Corresponding_Unsubscriptions()
    {
        var errors = new List<string>();
        var sourceDirectory = ArchitectureHelper.SourceDirectory;

        if (!Directory.Exists(sourceDirectory))
        {
            Assert.Fail($"Source directory not found: {sourceDirectory}");
            return;
        }

        // Common event names in .NET that should be properly managed
        var eventPatterns = new[]
        {
            "Elapsed",           // System.Timers.Timer
            "Tick",              // System.Threading.Timer patterns
            "ProcessExit",       // AppDomain
            "UnhandledException",// AppDomain
            "Closing",           // Windows/Forms
            "Closed",
            "Click",
            "Changed",
            "Completed",
            "Received",
            "Connected",
            "Disconnected"
        };

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(file);
                var fileName = Path.GetFileName(file);
                var relativePath = Path.GetRelativePath(sourceDirectory, file);

                // Skip test files and generated files
                if (relativePath.Contains("Tests") || relativePath.Contains("obj") || relativePath.Contains("bin"))
                {
                    continue;
                }

                // Find all classes in the file
                var classMatches = System.Text.RegularExpressions.Regex.Matches(
                    content,
                    @"(?:public|private|internal|protected)\s+(?:sealed\s+|abstract\s+)?(?:partial\s+)?class\s+(\w+)");

                foreach (System.Text.RegularExpressions.Match classMatch in classMatches)
                {
                    var className = classMatch.Groups[1].Value;
                    var classBody = ExtractClassBody(content, classMatch.Index);

                    if (classBody == null)
                    {
                        continue;
                    }

                    // Find event subscriptions with lambda handlers (anonymous delegates)
                    // Pattern: .EventName += (args) => ... or .EventName += delegate ...
                    foreach (var eventName in eventPatterns)
                    {
                        // Find subscriptions with anonymous handlers
                        var anonymousSubPattern = $@"\.{eventName}\s*\+=\s*(?:\([^)]*\)\s*=>|delegate)";
                        var anonymousSubs = System.Text.RegularExpressions.Regex.Matches(classBody, anonymousSubPattern);

                        if (anonymousSubs.Count > 0)
                        {
                            // Check if there's a stored handler field pattern nearby or corresponding unsubscription
                            // Look for patterns like: _handler = (s, e) => ... followed by .EventName += _handler
                            // or a -= pattern for the same event

                            var unsubPattern = $@"\.{eventName}\s*-=";
                            var hasUnsub = System.Text.RegularExpressions.Regex.IsMatch(classBody, unsubPattern);

                            // Check if the class implements IDisposable (should clean up in Dispose)
                            var implementsDisposable = classBody.Contains("IDisposable") ||
                                                       classBody.Contains("void Dispose");

                            // Only flag if class is disposable but doesn't unsubscribe
                            // (non-disposable classes with static lifetime may be acceptable)
                            if (implementsDisposable && !hasUnsub)
                            {
                                foreach (System.Text.RegularExpressions.Match sub in anonymousSubs)
                                {
                                    // Check if this is in a static constructor or AppDomain event (acceptable)
                                    var context = GetSurroundingContext(classBody, sub.Index, 200);
                                    if (context.Contains("ProcessExit") ||
                                        context.Contains("static " + className))
                                    {
                                        continue; // Skip static initializers and process-lifetime events
                                    }

                                    errors.Add($"{relativePath} ({className}): .{eventName} += (anonymous) - no corresponding -= found. " +
                                              "Store handler in field for proper unsubscription.");
                                }
                            }
                        }

                        // Find subscriptions with named handlers: .EventName += MethodName or .EventName += _handler
                        var namedSubPattern = $@"\.{eventName}\s*\+=\s*(\w+)";
                        var namedSubs = System.Text.RegularExpressions.Regex.Matches(classBody, namedSubPattern);

                        foreach (System.Text.RegularExpressions.Match sub in namedSubs)
                        {
                            var handlerName = sub.Groups[1].Value;

                            // Skip if handler is 'new' (creating new delegate) or common patterns
                            if (handlerName == "new" || handlerName == "null")
                            {
                                continue;
                            }

                            // Check for corresponding unsubscription with same handler
                            var unsubPattern = $@"\.{eventName}\s*-=\s*{handlerName}\b";
                            var hasUnsub = System.Text.RegularExpressions.Regex.IsMatch(classBody, unsubPattern);

                            // Check if class implements IDisposable
                            var implementsDisposable = classBody.Contains("IDisposable") ||
                                                       classBody.Contains("void Dispose");

                            if (implementsDisposable && !hasUnsub)
                            {
                                // Check context - skip static constructors
                                var context = GetSurroundingContext(classBody, sub.Index, 200);
                                if (context.Contains("static " + className))
                                {
                                    continue;
                                }

                                errors.Add($"{relativePath} ({className}): .{eventName} += {handlerName} - no corresponding -= {handlerName} found");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Skip files that can't be read
                System.Diagnostics.Debug.WriteLine($"Error reading {file}: {ex.Message}");
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Event subscription/unsubscription imbalance detected (potential memory leaks):\n" +
                       $"Fix: Store event handlers in fields and unsubscribe in Dispose() or Stop() methods.\n\n" +
                       string.Join("\n", errors));
        }
    }

    /// <summary>
    /// Extracts the body of a class starting from the class declaration.
    /// </summary>
    private static string? ExtractClassBody(string content, int classStartIndex)
    {
        var braceStart = content.IndexOf('{', classStartIndex);
        if (braceStart == -1)
        {
            return null;
        }

        var braceCount = 1;
        var pos = braceStart + 1;

        while (pos < content.Length && braceCount > 0)
        {
            if (content[pos] == '{')
            {
                braceCount++;
            }
            else if (content[pos] == '}')
            {
                braceCount--;
            }

            pos++;
        }

        if (braceCount != 0)
        {
            return null;
        }

        return content[braceStart..pos];
    }

    /// <summary>
    /// Gets surrounding context around a position in source code.
    /// </summary>
    private static string GetSurroundingContext(string content, int position, int radius)
    {
        var start = Math.Max(0, position - radius);
        var end = Math.Min(content.Length, position + radius);
        return content[start..end];
    }

    /// <summary>
    /// Ensures fire-and-forget tasks have exception handling.
    /// Motivation: Tasks started with `_ = SomeAsync()` can fail silently, hiding bugs
    /// and causing unexpected behavior. All fire-and-forget tasks should observe
    /// exceptions via ContinueWith, try-catch, or similar patterns.
    ///
    /// Detection approach: Finds patterns like `_ = Method(` and checks if there's
    /// a corresponding `.ContinueWith(` on the same line or nearby.
    /// </summary>
    [Fact]
    public void Fire_And_Forget_Tasks_Must_Handle_Exceptions()
    {
        var errors = new List<string>();
        var sourceDirectory = ArchitectureHelper.SourceDirectory;

        if (!Directory.Exists(sourceDirectory))
        {
            Assert.Fail($"Source directory not found: {sourceDirectory}");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(sourceDirectory, file);

                // Skip generated files
                if (relativePath.Contains("obj") || relativePath.Contains("bin"))
                {
                    continue;
                }

                var lines = content.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var lineNumber = i + 1;

                    // Find fire-and-forget pattern: _ = SomeMethod( or _ = something.Method(
                    // But NOT _ = variable (simple assignment)
                    if (System.Text.RegularExpressions.Regex.IsMatch(line, @"_\s*=\s*\w+.*\("))
                    {
                        // Check if this line or next few lines have .ContinueWith
                        var context = string.Join("\n", lines.Skip(i).Take(5));

                        // Skip if it has proper exception handling
                        if (context.Contains(".ContinueWith(") ||
                            context.Contains("try") ||
                            context.Contains("catch"))
                        {
                            continue;
                        }

                        // Skip common non-async patterns
                        if (line.Contains("TryRemove") ||
                            line.Contains("TryGetValue") ||
                            line.Contains("TryDequeue") ||
                            line.Contains("TryAdd") ||
                            line.Contains("Interlocked") ||
                            line.Contains("// Fire-and-forget OK") ||
                            line.Contains("// fire-and-forget") ||
                            line.Contains("//Fire-and-forget"))
                        {
                            continue;
                        }

                        // Skip common intentional fire-and-forget patterns in networking code
                        // These are messages where failure is non-critical and handled elsewhere
                        if (line.Contains("SendMessageAsync") ||      // Peer messages - failures handled by disconnect
                            line.Contains("SendAsync") ||             // UDP sends - inherently unreliable
                            line.Contains("AnnounceAsync") ||         // Tracker announces - retry on timer
                            line.Contains("UnmapUpnpSafeAsync") ||    // UPnP cleanup - best effort
                            line.Contains("StartUpnpSafeAsync"))      // UPnP setup - best effort
                        {
                            continue;
                        }

                        // Check if it's actually an async call (contains Async or await-able patterns)
                        if (line.Contains("Async(") || line.Contains("Task.Run") || line.Contains("Task.Factory"))
                        {
                            errors.Add($"{relativePath}:{lineNumber}: {line.Trim()}");
                        }
                    }
                }
            }
            catch
            {
                // Skip files that can't be read
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Fire-and-forget tasks without exception handling detected:\n" +
                       $"Fix: Add .ContinueWith(t => {{ if (t.IsFaulted) Log(t.Exception); }}) or wrap in try-catch.\n\n" +
                       string.Join("\n", errors));
        }
    }

    /// <summary>
    /// Ensures ArrayPool rentals are properly returned.
    /// Motivation: ArrayPool.Rent() provides pooled buffers for performance, but
    /// failing to return them causes memory leaks and defeats the purpose of pooling.
    ///
    /// Detection approach: Counts Rent() calls and Return() calls per file/class.
    /// Also checks that classes using ArrayPool implement IDisposable for cleanup.
    /// </summary>
    [Fact]
    public void ArrayPool_Rentals_Must_Be_Returned()
    {
        var errors = new List<string>();
        var sourceDirectory = ArchitectureHelper.SourceDirectory;

        if (!Directory.Exists(sourceDirectory))
        {
            Assert.Fail($"Source directory not found: {sourceDirectory}");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(sourceDirectory, file);

                // Skip generated files
                if (relativePath.Contains("obj") || relativePath.Contains("bin"))
                {
                    continue;
                }

                // Find ArrayPool.Rent calls
                var rentMatches = System.Text.RegularExpressions.Regex.Matches(
                    content, @"ArrayPool<\w+>\.Shared\.Rent\(");

                if (rentMatches.Count == 0)
                {
                    continue;
                }

                // Find ArrayPool.Return calls
                var returnMatches = System.Text.RegularExpressions.Regex.Matches(
                    content, @"ArrayPool<\w+>\.Shared\.Return\(");

                // Check if class implements IDisposable (for cleanup on error paths)
                var hasDispose = content.Contains("IDisposable") || content.Contains("void Dispose");
                var hasFinally = content.Contains("finally");
                var hasUsing = System.Text.RegularExpressions.Regex.IsMatch(content, @"using\s*\(|using\s+var");

                // If there are more rents than returns and no cleanup mechanism, flag it
                if (rentMatches.Count > returnMatches.Count && !hasFinally && !hasUsing)
                {
                    errors.Add($"{relativePath}: {rentMatches.Count} Rent() calls but only {returnMatches.Count} Return() calls. " +
                              $"Ensure all rented buffers are returned in finally blocks or using statements.");
                }

                // If using ArrayPool but not implementing IDisposable, might be a problem
                if (rentMatches.Count > 0 && !hasDispose && !hasFinally && !hasUsing)
                {
                    // Check if it's a static helper class (acceptable)
                    if (!content.Contains("static class"))
                    {
                        errors.Add($"{relativePath}: Uses ArrayPool.Rent() but doesn't implement IDisposable or use finally/using for cleanup.");
                    }
                }
            }
            catch
            {
                // Skip files that can't be read
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"ArrayPool rental issues detected:\n" +
                       $"Fix: Always return rented buffers in finally blocks, using statements, or Dispose methods.\n\n" +
                       string.Join("\n", errors));
        }
    }

    /// <summary>
    /// Ensures async code doesn't block synchronously.
    /// Motivation: Using .Result, .Wait(), or .GetAwaiter().GetResult() on tasks
    /// can cause deadlocks, especially when called from UI or ASP.NET contexts.
    /// These patterns also defeat the purpose of async programming.
    ///
    /// Note: Test code is excluded as blocking in tests is sometimes acceptable.
    /// Constructors are also flagged separately by the lightweight constructor test.
    /// </summary>
    [Fact]
    public void Async_Code_Should_Not_Block_Synchronously()
    {
        var errors = new List<string>();
        var sourceDirectory = ArchitectureHelper.SourceDirectory;

        if (!Directory.Exists(sourceDirectory))
        {
            Assert.Fail($"Source directory not found: {sourceDirectory}");
            return;
        }

        // Patterns that indicate synchronous blocking on async code
        var blockingPatterns = new[]
        {
            (@"\.Result\b(?!\s*[=!<>])", ".Result"),           // .Result but not .ResultCode or comparisons
            (@"\.Wait\(\)", ".Wait()"),                         // .Wait()
            (@"\.GetAwaiter\(\)\.GetResult\(\)", ".GetAwaiter().GetResult()"),
            (@"Task\.WaitAll\(", "Task.WaitAll("),
            (@"Task\.WaitAny\(", "Task.WaitAny("),
        };

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(sourceDirectory, file);

                // Skip generated files and test files
                if (relativePath.Contains("obj") || relativePath.Contains("bin") || relativePath.Contains("Test"))
                {
                    continue;
                }

                var lines = content.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var lineNumber = i + 1;

                    foreach (var (pattern, description) in blockingPatterns)
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(line, pattern))
                        {
                            // Skip if it's in a comment
                            var trimmed = line.TrimStart();
                            if (trimmed.StartsWith("//") || trimmed.StartsWith("*"))
                            {
                                continue;
                            }

                            // Skip if explicitly marked as acceptable (including pragma disable for VSTHRD002)
                            if (line.Contains("// Blocking OK") || line.Contains("// Sync context") || line.Contains("VSTHRD002"))
                            {
                                continue;
                            }

                            // Skip specific acceptable patterns
                            if (line.Contains("_processingTask.Wait(TimeSpan") || // Shutdown with timeout
                                (line.Contains("GetAwaiter().GetResult()") && line.Contains("static"))) // Static initializers
                            {
                                continue;
                            }

                            errors.Add($"{relativePath}:{lineNumber}: Uses {description} - {line.Trim()}");
                        }
                    }
                }
            }
            catch
            {
                // Skip files that can't be read
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Synchronous blocking on async code detected (potential deadlocks):\n" +
                       $"Fix: Use 'await' instead, or if in sync context, restructure to be fully async.\n\n" +
                       string.Join("\n", errors));
        }
    }

    /// <summary>
    /// Ensures exceptions are not silently swallowed.
    /// Motivation: Empty catch blocks or catch blocks that don't log, rethrow,
    /// or handle the exception hide bugs and make debugging extremely difficult.
    ///
    /// Detection approach: Finds catch blocks and checks if they have meaningful
    /// handling (logging, rethrowing, or setting error state).
    /// </summary>
    [Fact]
    public void Exceptions_Should_Not_Be_Silently_Swallowed()
    {
        var errors = new List<string>();
        var sourceDirectory = ArchitectureHelper.SourceDirectory;

        if (!Directory.Exists(sourceDirectory))
        {
            Assert.Fail($"Source directory not found: {sourceDirectory}");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(sourceDirectory, file);

                // Skip generated files
                if (relativePath.Contains("obj") || relativePath.Contains("bin"))
                {
                    continue;
                }

                // Find all catch blocks
                var catchPattern = @"catch\s*\([^)]*\)\s*\{([^{}]*(?:\{[^{}]*\}[^{}]*)*)\}";
                var matches = System.Text.RegularExpressions.Regex.Matches(content, catchPattern);

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var catchBody = match.Groups[1].Value.Trim();
                    var fullMatch = match.Value;

                    // Find line number
                    var lineNumber = content[..match.Index].Count(c => c == '\n') + 1;

                    // Skip if catch body has meaningful handling
                    if (string.IsNullOrWhiteSpace(catchBody))
                    {
                        // Completely empty catch block
                        errors.Add($"{relativePath}:{lineNumber}: Empty catch block - exceptions silently swallowed");
                        continue;
                    }

                    // Check for meaningful handling patterns
                    var hasLogging = catchBody.Contains("Log") ||
                                    catchBody.Contains("log") ||
                                    catchBody.Contains("Console.") ||
                                    catchBody.Contains("Debug.") ||
                                    catchBody.Contains("Trace.");

                    var hasRethrow = catchBody.Contains("throw");

                    var hasErrorState = catchBody.Contains("error") ||
                                       catchBody.Contains("Error") ||
                                       catchBody.Contains("failed") ||
                                       catchBody.Contains("Failed") ||
                                       catchBody.Contains("= false") ||
                                       catchBody.Contains("= null") ||
                                       catchBody.Contains("return");

                    var hasComment = catchBody.Contains("//") ||
                                    catchBody.Contains("/*") ||
                                    catchBody.Contains("expected") ||
                                    catchBody.Contains("ignore") ||
                                    catchBody.Contains("Ignore");

                    var isSpecificException = fullMatch.Contains("TaskCanceledException") ||
                                             fullMatch.Contains("OperationCanceledException") ||
                                             fullMatch.Contains("ObjectDisposedException") ||
                                             fullMatch.Contains("IOException") ||
                                             fullMatch.Contains("SocketException");

                    // If no meaningful handling and not a specific expected exception
                    if (!hasLogging && !hasRethrow && !hasErrorState && !hasComment && !isSpecificException)
                    {
                        // Get a snippet of the catch for context
                        var snippet = catchBody.Length > 50 ? string.Concat(catchBody.AsSpan(0, 50), "...") : catchBody;
                        errors.Add($"{relativePath}:{lineNumber}: Catch block may swallow exception without handling: {snippet.Replace("\n", " ").Replace("\r", "")}");
                    }
                }
            }
            catch
            {
                // Skip files that can't be read
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Potentially swallowed exceptions detected:\n" +
                       $"Fix: Log the exception, rethrow it, or add a comment explaining why it's safe to ignore.\n\n" +
                       string.Join("\n", errors.Take(20))); // Limit output
        }
    }

    /// <summary>
    /// Ensures network operations have timeouts configured.
    /// Motivation: Network operations without timeouts can hang indefinitely,
    /// causing resource exhaustion and unresponsive applications. All network
    /// clients should have explicit timeout configuration.
    ///
    /// Detection approach: Finds instantiations of network clients and checks
    /// if timeout properties are set nearby.
    /// </summary>
    [Fact]
    public void Network_Operations_Should_Have_Timeouts()
    {
        var errors = new List<string>();
        var sourceDirectory = ArchitectureHelper.SourceDirectory;

        if (!Directory.Exists(sourceDirectory))
        {
            Assert.Fail($"Source directory not found: {sourceDirectory}");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(sourceDirectory, file);

                // Skip generated files
                if (relativePath.Contains("obj") || relativePath.Contains("bin"))
                {
                    continue;
                }

                // Find network client instantiations - focus on HttpClient and TcpClient
                // UdpClient is excluded as UDP is inherently unreliable and typically uses
                // CancellationToken-based timeouts via ReceiveAsync, not property-based timeouts
                var networkPatterns = new[]
                {
                    ("new HttpClient", "HttpClient", "Timeout"),
                    ("new TcpClient", "TcpClient", "ReceiveTimeout|SendTimeout|ConnectAsync"),
                };

                foreach (var (pattern, clientType, timeoutPattern) in networkPatterns)
                {
                    var matches = System.Text.RegularExpressions.Regex.Matches(content, System.Text.RegularExpressions.Regex.Escape(pattern));

                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        var lineNumber = content[..match.Index].Count(c => c == '\n') + 1;

                        // Get surrounding context (next 20 lines or so)
                        var endIndex = Math.Min(content.Length, match.Index + 1000);
                        var context = content[match.Index..endIndex];

                        // Check if timeout is set in the context
                        var hasTimeout = System.Text.RegularExpressions.Regex.IsMatch(context, timeoutPattern) ||
                                        context.Contains("Timeout =") ||
                                        context.Contains("Timeout=") ||
                                        context.Contains(".Timeout") ||
                                        context.Contains("ConnectTimeout") ||
                                        context.Contains("TimeSpan") ||
                                        context.Contains("CancellationToken") ||
                                        context.Contains("cancellation");

                        // Check for SocketsHttpHandler configuration (proper way for HttpClient)
                        var hasHandlerConfig = context.Contains("SocketsHttpHandler") ||
                                              context.Contains("HttpClientHandler");

                        // Check if it's a static/shared client (timeout usually set at creation)
                        var isStatic = context.Contains("static readonly") ||
                                      context.Contains("static HttpClient") ||
                                      context.Contains("SharedClient");

                        // Check if timeout is managed via wrapper method or configuration
                        var hasWrapperTimeout = context.Contains("AdaptiveTimeout") ||
                                               context.Contains("ConnectionTimeout") ||
                                               context.Contains("_timeout");

                        if (!hasTimeout && !hasHandlerConfig && !isStatic && !hasWrapperTimeout)
                        {
                            errors.Add($"{relativePath}:{lineNumber}: {clientType} created without explicit timeout configuration");
                        }
                    }
                }
            }
            catch
            {
                // Skip files that can't be read
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Network clients without timeout configuration detected:\n" +
                       $"Fix: Set Timeout, ReceiveTimeout, SendTimeout, or ConnectTimeout properties.\n\n" +
                       string.Join("\n", errors));
        }
    }

    /// <summary>
    /// Ensures locks are not held across await points.
    /// Motivation: Holding a lock while awaiting is dangerous because:
    /// 1. The continuation may run on a different thread, violating lock semantics
    /// 2. It can cause deadlocks if other code tries to acquire the same lock
    /// 3. It holds the lock for an unpredictable duration
    ///
    /// Use SemaphoreSlim.WaitAsync() for async-compatible synchronization.
    /// </summary>
    [Fact]
    public void Locks_Should_Not_Be_Held_Across_Await()
    {
        var errors = new List<string>();
        var sourceDirectory = ArchitectureHelper.SourceDirectory;

        if (!Directory.Exists(sourceDirectory))
        {
            Assert.Fail($"Source directory not found: {sourceDirectory}");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(file);
                var relativePath = Path.GetRelativePath(sourceDirectory, file);

                // Skip generated files
                if (relativePath.Contains("obj") || relativePath.Contains("bin"))
                {
                    continue;
                }

                // Find lock blocks and check for await inside
                // Pattern: lock (...) { ... await ... }
                var lockPattern = @"lock\s*\([^)]+\)\s*\{";
                var lockMatches = System.Text.RegularExpressions.Regex.Matches(content, lockPattern);

                foreach (System.Text.RegularExpressions.Match match in lockMatches)
                {
                    var lineNumber = content[..match.Index].Count(c => c == '\n') + 1;

                    // Extract the lock block body
                    var braceStart = match.Index + match.Length - 1;
                    var braceCount = 1;
                    var pos = braceStart + 1;

                    while (pos < content.Length && braceCount > 0)
                    {
                        if (content[pos] == '{')
                        {
                            braceCount++;
                        }
                        else if (content[pos] == '}')
                        {
                            braceCount--;
                        }

                        pos++;
                    }

                    if (braceCount != 0)
                    {
                        continue;
                    }

                    var lockBody = content.Substring(braceStart + 1, pos - braceStart - 2);

                    // Check if there's an await inside the lock
                    if (System.Text.RegularExpressions.Regex.IsMatch(lockBody, @"\bawait\b"))
                    {
                        // Skip if the await is inside Task.Run (which starts a new task, doesn't hold lock)
                        // Pattern: Task.Run(async () => ... await ...)
                        if (System.Text.RegularExpressions.Regex.IsMatch(lockBody, @"Task\.Run\s*\(\s*async"))
                        {
                            continue;
                        }

                        var awaitLine = lockBody.Split('\n')
                            .Select((line, idx) => new { line, idx })
                            .FirstOrDefault(x => x.line.Contains("await"));

                        var snippet = awaitLine?.line.Trim() ?? "await ...";
                        errors.Add($"{relativePath}:{lineNumber}: Lock held across await point: {snippet}");
                    }
                }

                // Also check for Monitor.Enter without corresponding async-safe patterns
                if (content.Contains("Monitor.Enter"))
                {
                    var lines = content.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Contains("Monitor.Enter"))
                        {
                            // Check next 20 lines for await
                            var context = string.Join("\n", lines.Skip(i).Take(20));
                            if (context.Contains("await") && !context.Contains("Monitor.Exit"))
                            {
                                errors.Add($"{relativePath}:{i + 1}: Monitor.Enter may be held across await");
                            }
                        }
                    }
                }
            }
            catch
            {
                // Skip files that can't be read
            }
        }

        if (errors.Count != 0)
        {
            Assert.Fail($"Locks held across await points detected (dangerous!):\n" +
                       $"Fix: Use SemaphoreSlim.WaitAsync() instead of lock for async code.\n\n" +
                       string.Join("\n", errors));
        }
    }

    /// <summary>
    /// Encourages constructors to be lightweight and free of expensive or side-effecting work.
    ///
    /// Motivation:
    /// Constructors should primarily establish object invariants.
    /// Performing I/O, blocking operations, or other heavyweight work during construction
    /// makes object creation unpredictable, harder to test, and harder to compose.
    ///
    /// This test uses heuristics to detect commonly problematic patterns.
    /// It may produce false positives and should be treated as a design signal,
    /// not an absolute correctness rule.
    ///
    /// Guidance:
    /// When this test fails, consider:
    /// - Deferring work to an Init or Start method
    /// - Using lazy initialization
    /// - Moving decision-making or expensive work to a static factory
    /// - Explicitly allowing the behavior if construction cost is intentional with [AllowHeavyConstructor]
    /// </summary>
    [Fact]
    public void Constructors_Should_Be_Lightweight()
    {
        bool IsCompilerGenerated(Type t) =>
            t.Name.Contains('<') || t.Name.Contains('>') ||
            t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false);

        var typesToAnalyze = AllTypes
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.Namespace?.StartsWith(RootNamespace) ?? false)
            .Where(t => !IsCompilerGenerated(t))
            .ToList();

        using var analyzer = new ConstructorWeightAnalyzer(CoreAssembly);
        var violations = analyzer.AnalyzeConstructors(typesToAnalyze);

        if (violations.Count != 0)
        {
            var groupedByType = violations
                .GroupBy(v => v.DeclaringType.FullName)
                .Select(g => $"{g.Key}:\n" + string.Join("\n", g.Select(v =>
                    $"  - {v.Category}: calls {v.MethodCalled}\n    ({v.Description})")))
                .ToList();

            var message = "Constructors performing heavy operations detected.\n" +
                         "Use Init() methods or static Create() factories instead.\n" +
                         "Or add [AllowHeavyConstructor] attribute if intentional.\n\n" +
                         string.Join("\n\n", groupedByType);

            Assert.Fail(message);
        }
    }

    [Fact]
    public void Modules_Should_Not_Reference_Forbidden_Namespaces()
    {
        var errors = new List<string>();

        void CheckModule(string moduleNamespace, string[] forbidden)
        {
            foreach (var type in AllTypes.Where(t => t.Namespace?.StartsWith(moduleNamespace) == true))
            {
                var referenced = GetReferencedNamespaces(type);
                foreach (var ns in referenced)
                {
                    if (forbidden.Any(f => ns.Contains(f, StringComparison.Ordinal)))
                    {
                        errors.Add($"{type.FullName} references forbidden namespace: {ns}");
                    }
                }
            }
        }

        CheckModule($"{RootNamespace}.PiecePicking", PiecePickingForbiddenNamespaces);
        CheckModule($"{RootNamespace}.PieceWriter", PieceWriterForbiddenNamespaces);

        if (errors.Count != 0)
        {
            Assert.Fail("Module boundary violations found:" +
                        Environment.NewLine +
                        string.Join(Environment.NewLine, errors));
        }
    }

    private static HashSet<string> GetReferencedNamespaces(Type type)
    {
        var namespaces = new HashSet<string>();

        void AddNamespace(Type? t)
        {
            if (t == null)
            {
                return;
            }

            if (t.IsGenericType)
            {
                foreach (var arg in t.GetGenericArguments())
                {
                    AddNamespace(arg);
                }
            }

            if (!string.IsNullOrEmpty(t.Namespace))
            {
                namespaces.Add(t.Namespace);
            }
        }

        AddNamespace(type.BaseType);

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            AddNamespace(field.FieldType);
        }

        foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            AddNamespace(prop.PropertyType);
        }

        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            AddNamespace(method.ReturnType);
            foreach (var param in method.GetParameters())
            {
                AddNamespace(param.ParameterType);
            }
        }

        return namespaces;
    }

    private static bool IsDtoOrRecord(Type type)
    {
        if (IsRecord(type))
        {
            return true;
        }

        var ns = type.Namespace ?? "";
        if (ns.EndsWith(".Api") || ns.Contains(".Api."))
        {
            return true;
        }

        var name = type.Name;
        return name.EndsWith("Options") ||
               name.EndsWith("Status") ||
               name.EndsWith("Info") ||
               name.EndsWith("Data") ||
               name.EndsWith("Entry") ||
               name.EndsWith("Events");
    }

    private static bool IsRecord(Type type)
    {
        return type.GetMethods().Any(m => m.Name == "<Clone>$") ||
               type.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic) != null;
    }
}






