using System.Reflection;
using Charter.Configuration;
using Charter.Hosting;

namespace Charter.Tests;

/// <summary>
/// Every value section 4.2 accepts is read by something.
/// </summary>
/// <remarks>
/// <para>
/// The regression net for a defect class this repository has shipped repeatedly: a variable that
/// parses, validates, is reported in an error message when it is malformed - and reaches nothing.
/// That is worse than rejecting it, because the operator sets it, sees no complaint, and believes it
/// took effect. <c>CHARTER_MODEL_REFINE</c> was one; <c>CHARTER_DEMO</c> was another; the section
/// 30.1 preflight checks themselves were a third.
/// </para>
/// <para>
/// The question asked here is deliberately weak and therefore reliable: <em>does any method in the
/// Charter assembly, outside the section 4.2 records themselves, call this property's getter?</em> It
/// cannot prove a value is honoured correctly - only <c>HostModelSelectionTests</c> and the
/// subsystem suites can do that - but it does prove the value is not inert, and it fails the moment
/// somebody adds a variable to the parser and stops there.
/// </para>
/// <para>
/// A record reading its own property does not count on its own: a generated <c>PrintMembers</c>
/// reads every property there is, so counting it would make the whole table look alive, which is
/// exactly the illusion this test exists to strip away. A derived helper does count, but only once
/// something outside the records has called it - which is how <c>OutboundCallsAllowed</c> carries
/// <c>CHARTER_DEMO</c> and <c>ToStartupOptions</c> carries the logging block.
/// </para>
/// </remarks>
public class ConfigReachabilityTests
{
    /// <summary>
    /// Configuration that is parsed, validated, and read by nothing - with the reason on the record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every entry here is a promise section 4.2 makes and the code does not keep. They are listed
    /// rather than deleted because the variables are documented, operators set them, and an
    /// undocumented gap is how they stayed invisible; naming them is what turns "nobody noticed"
    /// into "somebody decided". Emptying this dictionary is the goal.
    /// </para>
    /// <para>
    /// A stale entry - one that has since found a consumer - does not fail this test, deliberately.
    /// Several people work in this repository at once and a passing fix should never break somebody
    /// else's build; the assertion that matters is the other direction.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> AcceptedAndIgnored = new(StringComparer.Ordinal);

    [Fact]
    public void EverySection42ValueIsReadBySomething()
    {
        var undocumented = Unconsumed()
            .Where(name => !AcceptedAndIgnored.ContainsKey(name))
            .ToArray();

        Assert.True(
            undocumented.Length == 0,
            "These configuration values are parsed and validated, and nothing in the running "
            + "application reads them. Section 4.1 is fail fast and loud; accepting a variable and "
            + "silently ignoring it is the opposite, because the operator believes it took effect. "
            + "Wire each one to its consumer, or record why it has none in "
            + $"{nameof(AcceptedAndIgnored)}:{Environment.NewLine}  "
            + string.Join(Environment.NewLine + "  ", undocumented));
    }

