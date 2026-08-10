using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Charter.Adapters;

/// <summary>
/// Parses one adapter YAML file into an <see cref="AdapterDocument"/> (section 12b).
/// </summary>
/// <remarks>
/// <para>
/// Two rules from section 8 shape everything here. <c>version: 1</c> is required, and a missing or
/// unknown version is an error that names what this Charter supports. Unknown keys warn and are
/// ignored, so an old Charter keeps working against an adapter file written for a newer one.
/// </para>
/// <para>
/// The difference between the two is worth stating: an unknown <em>key</em> is forward compatibility
/// and warns; a missing <em>required</em> key, a malformed value, or a capability the declared
/// invocation cannot support is a defect in the file and fails at load, naming the file and the
/// field. Discovering either at dispatch time, with a requester waiting, is the outcome both rules
/// exist to prevent.
/// </para>
/// </remarks>
public static partial class AdapterYamlLoader
{
    /// <summary>The only adapter schema version this Charter understands.</summary>
    public const int SupportedVersion = 1;

    private static readonly string[] KnownRootKeys =
    [
        "id", "display_name", "version", "install", "invoke", "auth", "model_arg", "events", "capabilities",
    ];

    /// <summary>
    /// Parses and validates <paramref name="yaml"/>, appending any unknown-key warnings to
    /// <paramref name="warnings"/>.
    /// </summary>
    /// <exception cref="AdapterLoadException">The file is not a usable adapter.</exception>
    public static AdapterDocument Load(string sourcePath, string yaml, ICollection<AdapterWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(warnings);

        var reader = new Reader(sourcePath, warnings);
        var root = reader.ParseRoot(yaml);
        var document = reader.ReadDocument(root);
        reader.ThrowIfInvalid();
        return document!;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern { get; }

    private sealed class Reader
    {
        private readonly string _sourcePath;
        private readonly ICollection<AdapterWarning> _warnings;
        private readonly List<string> _problems = [];

        public Reader(string sourcePath, ICollection<AdapterWarning> warnings)
        {
            _sourcePath = sourcePath;
            _warnings = warnings;
        }

        public YamlMappingNode ParseRoot(string yaml)
        {
            var stream = new YamlStream();
            try
            {
                stream.Load(new StringReader(yaml));
            }
            catch (YamlException ex)
            {
                throw new AdapterLoadException($"{_sourcePath}: is not valid YAML: {ex.Message}", ex);
            }

            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                throw new AdapterLoadException(
                    $"{_sourcePath}: an adapter file must contain a single YAML mapping with at least "
                    + $"'id', 'display_name' and 'version: {SupportedVersion}'.");
            }

            return root;
        }

        public AdapterDocument ReadDocument(YamlMappingNode root)
        {
            WarnOnUnknownKeys(root, KnownRootKeys, parentField: null);

            var version = ReadVersion(root);
            var id = ReadId(root);
            var displayName = RequiredScalar(root, "display_name") ?? id;
            var install = ReadInstall(root);
            var invoke = ReadInvoke(root);
            var auth = ReadAuth(root);
            var modelArg = ReadModelArg(root);
            var events = ReadEvents(root);
            var capabilities = ReadCapabilities(root);

            ValidateCapabilitiesAgainstInvocation(capabilities, invoke, events);

            return new AdapterDocument(
                id,
                displayName,
                version,
                install,
                invoke,
                auth,
                modelArg,
                events,
                capabilities,
                _sourcePath);
        }

        public void ThrowIfInvalid()
        {
            if (_problems.Count > 0)
            {
                throw new AdapterLoadException(_problems);
            }
        }

        // -----------------------------------------------------------------------------------------
        // Fields
        // -----------------------------------------------------------------------------------------

        private int ReadVersion(YamlMappingNode root)
        {
            // Section 8. The version is the one field where being lenient would be actively harmful:
            // a file written for a schema this Charter does not know is not safe to guess at.
            if (!TryGetChild(root, "version", out var node))
            {
                Problem("version", $"is required. This Charter supports adapter schema version {SupportedVersion}.");
                return SupportedVersion;
            }

            var raw = Scalar(node);
            if (raw is null
                || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
            {
                Problem(
                    "version",
                    $"must be an integer, but is '{Describe(node)}'. This Charter supports version {SupportedVersion}.");
                return SupportedVersion;
            }

            if (version != SupportedVersion)
            {
                Problem(
                    "version",
                    $"is {version}, which this Charter does not support. Supported versions: {SupportedVersion}.");
            }

            return version;
        }

        private string ReadId(YamlMappingNode root)
        {
            var id = RequiredScalar(root, "id");
            if (id is null)
            {
                return "<unknown>";
            }

            if (!IdPattern.IsMatch(id))
            {
                Problem("id", $"'{id}' must be lowercase and hyphenated, for example 'claude-code'.");
            }

            return id;
        }

        private AdapterInstall ReadInstall(YamlMappingNode root)
        {
            if (!TryGetMap(root, "install", required: true, out var install))
            {
                return new AdapterInstall(string.Empty, string.Empty);
            }

            WarnOnUnknownKeys(install, ["check", "hint"], "install");

            var check = RequiredScalar(install, "check", "install.check");
            var hint = RequiredScalar(install, "hint", "install.hint");
            return new AdapterInstall(check ?? string.Empty, hint ?? string.Empty);
        }

        private AdapterInvoke ReadInvoke(YamlMappingNode root)
        {
            if (!TryGetMap(root, "invoke", required: true, out var invoke))
            {
                return new AdapterInvoke([], AdapterPromptDelivery.Stdin, null);
            }

            WarnOnUnknownKeys(invoke, ["command", "prompt"], "invoke");

            var command = ReadStringSequence(invoke, "command", "invoke.command", required: true);
            if (command.Count == 0)
            {
                Problem("invoke.command", "must be a non-empty argument vector, for example [\"pi\", \"--print\"].");
            }

            var prompt = RequiredScalar(invoke, "prompt", "invoke.prompt");
            if (prompt is null)
            {
                return new AdapterInvoke(command, AdapterPromptDelivery.Stdin, null);
            }

            if (string.Equals(prompt, "stdin", StringComparison.Ordinal))
            {
                return new AdapterInvoke(command, AdapterPromptDelivery.Stdin, null);
            }

            if (prompt.Contains(AdapterDocument.PromptPlaceholder, StringComparison.Ordinal))
            {
                return new AdapterInvoke(command, AdapterPromptDelivery.Argument, prompt);
            }

            Problem(
                "invoke.prompt",
                $"'{prompt}' is neither 'stdin' nor an argument template containing "
                + $"'{AdapterDocument.PromptPlaceholder}'. The spec would never reach the agent.");
            return new AdapterInvoke(command, AdapterPromptDelivery.Stdin, null);
        }

        private IReadOnlyList<AdapterAuthBinding> ReadAuth(YamlMappingNode root)
        {
            if (!TryGetMap(root, "auth", required: true, out var auth))
            {
                return [];
            }

            var bindings = new List<AdapterAuthBinding>();
            foreach (var (keyNode, valueNode) in auth.Children)
            {
                var kind = Scalar(keyNode);
                if (kind is null)
                {
                    continue;
                }

                var field = $"auth.{kind}";

                if (!AdapterCredentialKinds.IsKnown(kind))
                {
                    // Section 8: a credential kind a newer Charter knows about must not stop this one
                    // loading the file. It simply cannot be satisfied here, so it offers no provider.
                    Warn(field, $"'{kind}' is not a credential kind this Charter knows, and is ignored. "
                        + $"Known kinds: {string.Join(", ", AdapterCredentialKinds.All)}.");
                    continue;
                }

                if (valueNode is not YamlMappingNode entry)
                {
                    Problem(field, "must be a mapping with an 'env' key, for example { env: \"OPENAI_API_KEY\" }.");
                    continue;
                }

                WarnOnUnknownKeys(entry, ["env"], field);

                var env = RequiredScalar(entry, "env", $"{field}.env");
                if (env is not null)
                {
                    bindings.Add(new AdapterAuthBinding(kind, env));
                }
            }

            if (bindings.Count == 0 && auth.Children.Count == 0)
            {
                Problem("auth", "must name at least one credential kind the agent CLI can authenticate with.");
            }

            return bindings;
        }

        private IReadOnlyList<string> ReadModelArg(YamlMappingNode root)
        {
            if (!TryGetChild(root, "model_arg", out _))
            {
                return [];
            }

            var modelArg = ReadStringSequence(root, "model_arg", "model_arg", required: false);
            if (modelArg.Count > 0
                && !modelArg.Any(argument =>
                    argument.Contains(AdapterDocument.ModelPlaceholder, StringComparison.Ordinal)))
            {
                Problem(
                    "model_arg",
                    $"contains no '{AdapterDocument.ModelPlaceholder}' placeholder, so the resolved model "
                    + "would never reach the CLI. Omit 'model_arg' if the CLI takes its model from "
                    + "configuration only.");
            }

            return modelArg;
        }

        private AdapterEvents ReadEvents(YamlMappingNode root)
        {
            if (!TryGetMap(root, "events", required: true, out var events))
            {
                return new AdapterEvents(AdapterEventFormat.Text, []);
            }

            WarnOnUnknownKeys(events, ["format", "map"], "events");

            var rawFormat = RequiredScalar(events, "format", "events.format");
            var format = rawFormat switch
            {
                "jsonl" => AdapterEventFormat.Jsonl,
                "text" => AdapterEventFormat.Text,
                null => AdapterEventFormat.Text,
                _ => Unsupported(rawFormat),
            };

            AdapterEventFormat Unsupported(string value)
            {
                Problem("events.format", $"'{value}' is not a supported format. Supported formats: jsonl, text.");
                return AdapterEventFormat.Text;
            }

            var hasMap = TryGetChild(events, "map", out _);
            if (!hasMap)
            {
                if (format == AdapterEventFormat.Jsonl)
                {
                    Problem(
                        "events.map",
                        "is required when 'events.format' is jsonl. Without it Charter reads the stream "
                        + "but classifies nothing, which is strictly worse than declaring format: text.");
                }

                return new AdapterEvents(format, []);
            }

            if (format == AdapterEventFormat.Text)
            {
                Problem(
                    "events.map",
                    "cannot be used when 'events.format' is text: there are no structured lines to match. "
                    + "Either set 'events.format: jsonl' or remove the map.");
                return new AdapterEvents(format, []);
            }

            if (!TryGetMap(events, "map", required: true, out var map, "events.map"))
            {
                return new AdapterEvents(format, []);
            }

            var mappings = new List<AdapterEventMapping>();
            foreach (var (keyNode, valueNode) in map.Children)
            {
                var key = Scalar(keyNode);
                if (key is null)
                {
                    continue;
                }

                var field = $"events.map.{key}";
                if (!TryParseEventType(key, out var eventType))
                {
                    // Forward compatibility again: a newer Charter may classify more event types.
                    Warn(field, $"'{key}' is not an event type this Charter knows, and is ignored. "
                        + "Known event types: tool_use, file_write, message.");
                    continue;
                }

                var source = Scalar(valueNode);
                if (string.IsNullOrWhiteSpace(source))
                {
                    Problem(field, "must be a predicate, for example \"$.type == 'tool_call'\".");
                    continue;
                }

                try
                {
                    mappings.Add(new AdapterEventMapping(eventType, source, EventExpression.Parse(source)));
                }
                catch (EventExpressionException ex)
                {
                    Problem(field, $"is not a valid event mapping expression: {ex.Message}");
                }
            }

            if (mappings.Count == 0 && format == AdapterEventFormat.Jsonl)
            {
                Problem("events.map", "maps no event types Charter understands, so no event would ever be emitted.");
            }

            return new AdapterEvents(format, mappings);
        }

        private IReadOnlyList<AdapterCapability> ReadCapabilities(YamlMappingNode root)
        {
            if (!TryGetChild(root, "capabilities", out var node))
            {
                Problem(
                    "capabilities",
                    "is required. Declare an empty list if the adapter supports none of steering, "
                    + "resume or cost_reporting - anything absent is treated as unsupported.");
                return [];
            }

            if (node is not YamlSequenceNode sequence)
            {
                Problem("capabilities", "must be a list, for example [steering, resume, cost_reporting].");
                return [];
            }

            var capabilities = new List<AdapterCapability>();
            foreach (var item in sequence.Children)
            {
                var name = Scalar(item);
                if (name is null)
                {
                    continue;
                }

                if (!TryParseCapability(name, out var capability))
                {
                    Warn("capabilities", $"'{name}' is not a capability this Charter knows, and is ignored. "
                        + "Known capabilities: steering, resume, cost_reporting.");
                    continue;
                }

                if (capabilities.Contains(capability))
                {
                    Warn("capabilities", $"'{name}' is listed more than once.");
                    continue;
                }

                capabilities.Add(capability);
            }

            return capabilities;
        }

        private void ValidateCapabilitiesAgainstInvocation(
            IReadOnlyList<AdapterCapability> capabilities,
            AdapterInvoke invoke,
            AdapterEvents events)
        {
            // Section 12b: pretending parity is worse than documenting the degraded experience. A
            // capability the declared invocation cannot deliver is exactly that pretence, so it fails
            // here rather than at dispatch.
            if (capabilities.Contains(AdapterCapability.CostReporting)
                && events.Format != AdapterEventFormat.Jsonl)
            {
                Problem(
                    "capabilities",
                    "declares 'cost_reporting' but 'events.format' is text, so there is no machine-readable "
                    + "line for Charter to read a cost from. Remove the capability and Charter estimates "
                    + "from token counts instead.");
            }

            if (capabilities.Contains(AdapterCapability.Steering)
                && invoke.PromptDelivery != AdapterPromptDelivery.Stdin)
            {
                Problem(
                    "capabilities",
                    "declares 'steering' but 'invoke.prompt' delivers the spec as an argument, so there is "
                    + "no open channel to send further instructions down. Steering requires "
                    + "'invoke.prompt: stdin'.");
            }
        }

        // -----------------------------------------------------------------------------------------
        // YAML helpers
        // -----------------------------------------------------------------------------------------

        private static bool TryParseEventType(string key, out AdapterEventType eventType)
        {
            switch (key)
            {
                case "tool_use":
                    eventType = AdapterEventType.ToolUse;
                    return true;
                case "file_write":
                    eventType = AdapterEventType.FileWrite;
                    return true;
                case "message":
                    eventType = AdapterEventType.Message;
                    return true;
                default:
                    eventType = default;
                    return false;
            }
        }

        private static bool TryParseCapability(string name, out AdapterCapability capability)
        {
            switch (name)
            {
                case "steering":
                    capability = AdapterCapability.Steering;
                    return true;
                case "resume":
                    capability = AdapterCapability.Resume;
                    return true;
                case "cost_reporting":
                    capability = AdapterCapability.CostReporting;
                    return true;
                default:
                    capability = default;
                    return false;
            }
        }

        private static bool TryGetChild(YamlMappingNode map, string key, out YamlNode value)
        {
            foreach (var (keyNode, valueNode) in map.Children)
            {
                if (keyNode is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
                {
                    value = valueNode;
                    return true;
                }
            }

            value = null!;
            return false;
        }

        private static string? Scalar(YamlNode node)
            => node is YamlScalarNode { Value: { Length: > 0 } value } ? value : null;

        private static string Describe(YamlNode node) => node switch
        {
            YamlMappingNode => "a mapping",
            YamlSequenceNode => "a list",
            YamlScalarNode scalar => scalar.Value ?? "empty",
            _ => "empty",
        };

        private bool TryGetMap(
            YamlMappingNode parent,
            string key,
            bool required,
            out YamlMappingNode map,
            string? field = null)
        {
            if (!TryGetChild(parent, key, out var node))
            {
                if (required)
                {
                    Problem(field ?? key, "is required.");
                }

                map = null!;
                return false;
            }

            if (node is not YamlMappingNode mapping)
            {
                Problem(field ?? key, $"must be a mapping, but is {Describe(node)}.");
                map = null!;
                return false;
            }

            map = mapping;
            return true;
        }

        private string? RequiredScalar(YamlMappingNode parent, string key, string? field = null)
        {
            if (!TryGetChild(parent, key, out var node))
            {
                Problem(field ?? key, "is required.");
                return null;
            }

            var value = Scalar(node);
            if (value is null)
            {
                Problem(field ?? key, $"must be a non-empty string, but is {Describe(node)}.");
            }

            return value;
        }

        private IReadOnlyList<string> ReadStringSequence(
            YamlMappingNode parent,
            string key,
            string field,
            bool required)
        {
            if (!TryGetChild(parent, key, out var node))
            {
                if (required)
                {
                    Problem(field, "is required.");
                }

                return [];
            }

            if (node is not YamlSequenceNode sequence)
            {
                Problem(field, $"must be a list of strings, but is {Describe(node)}.");
                return [];
            }

            var values = new List<string>();
            foreach (var item in sequence.Children)
            {
                var value = Scalar(item);
                if (value is null)
                {
                    Problem(field, $"contains an entry that is not a non-empty string ({Describe(item)}).");
                    continue;
                }

                values.Add(value);
            }

            return values;
        }

        private void WarnOnUnknownKeys(YamlMappingNode map, string[] known, string? parentField)
        {
            foreach (var (keyNode, _) in map.Children)
            {
                var key = Scalar(keyNode);
                if (key is null || Array.IndexOf(known, key) >= 0)
                {
                    continue;
                }

                var field = parentField is null ? key : $"{parentField}.{key}";
                Warn(field, "is not a key this Charter understands, and is ignored.");
            }
        }

        private void Warn(string field, string message) => _warnings.Add(new AdapterWarning(_sourcePath, field, message));

        private void Problem(string field, string message) => _problems.Add($"{_sourcePath}: '{field}' {message}");
    }
}
