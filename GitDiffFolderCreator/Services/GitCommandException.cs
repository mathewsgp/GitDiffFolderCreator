namespace GitDiffFolderCreator.Services
{
    /// <summary>Raised when a git invocation fails or the repository is unusable.</summary>
    public sealed class GitCommandException : Exception
    {
        public GitCommandException(string message)
            : base(message)
        {
        }

        public GitCommandException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public GitCommandException(string message, string commandLine, string standardError)
            : base(message)
        {
            CommandLine = commandLine;
            StandardError = standardError;
        }

        public string? CommandLine { get; private set; }

        public string? StandardError { get; private set; }
    }
}