    [Fact]
    public void TheValuesThisChangeWiredUpAreNoLongerInert()
    {
        // Named individually rather than left to the sweep above, because the sweep would go on
        // passing if a later refactor quietly reverted one of them into the documented-dead list.
        var unconsumed = Unconsumed().ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("ModelConfig.Refine", unconsumed);
        Assert.DoesNotContain("ModelConfig.Teach", unconsumed);
        Assert.DoesNotContain("ModelConfig.Build", unconsumed);
        Assert.DoesNotContain("CharterConfig.DemoMode", unconsumed);
        Assert.DoesNotContain("ModelConfig.AnthropicApiKey", unconsumed);
        Assert.DoesNotContain("ModelConfig.OpenRouterApiKey", unconsumed);

        // The six this change wired up, each of which had its own entry above until it did not.
        Assert.DoesNotContain("BudgetConfig.DefaultSessionUsd", unconsumed);
        Assert.DoesNotContain("BudgetConfig.DefaultMonthlyUsd", unconsumed);
        Assert.DoesNotContain("LoggingConfig.IncludeTranscripts", unconsumed);
        Assert.DoesNotContain("ModelConfig.AllowSharedPool", unconsumed);
        Assert.DoesNotContain("CharterConfig.AllowRepoCreation", unconsumed);
        Assert.DoesNotContain("DatabaseConfig.Username", unconsumed);
        Assert.DoesNotContain("GitHubConfig.PrivateKeyWasBase64", unconsumed);

        // Section 28's release check and section 4.2's object storage, which between them were the
        // last eight. CHARTER_STORAGE_BACKEND and CHARTER_STORAGE_PATH select the backend that reads
        // these six, and they live outside CharterConfig, so the sweep below does not cover them.
        Assert.DoesNotContain("UpdateCheckConfig.Enabled", unconsumed);
        Assert.DoesNotContain("UpdateCheckConfig.Channel", unconsumed);
        Assert.DoesNotContain("StorageConfig.Endpoint", unconsumed);
        Assert.DoesNotContain("StorageConfig.Bucket", unconsumed);
        Assert.DoesNotContain("StorageConfig.AccessKey", unconsumed);
        Assert.DoesNotContain("StorageConfig.SecretKey", unconsumed);
        Assert.DoesNotContain("StorageConfig.Region", unconsumed);
        Assert.DoesNotContain("StorageConfig.ForcePathStyle", unconsumed);
    }

