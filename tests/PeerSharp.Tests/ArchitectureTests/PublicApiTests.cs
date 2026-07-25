using System.Reflection;
using System.Text;

namespace PeerSharp.Tests.ArchitectureTests;

/// <summary>
/// Snapshots the public API surface so that changing it is a deliberate, reviewable act.
///
/// PeerSharp is a v2.x package on NuGet, which means every public signature is a promise. Without
/// a snapshot, removing an overload or narrowing a return type looks like an ordinary refactor in
/// review and only surfaces later as a consumer's build break. With one, the same change shows up
/// as a diff in <c>PublicApi.approved.txt</c> that a reviewer has to sign off on.
///
/// To accept an intended change, run this test and copy the generated
/// <c>PublicApi.received.txt</c> over the approved file. The received file is written next to the
/// approved one only when they differ.
/// </summary>
public class PublicApiTests
{
    private const string ApprovedFileName = "PublicApi.approved.txt";
    private const string ReceivedFileName = "PublicApi.received.txt";

    /// <summary>
    /// Fails when the public surface of PeerSharp differs from the approved snapshot.
    /// </summary>
    [Fact]
    public void Public_Api_Matches_Approved_Snapshot()
    {
        string actual = ApiSurface.Describe(ArchitectureHelper.CoreAssembly);

        string approvedPath = Path.Combine(SnapshotDirectory(), ApprovedFileName);
        string receivedPath = Path.Combine(SnapshotDirectory(), ReceivedFileName);

        if (!File.Exists(approvedPath))
        {
            File.WriteAllText(receivedPath, actual);
            Assert.Fail(
                $"No approved API snapshot found. A candidate was written to {receivedPath}; " +
                $"review it and rename it to {ApprovedFileName} to accept it as the baseline.");
        }

        // Normalise line endings: the file is committed and will differ between platforms.
        string approved = File.ReadAllText(approvedPath).ReplaceLineEndings("\n").TrimEnd();
        string normalised = actual.ReplaceLineEndings("\n").TrimEnd();

        if (approved == normalised)
        {
            if (File.Exists(receivedPath))
            {
                File.Delete(receivedPath);
            }
            return;
        }

        File.WriteAllText(receivedPath, actual);
        Assert.Fail(
            $"The public API surface changed.{Environment.NewLine}{Environment.NewLine}" +
            $"{Describe(approved, normalised)}{Environment.NewLine}" +
            $"If the change is intended, copy {receivedPath} over {approvedPath}.");
    }

    private static string Describe(string approved, string actual)
    {
        var approvedLines = approved.Split('\n');
        var actualLines = actual.Split('\n');

        var removed = approvedLines.Except(actualLines).ToArray();
        var added = actualLines.Except(approvedLines).ToArray();

        var builder = new StringBuilder();
        if (removed.Length != 0)
        {
            builder.AppendLine($"Removed or changed ({removed.Length}) - these are breaking:");
            foreach (var line in removed.Take(40))
            {
                builder.AppendLine($"  - {line}");
            }
            if (removed.Length > 40)
            {
                builder.AppendLine($"  ... and {removed.Length - 40} more");
            }
        }

        if (added.Length != 0)
        {
            builder.AppendLine($"Added ({added.Length}):");
            foreach (var line in added.Take(40))
            {
                builder.AppendLine($"  + {line}");
            }
            if (added.Length > 40)
            {
                builder.AppendLine($"  ... and {added.Length - 40} more");
            }
        }

        return builder.ToString();
    }

    private static string SnapshotDirectory()
    {
        // The snapshot lives next to the test source, not in the output directory, so it is
        // committed and shows up in review.
        var current = AppDomain.CurrentDomain.BaseDirectory;
        var search = current;
        while (!string.IsNullOrEmpty(search))
        {
            var candidate = Path.Combine(search, "tests", "PeerSharp.Tests", "ArchitectureTests");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            search = Path.GetDirectoryName(search);
        }

        return current;
    }

    /// <summary>
    /// Renders an assembly's public surface as stable, sorted text.
    /// </summary>
    private static class ApiSurface
    {
        public static string Describe(Assembly assembly)
        {
            var builder = new StringBuilder();

            var types = assembly.GetExportedTypes()
                .OrderBy(static t => t.FullName, StringComparer.Ordinal);

            foreach (var type in types)
            {
                builder.AppendLine(DescribeType(type));

                foreach (var member in DescribeMembers(type).OrderBy(static m => m, StringComparer.Ordinal))
                {
                    builder.AppendLine($"    {member}");
                }
            }

            return builder.ToString();
        }

