using System.Diagnostics;
using System.IO;
using System.Text;

namespace GitDiffFolderCreator.Services;

/// <summary>
/// Raw outcome of a git invocation. The streams are kept separate so callers can tell data from
/// diagnostics and never mistake a <c>fatal:</c> line for data.
/// </summary>
internal sealed class GitProcessResult
{
    public GitProcessResult(string standardOutput, string standardError, int exitCode)
    {
        StandardOutput = standardOutput;
        StandardError = standardError;
        ExitCode = exitCode;
    }

    public string StandardOutput { get; }

    public string StandardError { get; }

    public int ExitCode { get; }

    public bool Succeeded
    {
        get { return ExitCode == 0; }
    }
}

/// <summary>
/// Reads the <c>git cat-file --batch</c> protocol off a pipe through a fixed buffer.
/// </summary>
/// <remarks>
/// Reading a header a byte at a time costs one pipe read per byte, and a header is around fifty of
/// them. Buffering measured about 7% faster on a several-hundred-file export and cut its allocation
/// sixteen-fold, which is the difference between a header that costs a few allocations and one that
/// costs one per character.
/// <para>
/// Reading ahead is safe here and is the point of the buffer: only one request is ever outstanding,
/// so the bytes after the current answer can only belong to an answer that has not been asked for yet
/// and therefore cannot exist. What must not happen is reading into the <em>next</em> answer and
/// losing track of it, which is why <see cref="SkipTo"/> exists and why content is counted rather
/// than "read until it stops".
/// </para>
/// </remarks>
internal sealed class PipeReader
{
    private const int Capacity = 8192;

    private readonly Stream stream;
    private readonly byte[] buffer = new byte[Capacity];
    private int start;
    private int end;

    /// <summary>
    /// Bytes of the current object already handed to the caller, and the object's announced length.
    /// </summary>
    private long handed;
    private long length;

    public PipeReader(Stream stream)
    {
        this.stream = stream;
    }

    /// <summary>Reads up to and including the next newline, discarding the newline.</summary>
    public string ReadLine(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();

        while (true)
        {
            if (start == end)
            {
                Fill(cancellationToken);
            }

            byte b = buffer[start++];

            if (b == (byte)'\n')
            {
                // A header ends an object, so the next one starts from zero. The caller's own
                // ReadLine for the trailing newline resets it again, which is harmless.
                handed = 0;
                return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
            }

            bytes.Add(b);
        }
    }

    /// <summary>
    /// Records the announced length of the object about to be handed over, so
    /// <see cref="SkipRemaining"/> knows how much of it there is.
    /// </summary>
    public void BeginObject(long size)
    {
        length = size;
        handed = 0;
    }

