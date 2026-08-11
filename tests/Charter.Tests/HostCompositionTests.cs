using System.Reflection;
using System.Reflection.Emit;
using Charter.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Charter.Tests;

/// <summary>
/// Walks the IL of <see cref="CharterHost.ConfigureServices"/> and
/// <see cref="CharterHost.ConfigurePipeline"/> to find every Charter extension method the host
/// actually calls.
/// </summary>
/// <remarks>
/// <para>
/// A hand-maintained list of "what the host composes" is exactly the artifact that goes stale, so
/// this reads the compiled call graph instead. Starting from the two composition roots it follows
/// <c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c> and <c>ldvirtftn</c> through every method
/// declared in the Charter assembly, which is what makes indirect composition count:
/// <c>AddCharterVersionControl</c> is composed because <c>AddCharterGitHub</c> calls it, and
/// <c>AddCharterAgentPlane</c> because <c>AddCharterRunners</c> does.
/// </para>
/// <para>
/// Following <c>ldftn</c> matters as much as following <c>call</c>: most of Charter's registrations
/// are factory lambdas, and a lambda is a separate method the parent only references.
/// </para>
/// </remarks>
internal static class HostCallGraph
{
    private static readonly Dictionary<short, OpCode> Opcodes = BuildOpcodeTable();

    /// <summary>Every method in the Charter assembly reachable from <paramref name="roots"/>.</summary>
    public static IReadOnlySet<MethodBase> Reachable(params MethodBase[] roots)
    {
        var assembly = typeof(CharterHost).Assembly;
        var visited = new HashSet<MethodBase>(roots);
        var pending = new Queue<MethodBase>(roots);

        while (pending.Count > 0)
        {
            foreach (var callee in Callees(pending.Dequeue()))
            {
                // Only Charter's own methods are expanded. Framework calls are recorded nowhere:
                // nothing named AddCharter* lives outside this assembly, and walking the BCL would
                // turn a call graph into the whole runtime.
                if (callee.Module.Assembly != assembly || !visited.Add(callee))
                {
                    continue;
                }

                pending.Enqueue(callee);
            }
        }

        return visited;
    }

    /// <summary>
    /// Every public static extension method in the Charter assembly whose name starts with one of
    /// <paramref name="prefixes"/>, grouped by name.
    /// </summary>
    /// <remarks>
    /// Grouped by name rather than kept as individual overloads on purpose: the question a reviewer
    /// asks is "does the host compose deployments?", not "does it call the two-argument overload".
    /// </remarks>
    public static IReadOnlyDictionary<string, MethodInfo[]> Extensions(params string[] prefixes)
        => typeof(CharterHost).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: true, IsSealed: true, IsPublic: true })
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(method => method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false))
            .Where(method => prefixes.Any(prefix => method.Name.StartsWith(prefix, StringComparison.Ordinal)))
            .GroupBy(method => method.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

    /// <summary>
    /// Every method <paramref name="method"/> calls, constructs, or takes a function pointer to.
    /// </summary>
    /// <remarks>
    /// Exposed rather than private because <c>ConfigReachabilityTests</c> asks a different question
    /// of the same IL: not "does the host call this registration" but "does anything at all read
    /// this configuration value". Both are the same defect seen from two directions - something that
    /// exists and is never invoked - and both want one IL walker rather than two.
    /// </remarks>
    public static IEnumerable<MethodBase> Callees(MethodBase method)
    {
        byte[] il;

        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // Abstract, extern, or otherwise bodiless. Nothing to follow.
            yield break;
        }

        var declaringGenerics = method.DeclaringType?.IsGenericType == true
            ? method.DeclaringType.GetGenericArguments()
            : null;
        var methodGenerics = method.IsGenericMethodDefinition ? method.GetGenericArguments() : null;

        var offset = 0;
        while (offset < il.Length)
        {
            var code = il[offset++];
            var value = (short)code;

            if (code == 0xFE && offset < il.Length)
            {
                value = (short)(0xFE00 | il[offset++]);
            }

            if (!Opcodes.TryGetValue(value, out var opcode))
            {
                // An opcode this runtime does not name means the walk has lost alignment; stopping is
                // the honest response, because every offset after it would be noise.
                yield break;
            }

            var operand = OperandSize(opcode, il, offset);
            if (operand < 0 || offset + operand > il.Length)
            {
                yield break;
            }

            if (IsCallLike(opcode) && operand == 4)
            {
                var token = BitConverter.ToInt32(il, offset);
                MethodBase? callee = null;

                try
                {
                    callee = method.Module.ResolveMethod(token, declaringGenerics, methodGenerics);
                }
                catch (Exception exception) when (exception is ArgumentException or BadImageFormatException)
                {
                    // A token this module cannot resolve in isolation - a generic instantiation over
                    // a type argument, for instance. Never an AddCharter* call.
                }

                if (callee is not null)
                {
                    yield return callee;
                }
            }

            offset += operand;
        }
    }

    private static bool IsCallLike(OpCode opcode)
        => opcode == OpCodes.Call
           || opcode == OpCodes.Callvirt
           || opcode == OpCodes.Newobj
           || opcode == OpCodes.Ldftn
           || opcode == OpCodes.Ldvirtftn;

    private static int OperandSize(OpCode opcode, byte[] il, int offset) => opcode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
            or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
            or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => offset + 4 > il.Length
            ? -1
            : 4 + (4 * BitConverter.ToInt32(il, offset)),
        _ => -1,
    };

    private static Dictionary<short, OpCode> BuildOpcodeTable()
    {
        var table = new Dictionary<short, OpCode>();

        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opcode)
            {
                table[opcode.Value] = opcode;
            }
        }

        return table;
    }
}

