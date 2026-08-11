using System.Text;
using System.Text.Json.Nodes;
using Charter.Domain;
using Charter.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// Oversized transcript output leaves Postgres and leaves a reference behind (sections 5, 19, 27.1).
/// </summary>
/// <remarks>
/// <para>
/// This is the consumer that makes the storage block real. <c>events</c> is the largest table in the
/// schema by orders of magnitude, and what makes one row large is a single string inside it — an
/// adapter's <c>raw</c> payload carrying the whole file a <c>Write</c> tool call produced, a
/// <c>command</c> event carrying what the command said, a <c>check_result</c> carrying the tail of a
/// build log. Those bytes exist on every session that runs today, which is why this rather than the
/// section 27.1 artifact kinds, whose producers arrive with the project types that emit them.
/// </para>
/// <para>
/// The behaviour that has to hold: the event stays readable without a second fetch, the structure
/// nothing else reads changes, and an instance with no storage is byte-for-byte what it was before.
/// </para>
/// </remarks>
public class StorageOffloadTests
{
    private static readonly Guid Session = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public async Task WithNoStorageThePayloadIsStoredExactlyAsItArrived()
    {
        var offload = new TranscriptOffload(NullObjectStore.Instance, NullLogger<TranscriptOffload>.Instance);
        var payload = Payload(new string('x', 200_000));

        var rewritten = await offload.RewriteAsync(
            Session,
            EventTypes.CheckResult,
            payload,
            "runner:1",
            TestContext.Current.CancellationToken);

        // Section 2.3's default: an instance with an ephemeral filesystem does exactly what it did
        // before object storage existed.
        Assert.False(offload.Enabled);
        Assert.Same(payload, rewritten);
    }

