using Charter.Api.Changes;
using Charter.Auth.Authorization;
using Charter.Domain;

namespace Charter.Tests;

/// <summary>
/// Section 16.3 at pane 3: the path a file is read by is never one the execution plane chose.
/// </summary>
/// <remarks>
/// <para>
/// The old normalisation swapped <c>\</c> for <c>/</c> and trimmed a leading <c>/</c>, and nothing removed
/// a <c>..</c> segment. Every layer under it preserved one:
/// <see cref="Uri.EscapeDataString"/> leaves <c>..</c> alone because <c>.</c> is unreserved, and the request
/// URL is built with <c>new Uri(apiBase, path)</c>, which applies RFC 3986's <em>remove_dot_segments</em>.
/// Three <c>..</c> from <c>repos/{owner}/{name}/contents/</c> is a request to a different path on the API
/// host, with this session's installation token on it.
/// </para>
/// <para>
/// The allowlist was no defence, because it is built from the plane's own <c>file_write</c> paths through
/// the same normalisation — a poisoned transcript matched itself.
/// </para>
/// </remarks>
public class FileDiffPathTests
{
    public static TheoryData<string> Refused =>
    [
        "../../../../victim/secrets/contents/.env",
        "src/../../../../victim/secrets/contents/.env",
        "src/App.tsx/../../../..",
        "/etc/passwd",
        "/src/App.tsx",

        // The reachable spelling. Kestrel collapses `..` and `%2e%2e` segments in a request target before
        // routing, but `%5C` decodes to a backslash and is not a separator, so this arrives at the endpoint
        // intact — and the old `Replace('\\', '/')` turned it straight back into a climb.
        @"src\..\..\..\..\victim/secrets/contents/.env",
        @"..\..\..\victim",
        @"src\App.tsx",

        // Percent sequences, drive letters and UNC paths: none of them name a file in this repository,
        // whatever anything downstream would make of them.
        "src/%2e%2e/%2e%2e/victim",
        "C:/Windows/System32/config",
        "//evil.test/share/file",
        "src//App.tsx",
        "src/./App.tsx",
        "src/App.tsx/",
        "https://evil.test/file",
    ];

    public static TheoryData<string> Allowed =>
    [
        "src/App.tsx",
        ".charter/config.yml",
        "src/Quotes/QuoteWizard.cs",
        "docs/a file with spaces.md",
        "src/i18n/ja/メッセージ.json",
        "a..b/c",
        "...hidden",
    ];

    [Theory]
    [MemberData(nameof(Refused))]
    public void APathThatIsNotInsideTheRepositoryIsRefusedWhole(string path)
        => Assert.Empty(RepositoryPath.Normalise(path));

    [Theory]
    [MemberData(nameof(Allowed))]
    public void AnOrdinaryPathIsUnchanged(string path)
        => Assert.Equal(path, RepositoryPath.Normalise(path));

    [Fact]
    public void ThereIsACeilingOnLength()
    {
        Assert.Empty(RepositoryPath.Normalise(new string('a', RepositoryPath.MaxLength + 1)));
        Assert.Empty(RepositoryPath.Normalise(null));
        Assert.Empty(RepositoryPath.Normalise("   "));
        Assert.Empty(RepositoryPath.Normalise("src/App\u0000.tsx"));
    }

    [Fact]
    public async Task AClimbingPathInTheTranscriptIsNeverRead()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        // The whole chain, as it would actually happen: repository content tells the agent to write to a
        // path that climbs, the transcript records it, and the client renders it in the changed-file list
        // for an engineer to click.
        const string Climb = @"src\..\..\..\..\victim/secrets/contents/.env";

        await world.RecordFileWriteAsync(Climb);

        var files = new RecordingFileText();
        var read = await world.FileDiffs(files).ReadAsync(
            MemberSnapshot.From(world.Member),
            await world.RequestIdAsync(),
            Climb,
            TestContext.Current.CancellationToken);

        // Asserted first, because this is the harm: a path handed to the reader is a request built with
        // `new Uri(apiBase, "repos/{owner}/{name}/contents/" + path)`, and this one resolves elsewhere.
        Assert.Empty(files.Paths);

        // Indistinguishable from "no such file", which is what section 7.3 already answers for a path
        // outside the change.
        Assert.Equal(FileDiffReadStatus.NotFound, read.Status);
    }

    [Fact]
    public async Task AnOrdinaryPathInTheTranscriptStillReads()
    {
        await using var world = await ChangeRequestWorld.CreateAsync();
        if (world is null)
        {
            return;
        }

        await world.RecordFileWriteAsync("src/Widget.cs");

        var files = new RecordingFileText();
        var read = await world.FileDiffs(files).ReadAsync(
            MemberSnapshot.From(world.Member),
            await world.RequestIdAsync(),
            "src/Widget.cs",
            TestContext.Current.CancellationToken);

        Assert.Equal(FileDiffReadStatus.Ok, read.Status);
        Assert.Equal("src/Widget.cs", read.Diff!.Path);

        // Both sides of the change, at the two revisions, and nothing else.
        Assert.Equal(["src/Widget.cs", "src/Widget.cs"], files.Paths);
    }
}

/// <summary>A repository reader that answers the same file every time and remembers what it was asked.</summary>
internal sealed class RecordingFileText : IRepositoryFileText
{
    public List<string> Paths { get; } = [];

    public Task<FileText?> ReadAsync(
        Repo repo,
        string revision,
        string path,
        CancellationToken cancellationToken = default)
    {
        Paths.Add(path);

        return Task.FromResult<FileText?>(new FileText("one\ntwo\n"));
    }
}
