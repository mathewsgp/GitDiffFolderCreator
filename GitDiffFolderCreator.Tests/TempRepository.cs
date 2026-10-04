using System.Diagnostics;
using System.Globalization;
using System.Text;
using GitDiffFolderCreator.Services;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// Creates throwaway git repositories for tests. Using a real repository rather than canned output
/// keeps the parsing tests honest about the exact bytes git produces.
/// </summary>
public sealed class TempRepository : IDisposable
{
    private TempRepository(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public static TempRepository Create()
    {
        string root = Path.Combine(Path.GetTempPath(), $"gdfc-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        TempRepository repository = new(root);
        repository.Git("init", "-q", "-b", "main");
        repository.Git("config", "user.email", "test@example.com");
        repository.Git("config", "user.name", "Test User");
        repository.Git("config", "commit.gpgsign", "false");
        return repository;
    }

    /// <summary>Writes a file and stages it, creating parent folders as needed.</summary>
    public void WriteFile(string relativePath, string content)
    {
        string full = ToAbsolute(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        Git("add", "--", relativePath);
    }

    /// <summary>Writes binary content and stages it. Use for files git must report as binary.</summary>
    public void WriteBinaryFile(string relativePath, byte[] content)
    {
        string full = ToAbsolute(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        Git("add", "--", relativePath);
    }

    public void DeleteFile(string relativePath) => Git("rm", "-q", "--", relativePath);

    public void Move(string from, string to) => Git("mv", "--", from, to);

    public string Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
        return RevParse("HEAD");
    }

    /// <summary>
    /// Commits with a subject and a multi-line body.
    /// </summary>
    /// <remarks>
    /// The message goes through a file rather than <c>-m</c>, because passing an argument that
    /// contains newlines is exactly the kind of thing that gets mangled in transit and then blamed
    /// on the parser under test.
    /// <para>
    /// The file is written outside the repository. Writing it inside would make the following
    /// <c>add -A</c> stage the message file as well, so every commit made this way would report one
    /// extra changed file and quietly break any test that counts them.
    /// </para>
    /// </remarks>
    public string CommitWithBody(string subject, string body)
    {
        string messageFile = Path.Combine(Path.GetTempPath(), $"gdfc-msg-{Guid.NewGuid():N}.txt");

        try
        {
            // Git requires the blank line between the subject and the body.
            File.WriteAllText(messageFile, subject + "\n\n" + body + "\n", new UTF8Encoding(false));

            Git("add", "-A");
            Git("commit", "-q", "-F", messageFile);
            return RevParse("HEAD");
        }
        finally
        {
            File.Delete(messageFile);
        }
    }

    /// <summary>
    /// Commits without staging anything, for the case where a commit genuinely changed no files -
    /// an empty commit, a merge, or an amend with nothing new in it.
    /// </summary>
    public string CommitEmpty(string message)
    {
        Git("commit", "-q", "--allow-empty", "-m", message);
        return RevParse("HEAD");
    }

    /// <summary>
    /// Commits with an explicit date, for tests where several commits would otherwise land in the
    /// same second and their chronological order could not be told apart.
    /// </summary>
    public string CommitAt(string message, DateTimeOffset when)
    {
        string stamp = when.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        Git("add", "-A");
        GitWithEnv(
            Root,
            new[]
            {
                "-c", "commit.gpgsign=false",
                "commit", "-q", "-m", message,
                "--date=" + stamp,
            },
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_DATE"] = stamp,
                ["GIT_COMMITTER_DATE"] = stamp,
            });

        return RevParse("HEAD");
    }

    /// <summary>
    /// Commits an exact set of paths without going through the working tree, so a tree can hold
    /// names the local filesystem cannot represent - two paths differing only in case, for
    /// instance, which Windows silently merges into one file.
    /// </summary>
    /// <remarks>
    /// The blobs, index and tree are built by hand against a throwaway index file, and
    /// <c>commit-tree</c> records the result. The working tree and the real index are never touched,
    /// so nothing here can be undone by a later <c>add -A</c> and no name has to survive a
    /// checkout. The commit is left unreferenced: the tests use the returned hash directly.
    /// <para>
    /// Content is hashed with <c>hash-object</c> from a file rather than from standard input,
    /// because it has to work for bytes that are not valid text, and the helper this class has for
    /// starting git does not write to a child's input.
    /// </para>
    /// </remarks>
    /// <param name="files">Path and content, exactly as they should appear in the commit.</param>
    /// <param name="message">Commit message.</param>
    /// <param name="parent">Parent commit hash, or <c>null</c> for a root commit.</param>
    public string CommitTreeOf(IDictionary<string, string> files, string message, string? parent) =>
        CommitTreeOf(files, message, parent, null);

    /// <summary>
    /// As above, but with the file mode given per path, for a tree that has to hold something other
    /// than a regular file - a submodule entry, which is a commit rather than a blob.
    /// </summary>
    public string CommitTreeOf(
        IDictionary<string, string> files,
        string message,
        string? parent,
        IDictionary<string, string>? modes)
    {
        string indexPath = Path.Combine(Path.GetTempPath(), $"gdfc-index-{Guid.NewGuid():N}");
        string contentPath = Path.Combine(Path.GetTempPath(), $"gdfc-blob-{Guid.NewGuid():N}");

        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = indexPath };

        try
        {
            // An index that does not exist yet, so there is nothing in it to inherit.
            RunGitIn(Root, new[] { "read-tree", "--empty" }, env);

            foreach (KeyValuePair<string, string> file in files)
            {
                string mode = "100644";

                if (modes != null && modes.TryGetValue(file.Key, out string? given))
                {
                    mode = given;
                }

                string blob;

                if (mode == "160000")
                {
                    // A gitlink points at a commit in another repository and has no content of its
                    // own, so the value is the object id it records rather than bytes to hash. Git
                    // rejects a null id, so the caller has to name a real commit.
                    blob = file.Value;
                }
                else
                {
                    File.WriteAllText(contentPath, file.Value, new UTF8Encoding(false));

                    blob = RunGitIn(
                        Root,
                        new[] { "hash-object", "-w", contentPath },
                        env: null).Trim();
                }

                // The three-argument form of --cacheinfo, which is the one that survives a path
                // containing a space or a comma.
                RunGitIn(
                    Root,
                    new[] { "update-index", "--add", "--cacheinfo", mode, blob, file.Key },
                    env);
            }

            string tree = RunGitIn(Root, new[] { "write-tree" }, env).Trim();

            var commit = new List<string> { "commit-tree", tree };
            if (parent != null)
            {
                commit.Add("-p");
                commit.Add(parent);
            }

            commit.Add("-m");
            commit.Add(message);

            return RunGitIn(Root, commit.ToArray(), env).Trim();
        }
        finally
        {
            foreach (string temporary in new[] { indexPath, contentPath })
            {
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public string RevParse(string reference) => Git("rev-parse", reference).Trim();

    public string ToAbsolute(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public string Read(string relativePath) => File.ReadAllText(ToAbsolute(relativePath));

    /// <summary>Creates a bare repository to serve as a remote, and returns its path.</summary>
    public string CreateRemote()
    {
        string remote = Path.Combine(Path.GetTempPath(), $"gdfc-remote-{Guid.NewGuid():N}");

        // The target folder does not exist yet, so git must not be started inside it.
        RunGitIn(Path.GetTempPath(), "init", "-q", "--bare", remote);
        Git("remote", "add", "origin", remote);
        Git("push", "-q", "-u", "origin", "HEAD");
        string branch = Git("rev-parse", "--abbrev-ref", "HEAD").Trim();

        // A bare repository's HEAD names a branch that does not exist yet, which makes a later clone
        // check out nothing. Point it at the branch that was just pushed.
        RunGitIn(remote, "symbolic-ref", "HEAD", "refs/heads/" + branch);

        return remote;
    }

    /// <summary>Adds an empty commit on the remote side so the local branch falls behind.</summary>
    public void AdvanceRemote()
    {
        string remote = Git("remote", "get-url", "origin").Trim();
        string branch = Git("rev-parse", "--abbrev-ref", "HEAD").Trim();
        string clone = Path.Combine(Path.GetTempPath(), $"gdfc-clone-{Guid.NewGuid():N}");

        try
        {
            // The clone folder is created by git, so the process must start outside it.
            RunGitIn(Path.GetTempPath(), "clone", "-q", remote, clone);
            RunGitIn(clone, "config", "user.email", "test@example.com");
            RunGitIn(clone, "config", "user.name", "Test User");
            RunGitIn(clone, "commit", "-q", "--allow-empty", "-m", "remote commit");
            RunGitIn(clone, "push", "-q", "origin", "HEAD:" + branch);
        }
        finally
        {
            try
            {
                Directory.Delete(clone, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        Git("fetch", "-q", "origin");
    }

    /// <summary>
    /// Pushes a new branch to the remote from a separate clone, so this repository learns nothing
    /// about it until it fetches. Used to prove that the application updates its own refs.
    /// </summary>
    public void PushNewBranchFromClone(string branchName)
    {
        string remote = Git("remote", "get-url", "origin").Trim();
        string startPoint = Git("rev-parse", "--abbrev-ref", "HEAD").Trim();
        string clone = Path.Combine(Path.GetTempPath(), $"gdfc-clone-{Guid.NewGuid():N}");

        try
        {
            // The clone folder is created by git, so the process must start outside it.
            RunGitIn(Path.GetTempPath(), "clone", "-q", remote, clone);
            RunGitIn(clone, "config", "user.email", "test@example.com");
            RunGitIn(clone, "config", "user.name", "Test User");
            RunGitIn(clone, "checkout", "-q", "-b", branchName, startPoint);
            RunGitIn(clone, "commit", "-q", "--allow-empty", "-m", "pushed from elsewhere");
            RunGitIn(clone, "push", "-q", "origin", branchName);
        }
        finally
        {
            try
            {
                Directory.Delete(clone, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Runs git with an argument list, never through a shell.</summary>
    public string Git(params string[] arguments) => RunGitIn(Root, arguments);

    /// <summary>Runs git with extra environment variables, for per-invocation settings like dates.</summary>
    private static string GitWithEnv(string workingDirectory, string[] arguments, IDictionary<string, string> env) =>
        RunGitIn(workingDirectory, arguments, env);

    private static string RunGitIn(string workingDirectory, params string[] arguments) =>
        RunGitIn(workingDirectory, arguments, env: null);

    private static string RunGitIn(
        string workingDirectory,
        string[] arguments,
        IDictionary<string, string>? env)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = WindowsArgumentString.Build(arguments),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (env != null)
        {
            foreach (var pair in env)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git.");

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.Format(
                "git {0} failed ({1}): {2}", string.Join(" ", arguments), process.ExitCode, error));
        }

        return output;
    }

    public void Dispose()
    {
        try
        {
            // Git objects are read-only, but the index is not, so clear that attribute first.
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory must not fail an otherwise passing test run.
        }
    }
}