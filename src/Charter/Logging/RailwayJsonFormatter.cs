using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog.Events;
using Serilog.Formatting;

namespace Charter.Logging;

/// <summary>
/// Writes one JSON object per line in the shape Railway's structured log parser recognises.
/// </summary>
/// <remarks>
/// Railway renders structured fields as filterable attributes only when a log line carries a
/// <c>message</c> string and a <c>level</c> string it recognises, with the remaining properties
/// flattened alongside rather than nested under an envelope (section 19.1).
/// </remarks>
public sealed class RailwayJsonFormatter : ITextFormatter
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = true,

        // Log lines are never interpolated into HTML. The default encoder escapes every quote in a
        // rendered message to a numeric entity, which makes Railway's log view hard to read.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Property names Railway owns; a Serilog property of the same name is prefixed.</summary>
    private static readonly HashSet<string> ReservedNames =
        new(StringComparer.OrdinalIgnoreCase) { "level", "message", "timestamp", "traceId", "spanId", "exception" };

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();

            writer.WriteString("level", ToRailwayLevel(logEvent.Level));
            writer.WriteString("message", logEvent.RenderMessage(CultureInfo.InvariantCulture));
            writer.WriteString("timestamp", logEvent.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

            if (logEvent.TraceId is { } traceId)
            {
                writer.WriteString("traceId", traceId.ToString());
            }

            if (logEvent.SpanId is { } spanId)
            {
                writer.WriteString("spanId", spanId.ToString());
            }

            if (logEvent.Exception is not null)
            {
                writer.WriteString("exception", logEvent.Exception.ToString());
            }

            foreach (var property in logEvent.Properties)
            {
                var name = ReservedNames.Contains(property.Key) ? "property." + property.Key : property.Key;
                writer.WritePropertyName(name);
                WriteValue(writer, property.Value);
            }

            writer.WriteEndObject();
        }

        output.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>Maps Serilog levels onto the level strings Railway colours and filters on.</summary>
    private static string ToRailwayLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "trace",
        LogEventLevel.Debug => "debug",
        LogEventLevel.Information => "info",
        LogEventLevel.Warning => "warn",
        LogEventLevel.Error => "error",
        LogEventLevel.Fatal => "fatal",
        _ => "info",
    };

    private static void WriteValue(Utf8JsonWriter writer, LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue scalar:
                WriteScalar(writer, scalar.Value);
                break;

            case SequenceValue sequence:
                writer.WriteStartArray();
                foreach (var element in sequence.Elements)
                {
                    WriteValue(writer, element);
                }

                writer.WriteEndArray();
                break;

            case StructureValue structure:
                writer.WriteStartObject();
                if (structure.TypeTag is not null)
                {
                    writer.WriteString("$type", structure.TypeTag);
                }

                foreach (var property in structure.Properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteValue(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case DictionaryValue dictionary:
                writer.WriteStartObject();
                foreach (var entry in dictionary.Elements)
                {
                    writer.WritePropertyName(
                        entry.Key.Value?.ToString() ?? "null");
                    WriteValue(writer, entry.Value);
                }

                writer.WriteEndObject();
                break;

            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case byte or sbyte or short or ushort or int:
                writer.WriteNumberValue(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                break;
            case uint or long:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong number:
                writer.WriteNumberValue(number);
                break;
            case float or double:
                writer.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case DateTime timestamp:
                writer.WriteStringValue(timestamp.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset timestamp:
                writer.WriteStringValue(timestamp.ToString("O", CultureInfo.InvariantCulture));
                break;
            case Guid id:
                writer.WriteStringValue(id);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
