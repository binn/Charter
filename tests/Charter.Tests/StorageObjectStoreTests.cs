using System.Text;
using Charter.Storage;

namespace Charter.Tests;

/// <summary>
/// Bytes go in, the same bytes come out, and an untrusted key cannot leave its prefix (section 16).
/// </summary>
/// <remarks>
/// <para>
/// Everything that names an object comes from somewhere Charter does not control: a check name out of
/// a repository's <c>.charter/config.yml</c>, a path out of an agent's tool call. A check named
/// <c>../../../etc/passwd</c> is a realistic input — the repository is written by whoever opened the
/// pull request, and the agent's output is model-authored text — so the traversal cases here are
/// regression tests, not hypotheticals.
/// </para>
/// <para>
/// The filesystem backend is exercised against a real temporary directory with real bytes rather than
/// through an abstraction, because the failures worth catching are the ones only a filesystem has:
/// a path that resolves outside the root, a directory that is not there, a half-written file.
/// </para>
/// </remarks>
public class StorageObjectStoreTests
{
    private static readonly Guid Session = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task BytesRoundTripThroughARealDirectory()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path);

        var key = ObjectKey.WithExtension(ObjectKey.ForSession(Session, "transcript", "dotnet test", "output"), "txt");
        var text = new string('x', 40_000) + "\nBuild FAILED. 3 errors.\n";
        var bytes = Encoding.UTF8.GetBytes(text);

        var stored = await store.PutAsync(key, bytes, ObjectContentTypes.Text, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.Equal(key, stored.Key);
        Assert.Equal(bytes.Length, stored.SizeBytes);
        Assert.Equal(64, stored.Sha256.Length);

        var read = await store.GetAsync(key, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(text, Encoding.UTF8.GetString(read.Bytes));
        Assert.Equal(ObjectContentTypes.Text, read.ContentType);

        // And it is on the disk where the key says, under the session prefix and nowhere else.
        var expected = Path.Combine(directory.Path, key.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public async Task AMissingObjectReadsAsNothingRatherThanThrowing()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path);

        var read = await store.GetAsync(
            ObjectKey.ForSession(Session, "transcript", "never", "written"),
            TestContext.Current.CancellationToken);

        Assert.Null(read);
    }

    [Fact]
    public async Task AWriteOverAnExistingKeyReplacesItWhole()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path);
        var key = ObjectKey.ForSession(Session, "transcript", "check", "one");

        await store.PutAsync(key, "the first, much longer value"u8.ToArray(), ObjectContentTypes.Text, TestContext.Current.CancellationToken);
        await store.PutAsync(key, "second"u8.ToArray(), ObjectContentTypes.Text, TestContext.Current.CancellationToken);

        var read = await store.GetAsync(key, TestContext.Current.CancellationToken);

        // A redelivered event overwrites its own object; it must not leave a tail of the previous one.
        Assert.NotNull(read);
        Assert.Equal("second", Encoding.UTF8.GetString(read.Bytes));
    }

    [Fact]
    public async Task DeletingASessionTakesEverythingUnderItAndNothingElse()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path);
        var other = Guid.NewGuid();

        await store.PutAsync(ObjectKey.ForSession(Session, "transcript", "a", "one"), "a"u8.ToArray(), ObjectContentTypes.Text, TestContext.Current.CancellationToken);
        await store.PutAsync(ObjectKey.ForSession(Session, "transcript", "b", "two"), "b"u8.ToArray(), ObjectContentTypes.Text, TestContext.Current.CancellationToken);
        await store.PutAsync(ObjectKey.ForSession(other, "transcript", "c", "three"), "c"u8.ToArray(), ObjectContentTypes.Text, TestContext.Current.CancellationToken);

        var deleted = await store.DeleteSessionAsync(Session, TestContext.Current.CancellationToken);

        Assert.Equal(2, deleted);
        Assert.Null(await store.GetAsync(ObjectKey.ForSession(Session, "transcript", "a", "one"), TestContext.Current.CancellationToken));
        Assert.NotNull(await store.GetAsync(ObjectKey.ForSession(other, "transcript", "c", "three"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnObjectOverTheCapIsRefusedRatherThanWritten()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path);

        // Storage that only grows is an operator's problem handed to them by somebody else. Producers
        // truncate before they call, so exceeding the cap here is a bug in a producer and says so.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.PutAsync(
            ObjectKey.ForSession(Session, "huge"),
            new byte[StorageOptions.MaxObjectBytes + 1],
            ObjectContentTypes.Binary,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..")]
    [InlineData("....//....//etc")]
    [InlineData("/absolute/path")]
    [InlineData("C:\\Windows\\System32")]
    [InlineData("check\u0000name")]
    [InlineData("   ")]
    public void AnUntrustedComponentBecomesExactlyOneHarmlessSegment(string hostile)
    {
        var segment = ObjectKey.Segment(hostile);

        Assert.DoesNotContain("/", segment, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", segment, StringComparison.Ordinal);
        Assert.NotEqual("..", segment);
        Assert.NotEqual(".", segment);
        Assert.NotEmpty(segment);
        Assert.True(segment.Length <= ObjectKey.MaxSegmentLength);
        Assert.All(segment, character => Assert.True(
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_',
            $"'{character}' survived sanitisation."));
    }

    [Fact]
    public void AHostileCheckNameStaysInsideItsSessionPrefix()
    {
        // The whole point: a check named this in .charter/config.yml must not address another session.
        var key = ObjectKey.ForSession(Session, "transcript", "../../../../etc", "passwd");

        Assert.StartsWith(ObjectKey.SessionScope(Session), key, StringComparison.Ordinal);
        Assert.True(ObjectKey.IsWithinSession(key, Session));
        Assert.False(ObjectKey.IsWithinSession(key, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("sessions/../../etc/passwd")]
    [InlineData("sessions//transcript/x")]
    [InlineData("/sessions/x/y")]
    [InlineData("sessions\\x\\y")]
    [InlineData("other/11111111222233334444555555555555/x")]
    [InlineData("")]
    public void AKeyThatIsNotThisSessionsIsRejected(string key)
        => Assert.False(ObjectKey.IsWithinSession(key, Session));

    [Fact]
    public async Task TheFilesystemStoreRefusesAKeyThatWouldEscapeItsRoot()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSystemObjectStore(directory.Path);

        // Belt and braces: ObjectKey already guarantees this cannot be built, and the store refuses it
        // anyway. A traversal has to defeat both, and the second does not care how the first was reached.
        await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.PutAsync(
            "sessions/../../escaped.txt",
            "no"u8.ToArray(),
            ObjectContentTypes.Text,
            TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.GetAsync(
            "sessions/../../escaped.txt",
            TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void AnExtensionIsAppendedAfterSanitisationRatherThanThroughIt()
    {
        // Segment() turns a dot into a separator on purpose, so an extension has to be added after —
        // otherwise every stored object would be served as opaque bytes with a name ending "-txt".
        var key = ObjectKey.WithExtension(ObjectKey.ForSession(Session, "transcript", "npm test", "output"), "txt");

        Assert.EndsWith(".txt", key, StringComparison.Ordinal);
        Assert.Equal(ObjectContentTypes.Text, ObjectContentTypes.ForKey(key));
        Assert.True(ObjectKey.IsWithinSession(key, Session));

        // An unknown extension is opaque bytes, never something a browser will render.
        Assert.Equal(ObjectContentTypes.Binary, ObjectContentTypes.ForKey("sessions/x/y.html"));
    }

    [Fact]
    public void AVeryLongKeyIsShortenedWithoutTwoOfThemColliding()
    {
        var prefix = new string('a', 400);
        var first = ObjectKey.ForSession(Session, prefix, prefix, "one");
        var second = ObjectKey.ForSession(Session, prefix, prefix, "two");

        Assert.True(first.Length <= ObjectKey.MaxKeyLength);
        Assert.True(second.Length <= ObjectKey.MaxKeyLength);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task TheDisabledStoreIsANoOpRatherThanAFailure()
    {
        var store = NullObjectStore.Instance;

        Assert.False(store.Enabled);
        Assert.Null(await store.PutAsync("sessions/x/y", "bytes"u8.ToArray(), ObjectContentTypes.Text, TestContext.Current.CancellationToken));
        Assert.Null(await store.GetAsync("sessions/x/y", TestContext.Current.CancellationToken));
        Assert.Equal(0, await store.DeleteSessionAsync(Session, TestContext.Current.CancellationToken));
    }
}