    [Fact]
    public async Task AnEventThatFitsIsNotTouched()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path);
        var offload = new TranscriptOffload(store, NullLogger<TranscriptOffload>.Instance);
        var payload = Payload("Build succeeded.");

        var rewritten = await offload.RewriteAsync(
            Session,
            EventTypes.CheckResult,
            payload,
            "runner:1",
            TestContext.Current.CancellationToken);

        Assert.Same(payload, rewritten);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task AnOversizedOutputMovesToTheStoreAndKeepsItsTail()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path);
        var offload = new TranscriptOffload(store, NullLogger<TranscriptOffload>.Instance);

        var output = new string('x', 60_000) + "\nBuild FAILED. 3 errors.";
        var rewritten = await offload.RewriteAsync(
            Session,
            EventTypes.CheckResult,
            Payload(output),
            "runner:7",
            TestContext.Current.CancellationToken);

        var parsed = Assert.IsType<JsonObject>(JsonNode.Parse(rewritten));

        // What stayed: the end of the output, which is where a build tool puts the reason. Pane 2 has
        // to stay readable without a second fetch.
        var inline = parsed["output"]!.GetValue<string>();
        Assert.StartsWith(TranscriptOffload.TruncationMarker, inline, StringComparison.Ordinal);
        Assert.EndsWith("Build FAILED. 3 errors.", inline, StringComparison.Ordinal);
        Assert.True(inline.Length < output.Length);

        // What was added: where the whole thing went, how big it was, and what it hashes to.
        var reference = parsed["output_ref"]!.GetValue<string>();
        Assert.True(ObjectKey.IsWithinSession(reference, Session));
        Assert.Equal(Encoding.UTF8.GetByteCount(output), parsed["output_bytes"]!.GetValue<long>());
        Assert.Equal(64, parsed["output_sha256"]!.GetValue<string>().Length);

        // What did not change: everything anything else reads for structure.
        Assert.Equal("dotnet test", parsed["check"]!.GetValue<string>());
        Assert.False(parsed["passed"]!.GetValue<bool>());
        Assert.Equal(1, parsed["exit_code"]!.GetValue<int>());

        var stored = await store.GetAsync(reference, TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(output, Encoding.UTF8.GetString(stored.Bytes));
    }

    [Fact]
    public async Task TheCheckNameNamesTheObjectAndCannotEscapeTheSession()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path);
        var offload = new TranscriptOffload(store, NullLogger<TranscriptOffload>.Instance);

        // Section 16: the check name comes out of the repository's own .charter/config.yml, which is
        // written by whoever opened the pull request.
        var rewritten = await offload.RewriteAsync(
            Session,
            EventTypes.CheckResult,
            Payload(new string('y', 20_000), check: "../../../../etc/passwd"),
            "runner:1",
            TestContext.Current.CancellationToken);

        var reference = JsonNode.Parse(rewritten)!["output_ref"]!.GetValue<string>();

        Assert.True(ObjectKey.IsWithinSession(reference, Session));
        Assert.DoesNotContain("..", reference, StringComparison.Ordinal);

        // And nothing was written outside the root the store was opened on.
        var written = Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories).ToArray();
        Assert.Single(written);
        Assert.StartsWith(directory.Path, written[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAgentsWholeToolCallIsOffloadedFromWhereverItSits()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path);
        var offload = new TranscriptOffload(store, NullLogger<TranscriptOffload>.Instance);

        // The shape the shim actually posts: the adapter's classification plus the agent's whole JSONL
        // line under "raw". A Write tool call puts the entire file body in there.
        var body = new string('z', 100_000);
        var payload = new JsonObject
        {
            ["adapter"] = "claude-code",
            ["raw"] = new JsonObject
            {
                ["type"] = "tool_use",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["input"] = new JsonObject
                    {
                        ["file_path"] = "src/App.tsx",
                        ["content"] = body,
                    },
                }),
            },
        }.ToJsonString();

        var rewritten = await offload.RewriteAsync(
            Session,
            EventTypes.FileWrite,
            payload,
            "runner:12",
            TestContext.Current.CancellationToken);

        var input = JsonNode.Parse(rewritten)!["raw"]!["content"]![0]!["input"]!;
        var reference = input["content_ref"]!.GetValue<string>();

        Assert.True(ObjectKey.IsWithinSession(reference, Session));
        Assert.Equal("src/App.tsx", input["file_path"]!.GetValue<string>());

        var stored = await store.GetAsync(reference, TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(body, Encoding.UTF8.GetString(stored.Bytes));
    }

    [Fact]
    public async Task ARedeliveredEventOverwritesItsOwnObjectRatherThanAddingOne()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path);
        var offload = new TranscriptOffload(store, NullLogger<TranscriptOffload>.Instance);
        var payload = Payload(new string('q', 30_000));

        var first = await offload.RewriteAsync(Session, EventTypes.CheckResult, payload, "runner:3", TestContext.Current.CancellationToken);
        var second = await offload.RewriteAsync(Session, EventTypes.CheckResult, payload, "runner:3", TestContext.Current.CancellationToken);

        // The key is a pure function of the event's idempotency key, so a runner that reconnects and
        // replays its stream does not leave a second copy of the transcript in the bucket.
        Assert.Equal(first, second);
        Assert.Single(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AStringOverThePerObjectCapIsTruncatedRatherThanRefused()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path);
        var offload = new TranscriptOffload(store, NullLogger<TranscriptOffload>.Instance);

        var enormous = new string('w', (int)StorageOptions.MaxObjectBytes + 4096);
        var rewritten = await offload.RewriteAsync(
            Session,
            EventTypes.Command,
            Payload(enormous),
            "runner:4",
            TestContext.Current.CancellationToken);

        var reference = JsonNode.Parse(rewritten)!["output_ref"]!.GetValue<string>();
        var stored = await store.GetAsync(reference, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.True(stored.Bytes.Length <= StorageOptions.MaxObjectBytes);

        // The cap having bitten is recorded in the key rather than left to be inferred from a size.
        Assert.Contains("truncated", reference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoreThatRefusesLeavesTheEventWhole()
    {
        var offload = new TranscriptOffload(new FailingObjectStore(), NullLogger<TranscriptOffload>.Instance);
        var payload = Payload(new string('e', 20_000));

        var rewritten = await offload.RewriteAsync(
            Session,
            EventTypes.CheckResult,
            payload,
            "runner:5",
            TestContext.Current.CancellationToken);

        // A bucket being unreachable is an operator's problem. Losing a transcript event over it would
        // be Charter's, so the row is written whole and the failure is logged.
        Assert.Equal(payload, rewritten);
    }

    private static string Payload(string output, string check = "dotnet test") => new JsonObject
    {
        ["check"] = check,
        ["command"] = "dotnet test",
        ["passed"] = false,
        ["status"] = "failed",
        ["exit_code"] = 1,
        ["duration_ms"] = 4213,
        ["summary"] = $"The check '{check}' failed.",
        ["output"] = output,
    }.ToJsonString();

    private sealed class FailingObjectStore : IObjectStore
    {
        public StorageBackend Backend => StorageBackend.S3;

        public bool Enabled => true;

        public long MaxObjectBytes => StorageOptions.MaxObjectBytes;

        public Task<StoredObject?> PutAsync(string key, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken = default)
            => throw new ObjectStoreException("the bucket is not reachable");

        public Task<ObjectContent?> GetAsync(string key, CancellationToken cancellationToken = default)
            => throw new ObjectStoreException("the bucket is not reachable");

        public Task<int> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
            => throw new ObjectStoreException("the bucket is not reachable");
    }
}
