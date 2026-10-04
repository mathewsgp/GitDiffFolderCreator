using System;
using System.IO;
using System.Text;
using System.Threading;
using GitDiffFolderCreator.Services;
using Xunit;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// The pipe reader has to come back on every input git can produce, including the ones where git stops
/// halfway or is killed.
/// </summary>
/// <remarks>
/// These drive the reader directly rather than through git, because the interesting inputs are the ones
/// a healthy git never sends: a blob that ends before the length it announced, and a reader that is
/// cancelled part way through. Producing either through a real repository would mean arranging a
/// failure and racing it.
/// </remarks>
public sealed class PipeReaderTests
{
    /// <summary>
    /// Skipping the rest of a blob that ends early. A read at end of stream returns zero, which looks
    /// exactly like "nothing buffered yet": a skip loop that refills and recomputes on that answer
    /// never decreases its count, so the export hangs rather than failing.
    /// </summary>
    [Fact]
    public void Skipping_an_object_that_ends_before_its_announced_length_fails()
    {
        var reader = new PipeReader(new MemoryStream(new byte[10]));

        // A thousand bytes announced, ten delivered: what a git killed part way through a batch looks
        // like from this side of the pipe.
        reader.BeginObject(1000);

        GitCommandException failure = Assert.Throws<GitCommandException>(
            () => reader.SkipRemaining(CancellationToken.None));

        // The same message a short copy produces, and the one the caller already knows how to report.
        Assert.Contains("ended before answering", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same skip with the export cancelled. What is being discarded is content nobody is waiting
    /// for, so it must stop rather than finish: this runs while cancelling an export of a large file.
    /// </summary>
    [Fact]
    public void Skipping_the_rest_of_an_object_stops_when_the_export_is_cancelled()
    {
        // One buffer's worth of a very large object, so a single fill cannot finish the skip and the
        // second one is reached.
        var reader = new PipeReader(new MemoryStream(new byte[8192]));
        reader.BeginObject(10_000_000);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => reader.SkipRemaining(cancellation.Token));
    }

    /// <summary>
    /// The ordinary case: what is left of the object is discarded and the pipe is left at the byte
    /// after it, so the next header is read from the right place.
    /// </summary>
    [Fact]
    public void Skipping_the_rest_of_an_object_leaves_the_pipe_at_the_next_one()
    {
        // Both objects delivered in one buffer, which is what the reader's own read-ahead produces. The
        // caller takes five bytes of a fifteen-byte object, so the skip has real work to do.
        var reader = new PipeReader(new MemoryStream(Encoding.ASCII.GetBytes("hellorest-of-itNEXT\n")));

        reader.BeginObject(15);
        reader.CopyExactly(Stream.Null, 5, CancellationToken.None);
        reader.SkipRemaining(CancellationToken.None);

        Assert.Equal("NEXT", reader.ReadLine(CancellationToken.None));
    }

    /// <summary>
    /// An object the caller read in full has nothing left to skip. Asking anyway must not ask the pipe
    /// for bytes that are not coming, which is what skipping the whole announced length would do.
    /// </summary>
    [Fact]
    public void Skipping_an_object_that_was_read_in_full_does_not_ask_the_pipe_for_anything()
    {
        var reader = new PipeReader(new MemoryStream(Encoding.ASCII.GetBytes("hello")));

        reader.BeginObject(5);
        reader.CopyExactly(Stream.Null, 5, CancellationToken.None);

        // No bytes left to skip, so nothing more is read from a stream that has nothing more to give.
        reader.SkipRemaining(CancellationToken.None);

        GitCommandException failure = Assert.Throws<GitCommandException>(
            () => reader.ReadLine(CancellationToken.None));

        Assert.Contains("ended before answering", failure.Message, StringComparison.Ordinal);
    }
}