    /// <summary>Copies exactly <paramref name="count"/> bytes into <paramref name="to"/>.</summary>
    public void CopyExactly(Stream to, long count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            if (start == end)
            {
                Fill(cancellationToken);
            }

            int available = (int)Math.Min(count, end - start);
            to.Write(buffer, start, available);
            start += available;
            handed += available;
            count -= available;
        }
    }

    /// <summary>
    /// Discards the part of the current object the caller did not read, leaving the pipe positioned
    /// at the byte after it.
    /// </summary>
    /// <remarks>
    /// Refilled through <see cref="Fill"/> rather than read from the stream directly, and that is what
    /// makes this loop terminate. A read at end of stream returns 0, which is indistinguishable from
    /// "nothing buffered yet": a loop that refills and recomputes on that answer never decreases its
    /// count and never exits. Git stops mid-answer when it is killed or when it fails part way through
    /// a batch, and cancellation kills it, so this is a path that is reached rather than a theoretical
    /// one. Going through <see cref="Fill"/> also means a cancelled export stops here instead of
    /// draining a blob nobody is waiting for.
    /// </remarks>
    public void SkipRemaining(CancellationToken cancellationToken)
    {
        long count = length - handed;

        while (count > 0)
        {
            if (start == end)
            {
                Fill(cancellationToken);
            }

            int available = (int)Math.Min(count, end - start);
            start += available;
            count -= available;
        }

        length = 0;
        handed = 0;
    }

    private void Fill(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The read itself cannot be cancelled, so the token is checked either side of it: a cancelled
        // export stops at the next refill rather than after the current one finishes.
        start = 0;
        end = stream.Read(buffer, 0, Capacity);

        if (end == 0)
        {
            // Git stopped mid-answer, which means it exited early or was killed. The exit code would
            // explain it, but the caller is waiting on an answer that is never coming.
            throw new GitCommandException(
                "git cat-file --batch ended before answering every request.");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>
/// One answer from <c>git cat-file --batch</c>.
/// </summary>
internal sealed class CatFileEntry
{
    public CatFileEntry(
        string request, bool exists, string objectType, long size, PipeReader? content)
    {
        Request = request;
        Exists = exists;
        ObjectType = objectType;
        Size = size;
        Content = content;
    }

    /// <summary>The line that was asked for, echoed back.</summary>
    public string Request { get; }

    /// <summary>False when Git answered <c>missing</c>, meaning no such object at that revision.</summary>
    public bool Exists { get; }

    /// <summary>Object type: <c>blob</c>, or <c>commit</c>/<c>tree</c> for a path that is not a file.</summary>
    public string ObjectType { get; }

    public long Size { get; }

    /// <summary>
    /// The object's bytes. Null when the object does not exist; otherwise valid for exactly
    /// <see cref="Size"/> bytes, after which the runner discards any remainder.
    /// </summary>
    public PipeReader? Content { get; }

    public static CatFileEntry Missing(string request) =>
        new CatFileEntry(request, false, "missing", 0, null);
}

/// <summary>
/// Runs git without ever involving a shell.
/// </summary>
/// <remarks>
/// .NET Framework has no <c>ProcessStartInfo.ArgumentList</c>, so the argument vector is quoted by
/// <see cref="WindowsArgumentString"/> and handed to git directly. No shell is involved, so a
/// pathname such as <c>a&amp;calc.exe</c> is passed through as a pathname - the previous
/// <c>cmd /c</c> approach executed it.
/// </remarks>
internal static class GitProcessRunner
{
    public static Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        return RunAsync(workingDirectory, ToList(arguments), null, cancellationToken);
    }

    /// <summary>
    /// Runs git with extra environment variables in the child process only.
    /// </summary>
    /// <remarks>
    /// Needed for settings that must not leak into anything else git might start, such as
    /// <c>GIT_TERMINAL_PROMPT</c>, which stops git blocking on a credential prompt in a window with
    /// no console to answer it.
    /// </remarks>
    public static async Task<GitProcessResult> RunAsync(
        string workingDirectory,
        List<string> arguments,
        IDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = CreateStartInfo(workingDirectory, arguments, environment);

        using (Process process = new Process())
        {
            process.StartInfo = startInfo;

            try
            {
                if (!process.Start())
                {
                    throw new GitCommandException("Could not start git. Make sure git is installed and on PATH.");
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new GitCommandException("Could not start git. Make sure git is installed and on PATH.", ex);
            }

            // Process.WaitForExitAsync does not exist on .NET Framework, so the exit notification is
            // bridged onto a task instead. EnableRaisingEvents is required for Exited to fire.
            TaskCompletionSource<int> exited = new TaskCompletionSource<int>();
            process.Exited += (sender, e) => exited.TrySetResult(process.ExitCode);
            process.EnableRaisingEvents = true;

            using (cancellationToken.Register(() => TryKill(process)))
            {
                // Drain both pipes before waiting so a large log cannot deadlock the child process.
                Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
                Task<string> standardError = process.StandardError.ReadToEndAsync();

                int exitCode = await exited.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                return new GitProcessResult(
                    await standardOutput.ConfigureAwait(false),
                    await standardError.ConfigureAwait(false),
                    exitCode);
            }
        }
    }

    /// <summary>
    /// Runs git and writes its standard output straight to <paramref name="destinationPath"/>,
    /// byte for byte.
    /// </summary>
    /// <remarks>
    /// The text overloads decode the output as UTF-8, which silently corrupts a binary blob: the
    /// bytes are replaced with U+FFFD and the file on disk is no longer what git stored. Copying the
    /// raw stream avoids that, and no encoding is set on the reader for the same reason.
    /// <para>
    /// Standard error is still redirected and drained, so a large diagnostic cannot deadlock the
    /// child. Only the start is checked, and a non-zero exit code is returned rather than thrown, so
    /// the caller can treat "this path is not in that commit" as an ordinary answer.
    /// </para>
    /// </remarks>
    public static async Task<GitProcessResult> RunToFileAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = CreateStartInfo(workingDirectory, ToList(arguments), null);

        // The encoding the other calls set would decode the bytes before the file ever sees them.
        startInfo.StandardOutputEncoding = null;

        using (Process process = new Process())
        {
            process.StartInfo = startInfo;

            try
            {
                if (!process.Start())
                {
                    throw new GitCommandException("Could not start git. Make sure git is installed and on PATH.");
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new GitCommandException("Could not start git. Make sure git is installed and on PATH.", ex);
            }

            TaskCompletionSource<int> exited = new TaskCompletionSource<int>();
            process.Exited += (sender, e) => exited.TrySetResult(process.ExitCode);
            process.EnableRaisingEvents = true;

            using (cancellationToken.Register(() => TryKill(process)))
            {
                Task<string> standardError = process.StandardError.ReadToEndAsync();

                // FileMode.Create, so a failed run cannot leave a previous file in place looking
                // like this one succeeded.
                using (FileStream file = new FileStream(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    await process.StandardOutput.BaseStream.CopyToAsync(file, 81920, cancellationToken)
                        .ConfigureAwait(false);
                }

                int exitCode = await exited.Task.ConfigureAwait(false);

                return new GitProcessResult(
                    string.Empty,
                    await standardError.ConfigureAwait(false),
                    exitCode);
            }
        }
    }

    /// <summary>
    /// Asks git for many objects in one process, through <c>git cat-file --batch</c>.
    /// </summary>
    /// <remarks>
    /// One request is outstanding at a time and the answer is fully consumed before the next request
    /// is written. That is what keeps this from deadlocking: the child blocks writing once the pipe
    /// is full, so a caller that queued every request first would block writing while the child
    /// blocked reading. The cost is one round trip per object, which is far cheaper than the process
    /// per batch this replaces, and the path no longer travels on a command line at all.
    /// <para>
    /// Standard output is read as raw bytes and never decoded: the object is handed to the caller as
    /// a stream, so a binary blob arrives as the bytes Git stored.
    /// </para>
    /// <para>
    /// A path that resolves to something other than a blob - a submodule, which is a commit object,
    /// or a directory - is reported with its type rather than being written out as a file, because
    /// there is no file content for it to have.
    /// </para>
    /// </remarks>
    public static async Task RunCatFileBatchAsync(
        string workingDirectory,
        IEnumerable<string> requests,
        Func<CatFileEntry, Task> onEntry,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = CreateStartInfo(
            workingDirectory,
            new[] { "cat-file", "--batch" },
            environment: null);

        startInfo.RedirectStandardInput = true;

        // The answers are read a line at a time, and a blob is copied straight through: decoding
        // either would corrupt binary content.
        startInfo.StandardOutputEncoding = null;

        using (Process process = new Process())
        {
            process.StartInfo = startInfo;

            try
            {
                if (!process.Start())
                {
                    throw new GitCommandException("Could not start git. Make sure git is installed and on PATH.");
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new GitCommandException("Could not start git. Make sure git is installed and on PATH.", ex);
            }

            using (cancellationToken.Register(() => TryKill(process)))
            {
                // Diagnostics are drained continuously. A pipe left unread can fill and block the
                // child, and nothing here reads it again.
                Task<string> standardError = process.StandardError.ReadToEndAsync();

                Stream input = process.StandardInput.BaseStream;
                var reader = new PipeReader(process.StandardOutput.BaseStream);

                foreach (string request in requests)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Git's request protocol is line-delimited, so a request containing a newline
                    // would be read as two. Windows forbids that in a path, and Git's own path
                    // validation rejects it too, but the alternative - silently asking about the
                    // wrong object - is worse than saying so.
                    if (request.IndexOf('\n') >= 0 || request.IndexOf('\r') >= 0)
                    {
                        throw new GitCommandException(
                            "A path containing a line break cannot be requested from git: " + request);
                    }

                    byte[] line = Encoding.UTF8.GetBytes(request + "\n");

                    await input.WriteAsync(line, 0, line.Length, cancellationToken).ConfigureAwait(false);
                    await input.FlushAsync(cancellationToken).ConfigureAwait(false);

                    string header = reader.ReadLine(cancellationToken);

                    if (header.EndsWith(" missing", StringComparison.Ordinal))
                    {
                        await onEntry(CatFileEntry.Missing(request)).ConfigureAwait(false);
                        continue;
                    }

                    long size = ParseBatchHeader(header, request);
                    reader.BeginObject(size);

                    // The object is handed over as a window onto the pipe rather than as bytes, so a
                    // large blob is never held in memory. CopyExactly consumes precisely Size bytes,
                    // and the buffer may already hold part of the next answer without disturbing it.
                    await onEntry(
                        new CatFileEntry(request, true, ParseBatchType(header), size, reader))
                        .ConfigureAwait(false);

                    // Whatever the caller did not read has to go, or the next header is read from the
                    // wrong place. Only the *remainder* is skipped: the caller has usually taken the
                    // whole object already, and skipping the full size again would ask the pipe for
                    // bytes that are never coming and block until the child was killed.
                    reader.SkipRemaining(cancellationToken);
                    reader.ReadLine(cancellationToken);
                }

                // Closing stdin tells git there is nothing more to answer, so it exits rather than
                // waiting on a request that is never coming.
                input.Close();

                TaskCompletionSource<int> exited = new TaskCompletionSource<int>();
                process.Exited += (sender, e) => exited.TrySetResult(process.ExitCode);
                process.EnableRaisingEvents = true;

                int exitCode = await exited.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                string errors = await standardError.ConfigureAwait(false);

                if (exitCode != 0)
                {
                    throw new GitCommandException(
                        string.Format("git cat-file --batch failed with exit code {0}.", exitCode),
                        "git cat-file --batch",
                        errors.Trim());
                }
            }
        }
    }


    private static long ParseBatchHeader(string header, string request)
    {
        int sizeAt = header.LastIndexOf(' ');

        if (sizeAt < 0 || !long.TryParse(
                header.Substring(sizeAt + 1),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out long size))
        {
            throw new GitCommandException(
                "Could not read git cat-file --batch's answer for '" + request + "': " + header);
        }

        return size;
    }

    /// <summary>
    /// Reads the type out of a <c>&lt;object&gt; &lt;type&gt; &lt;size&gt;</c> header, which is the
    /// middle of the three fields.
    /// </summary>
    private static string ParseBatchType(string header)
    {
        int typeAt = header.IndexOf(' ') + 1;
        int sizeAt = typeAt > 0 ? header.IndexOf(' ', typeAt) : -1;

        return sizeAt < 0 ? header.Substring(Math.Max(typeAt, 0)) : header.Substring(typeAt, sizeAt - typeAt);
    }

    /// <summary>
    /// Exposes exactly <c>length</c> bytes of a larger stream, then discards the rest on dispose.
    /// </summary>
    /// <remarks>
    /// The batch protocol puts the next answer immediately after the content, so a reader that
    /// stopped early or ran long would corrupt everything after it. Clamping the reads and draining
    /// what is left means the stream is always positioned at the next answer, whether the caller read
    /// the object, ignored it, or was interrupted halfway.
    /// </remarks>
    private sealed class BoundedStream : Stream
    {
        private readonly Stream inner;
        private long remaining;

        public BoundedStream(Stream inner, long length)
        {
            this.inner = inner;
            remaining = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int allowed = (int)Math.Min(count, remaining);
            int read = allowed <= 0 ? 0 : inner.Read(buffer, offset, allowed);
            remaining -= read;
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            int allowed = (int)Math.Min(count, remaining);
            int read = allowed <= 0 ? 0 : await inner.ReadAsync(buffer, offset, allowed, cancellationToken)
                .ConfigureAwait(false);
            remaining -= read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Drain();
            }

            base.Dispose(disposing);
        }

        private void Drain()
        {
            var scratch = new byte[8192];

            while (remaining > 0)
            {
                int allowed = (int)Math.Min(scratch.Length, remaining);
                int read = inner.Read(scratch, 0, allowed);

                if (read == 0)
                {
                    // git will not send more; the next read reports the truncation.
                    return;
                }

                remaining -= read;
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public static async Task<GitProcessResult> RunCheckedAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        List<string> argumentList = ToList(arguments);
        GitProcessResult result = await RunAsync(workingDirectory, argumentList, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            string commandLine = WindowsArgumentString.Build(argumentList);
            throw new GitCommandException(
                string.Format("git {0} failed with exit code {1}.", commandLine, result.ExitCode),
                commandLine,
                result.StandardError.Trim());
        }

        return result;
    }

    /// <summary>Runs git and returns the standard output, treating any non-zero exit code as fatal.</summary>
    public static async Task<string> RunForOutputAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        GitProcessResult result = await RunCheckedAsync(workingDirectory, arguments, cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    private static ProcessStartInfo CreateStartInfo(
        string workingDirectory,
        IEnumerable<string> arguments,
        IDictionary<string, string>? environment)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            Arguments = WindowsArgumentString.Build(arguments),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (environment != null)
        {
            foreach (KeyValuePair<string, string> pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return startInfo;
    }

    /// <summary>
    /// Materialises an argument sequence once, so a caller can enumerate it again for a message
    /// without risking a second run of a lazy query.
    /// </summary>
    /// <remarks>
    /// <c>RunCheckedAsync</c> builds its failure message from the same arguments it ran with, which is
    /// the point of the message: it has to be the command that actually failed.
    /// </remarks>
    private static List<string> ToList(IEnumerable<string> arguments)
    {
        List<string> list = new List<string>(arguments);
        return list;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited between the check and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied while terminating; the process is reaped when it exits on its own.
        }
    }
}