        private static string DescribeType(Type type)
        {
            var kind = type.IsEnum ? "enum"
                : type.IsInterface ? "interface"
                : type.IsValueType ? "struct"
                : "class";

            var modifiers = new List<string>();
            if (type.IsAbstract && type.IsSealed)
            {
                modifiers.Add("static");
            }
            else
            {
                if (type.IsAbstract && !type.IsInterface)
                {
                    modifiers.Add("abstract");
                }
                if (type.IsSealed && !type.IsValueType)
                {
                    modifiers.Add("sealed");
                }
            }

            var prefix = modifiers.Count != 0 ? string.Join(' ', modifiers) + " " : string.Empty;
            return $"{prefix}{kind} {Name(type)}";
        }

        private static IEnumerable<string> DescribeMembers(Type type)
        {
            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var field in type.GetFields(flags))
            {
                if (type.IsEnum)
                {
                    // Skip the synthetic value__ backing field, which has no constant value.
                    if (field.IsLiteral)
                    {
                        yield return $"{field.Name} = {Convert.ToInt64(field.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture)}";
                    }
                    continue;
                }

                yield return $"{Name(field.FieldType)} {field.Name}";
            }

            foreach (var property in type.GetProperties(flags))
            {
                var accessors = new List<string>();
                if (property.GetMethod?.IsPublic == true)
                {
                    accessors.Add("get");
                }
                if (property.SetMethod?.IsPublic == true)
                {
                    accessors.Add(IsInitOnly(property) ? "init" : "set");
                }

                yield return $"{Name(property.PropertyType)} {property.Name} {{ {string.Join("; ", accessors)}; }}";
            }

            foreach (var constructor in type.GetConstructors(flags))
            {
                yield return $".ctor({Parameters(constructor)})";
            }

            foreach (var method in type.GetMethods(flags))
            {
                // Property and event accessors are already covered above.
                if (method.IsSpecialName)
                {
                    continue;
                }

                yield return $"{Name(method.ReturnType)} {method.Name}{Generics(method)}({Parameters(method)})";
            }

            foreach (var @event in type.GetEvents(flags))
            {
                yield return $"event {Name(@event.EventHandlerType!)} {@event.Name}";
            }
        }

        private static bool IsInitOnly(PropertyInfo property)
        {
            return property.SetMethod?.ReturnParameter
                .GetRequiredCustomModifiers()
                .Any(static m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit") == true;
        }

        private static string Parameters(MethodBase method)
        {
            return string.Join(", ", method.GetParameters().Select(static p =>
            {
                var prefix = p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : string.Empty;
                var suffix = p.IsOptional ? " = default" : string.Empty;
                return $"{prefix}{Name(p.ParameterType)} {p.Name}{suffix}";
            }));
        }

        private static string Generics(MethodInfo method)
        {
            return method.IsGenericMethodDefinition
                ? "<" + string.Join(", ", method.GetGenericArguments().Select(static a => a.Name)) + ">"
                : string.Empty;
        }

        /// <summary>
        /// Renders a type name without assembly qualification, so the snapshot stays readable and
        /// does not churn on version bumps.
        /// </summary>
        private static string Name(Type type)
        {
            if (type.IsByRef)
            {
                return Name(type.GetElementType()!);
            }

            if (type.IsArray)
            {
                return Name(type.GetElementType()!) + "[]";
            }

            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition().FullName ?? type.Name;
                var name = definition.Contains('`', StringComparison.Ordinal)
                    ? definition[..definition.IndexOf('`', StringComparison.Ordinal)]
                    : definition;
                var args = string.Join(", ", type.GetGenericArguments().Select(Name));
                return $"{Simplify(name)}<{args}>";
            }

            return Simplify(type.FullName ?? type.Name);
        }

        private static string Simplify(string fullName)
        {
            return fullName
                .Replace("System.Threading.Tasks.", string.Empty, StringComparison.Ordinal)
                .Replace("System.Collections.Generic.", string.Empty, StringComparison.Ordinal)
                .Replace("System.Threading.", string.Empty, StringComparison.Ordinal)
                .Replace("System.", string.Empty, StringComparison.Ordinal);
        }
    }
}