    [Fact]
    public void TheAcceptedAndIgnoredListCarriesNoStaleEntries()
    {
        // Without this, the list only ever grows: an entry describing a gap somebody has since
        // closed goes on asserting the value is dead, and the next reader believes it. Every entry
        // above was removed by wiring its value up, and this is what proves the list said so.
        var consumed = AcceptedAndIgnored.Keys
            .Except(Unconsumed(), StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            consumed.Length == 0,
            "These values are documented as accepted and ignored, and something reads them now. "
            + $"Delete them from {nameof(AcceptedAndIgnored)} and, if the wiring is worth pinning, "
            + $"assert them in {nameof(TheValuesThisChangeWiredUpAreNoLongerInert)}:"
            + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", consumed));
    }

    [Fact]
    public void TheDocumentedGapsAreRealPropertiesAndNotTypos()
    {
        // A misspelled entry would silently exempt nothing and hide a real gap forever.
        var known = SettableConfigurationProperties()
            .Select(Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var documented in AcceptedAndIgnored.Keys)
        {
            Assert.Contains(documented, known);
        }
    }

    /// <summary>Configuration properties whose getter nothing in the application calls.</summary>
    private static IEnumerable<string> Unconsumed()
    {
        var consumed = ConsumedGetters();

        return SettableConfigurationProperties()
            .Where(property => property.GetMethod is not null && !consumed.Contains(property.GetMethod))
            .Select(Name)
            .Order(StringComparer.Ordinal);
    }

    /// <summary>
    /// Every method reachable from a call site outside the configuration records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two passes. The first collects everything called from any type that is not itself a section
    /// 4.2 record - that is the honest definition of "somebody reads this". The second follows calls
    /// <em>within</em> the records, but only outward from a member the first pass already reached, so
    /// that a derived helper counts: <c>AddCharterDemoMode</c> reads
    /// <c>CharterConfig.OutboundCallsAllowed</c>, which reads <c>DemoMode</c>, and the variable is
    /// genuinely consumed even though nothing outside the record names it. Without the second pass
    /// every value behind a computed property and every value projected through
    /// <c>ToStartupOptions</c> would be reported as dead.
    /// </para>
    /// <para>
    /// The expansion deliberately stops at <c>ToString</c> and <c>PrintMembers</c>. A record's
    /// generated <c>PrintMembers</c> reads every property it has, so following it would mark the
    /// whole of section 4.2 as consumed on the strength of one diagnostic dump.
    /// </para>
    /// </remarks>
    private static HashSet<MethodBase> ConsumedGetters()
    {
        var assembly = typeof(CharterHost).Assembly;
        var records = ConfigurationRecords();
        var consumed = new HashSet<MethodBase>();

        foreach (var type in assembly.GetTypes())
        {
            if (IsRecordMember(type, records))
            {
                continue;
            }

            foreach (var method in Methods(type))
            {
                foreach (var callee in HostCallGraph.Callees(method))
                {
                    consumed.Add(callee);
                }
            }
        }

        var pending = new Queue<MethodBase>(consumed.Where(method => Expandable(method, records)));

        while (pending.Count > 0)
        {
            foreach (var callee in HostCallGraph.Callees(pending.Dequeue()))
            {
                if (consumed.Add(callee) && Expandable(callee, records))
                {
                    pending.Enqueue(callee);
                }
            }
        }

        return consumed;
    }

    /// <summary>True when <paramref name="type"/> is a config record or something nested inside one.</summary>
    private static bool IsRecordMember(Type type, HashSet<Type> records)
    {
        for (var candidate = type; candidate is not null; candidate = candidate.DeclaringType)
        {
            if (records.Contains(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Expandable(MethodBase method, HashSet<Type> records)
        => method.DeclaringType is not null
           && IsRecordMember(method.DeclaringType, records)
           && method.Name is not ("ToString" or "PrintMembers");

    private static IEnumerable<MethodBase> Methods(Type type)
    {
        const BindingFlags All = BindingFlags.Public
                                 | BindingFlags.NonPublic
                                 | BindingFlags.Instance
                                 | BindingFlags.Static
                                 | BindingFlags.DeclaredOnly;

        return type.GetMethods(All)
            .Where(method => !IsSynthesized(method))
            .Concat<MethodBase>(type.GetConstructors(All));
    }

    /// <summary>
    /// Members that read a value without anything acting on it.
    /// </summary>
    /// <remarks>
    /// <c>PrintMembers</c> calls every getter on a record, so counting it would make the whole of
    /// section 4.2 look consumed. A hand-written <c>ToString</c> is excluded for the same reason: a
    /// value that only ever appears in a diagnostic dump of the object holding it is still a value
    /// nothing acts on - which is exactly what <c>CHARTER_LOG_INCLUDE_TRANSCRIPTS</c> turned out to
    /// be. Compiler-generated <em>types</em> are not excluded: an <c>async</c> method's body lives in
    /// one, and skipping those hid every consumer that happened to be asynchronous.
    /// </remarks>
    private static bool IsSynthesized(MethodInfo method)
        => method.Name is "ToString" or "PrintMembers" or "Equals" or "GetHashCode"
            or "Deconstruct" or "<Clone>$";

    /// <summary>
    /// <see cref="CharterConfig"/> and every section record hanging off it.
    /// </summary>
    /// <remarks>
    /// Discovered by walking the property graph rather than listed, so a section added to
    /// <see cref="CharterConfig"/> tomorrow is covered without anybody remembering to add it here.
    /// The <c>Config</c> suffix is the boundary: it is what every section in section 4.2 is named,
    /// and it keeps the walk out of value types like <c>ModelIdentifier</c>, whose members are not
    /// settings.
    /// </remarks>
    private static HashSet<Type> ConfigurationRecords()
    {
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>([typeof(CharterConfig)]);

        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            if (!seen.Add(type))
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var candidate = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

                if (candidate is { IsClass: true, IsAbstract: false }
                    && candidate.Namespace == "Charter.Configuration"
                    && candidate.Name.EndsWith("Config", StringComparison.Ordinal))
                {
                    pending.Enqueue(candidate);
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// The properties of those records that hold a parsed value rather than derive one.
    /// </summary>
    /// <remarks>
    /// Computed helpers such as <c>OutboundCallsAllowed</c> and <c>StorageEnabled</c> are excluded by
    /// the settable test: they carry no state of their own, so an unused one is dead code rather than
    /// an ignored setting, and including them would bury the real findings in noise. A call from one
    /// of them does not count as consumption either, because they are declared on the record itself.
    /// </remarks>
    private static IEnumerable<PropertyInfo> SettableConfigurationProperties()
        => ConfigurationRecords()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(property => property.SetMethod is not null);

    private static string Name(PropertyInfo property)
        => $"{property.DeclaringType!.Name}.{property.Name}";
}