/// <summary>
/// That the host composes everything Charter ships, and that anything it leaves out is a decision on
/// the record.
/// </summary>
/// <remarks>
/// This is the suite for the defect where <c>AddCharterDeployments()</c> was never called in
/// <c>Program.cs</c>. The entire preview subsystem was unregistered in the running app and no test
/// noticed, because every test that needed it registered it itself. Nothing here asserts that a
/// service resolves — that is <c>BootMatrixTests</c>. What it asserts is that the <em>call</em>
/// exists.
/// </remarks>
public class HostCompositionTests
{
    private static readonly Lazy<IReadOnlySet<MethodBase>> ComposedByHost = new(() => HostCallGraph.Reachable(
        typeof(CharterHost).GetMethod(nameof(CharterHost.ConfigureServices))!,
        typeof(CharterHost).GetMethod(nameof(CharterHost.ConfigurePipeline))!));

    [Fact]
    public void EveryAddCharterExtensionIsComposedByTheHostOrIsExcludedOnTheRecord()
    {
        var declared = HostCallGraph.Extensions("AddCharter");
        Assert.NotEmpty(declared);

        var missing = declared
            .Where(entry => !entry.Value.Any(ComposedByHost.Value.Contains))
            .Select(entry => entry.Key)
            .Where(name => !CharterComposition.DeliberatelyNotComposed.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "CharterHost never calls these registration methods, so the subsystems behind them are absent "
            + "from the running application: "
            + string.Join(", ", missing)
            + ". Call them from CharterHost.ConfigureServices, or add each to "
            + "CharterComposition.DeliberatelyNotComposed with the reason the host is right to leave it out.");
    }

    [Fact]
    public void EveryMapAndUseCharterExtensionIsReachedByThePipeline()
    {
        var declared = HostCallGraph.Extensions("MapCharter", "UseCharter");
        Assert.NotEmpty(declared);

        var missing = declared
            .Where(entry => !entry.Value.Any(ComposedByHost.Value.Contains))
            .Select(entry => entry.Key)
            .Where(name => !CharterComposition.DeliberatelyNotComposed.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "CharterHost.ConfigurePipeline never calls these, so the routes and middleware behind them are "
            + "not in the running application: "
            + string.Join(", ", missing)
            + ". Map them, or record the omission in CharterComposition.DeliberatelyNotComposed.");
    }

    [Fact]
    public void TheExclusionListCarriesNoStaleEntries()
    {
        var declared = HostCallGraph.Extensions("AddCharter", "MapCharter", "UseCharter");

        foreach (var (name, reason) in CharterComposition.DeliberatelyNotComposed)
        {
            Assert.True(
                declared.ContainsKey(name),
                $"CharterComposition.DeliberatelyNotComposed names '{name}', which no longer exists. "
                + "Delete the entry.");

            Assert.False(
                declared[name].Any(ComposedByHost.Value.Contains),
                $"CharterComposition.DeliberatelyNotComposed says the host does not call '{name}', but it "
                + "does. Delete the entry: an exclusion that is not true is worse than none, because the "
                + "next person to remove the call gets a green build.");

            // An entry without a reason is an entry nobody reviewed.
            Assert.True(reason.Length > 40, $"The exclusion for '{name}' does not explain itself.");
        }
    }

    [Fact]
    public void TheIndirectlyComposedSetDescribesWhatTheCallGraphActuallyShows()
    {
        var declared = HostCallGraph.Extensions("AddCharter", "MapCharter", "UseCharter");

        foreach (var name in CharterComposition.ComposedIndirectly)
        {
            Assert.True(declared.ContainsKey(name), $"'{name}' no longer exists.");
            Assert.True(
                declared[name].Any(ComposedByHost.Value.Contains),
                $"'{name}' is documented as composed indirectly, but nothing the host calls reaches it.");
        }
    }

    [Fact]
    public void TheCallGraphWalkerFindsACallItShould()
    {
        // The walker is the load-bearing part of this file: if it silently found nothing, every test
        // above would pass forever. Pin it against three calls whose presence is not in question -
        // one direct, one reached only through another extension, and one in the pipeline.
        var direct = HostCallGraph.Extensions("AddCharter")["AddCharterData"];
        var indirect = HostCallGraph.Extensions("AddCharter")["AddCharterVersionControl"];
        var pipeline = HostCallGraph.Extensions("UseCharter")["UseCharterSetupMode"];

        Assert.Contains(direct[0], ComposedByHost.Value);
        Assert.Contains(indirect[0], ComposedByHost.Value);
        Assert.Contains(pipeline[0], ComposedByHost.Value);

        // And that it is not simply returning everything: a method nothing composes must be absent.
        var uncomposed = typeof(HostCompositionTests).GetMethod(
            nameof(TheCallGraphWalkerFindsACallItShould),
            BindingFlags.Public | BindingFlags.Instance)!;

        Assert.DoesNotContain(uncomposed, ComposedByHost.Value);
    }
}
