namespace GitDiffFolderCreator.Models
{
    /// <summary>
    /// Everything about one commit that the log row has no room for: who wrote it and when, which
    /// parents it has, and the message beyond its first line.
    /// </summary>
    /// <remarks>
    /// The log list carries only the commit subject, because a row is two lines tall. This is the
    /// rest of it, read on demand when the user asks for it rather than for every commit in the log.
    /// <para>
    /// The files the commit touched are not held here. They are already a
    /// <see cref="GitFileChange"/> list produced by a separate git call with rename detection, and
    /// duplicating them would create two answers to "what changed in this commit".
    /// </para>
    /// </remarks>
    public sealed class GitCommitDetail
    {
        /// <summary>Full 40-character object name.</summary>
        public string Hash { get; set; } = string.Empty;

        public string ShortHash { get; set; } = string.Empty;

        /// <summary>First line of the message, which the log row already shows.</summary>
        public string Subject { get; set; } = string.Empty;

        /// <summary>
        /// The message after the subject, with the usual trailing newline removed. Empty for a
        /// commit whose message is a single line, which is most of them.
        /// </summary>
        public string Body { get; set; } = string.Empty;

        public string AuthorName { get; set; } = string.Empty;

        public string AuthorEmail { get; set; } = string.Empty;

        /// <summary>Author date exactly as git reported it, in ISO-8601.</summary>
        public string AuthorDateText { get; set; } = string.Empty;

        public string CommitterName { get; set; } = string.Empty;

        public string CommitterEmail { get; set; } = string.Empty;

        public string CommitterDateText { get; set; } = string.Empty;

        /// <summary>
        /// The commit's parents, oldest listed first as git orders them. Empty for a root commit,
        /// which has none.
        /// </summary>
        public IList<string> Parents { get; } = new List<string>();

        /// <summary>Refs pointing at this commit, as shown on the log row.</summary>
        public string RefNames { get; set; } = string.Empty;

        /// <summary>True when there is anything past the subject to show.</summary>
        public bool HasBody => Body.Length > 0;

        /// <summary>
        /// The author's name and address as one line, for a field that has room for one.
        /// </summary>
        public string AuthorLine => FormatPerson(AuthorName, AuthorEmail);

        public string CommitterLine => FormatPerson(CommitterName, CommitterEmail);

        /// <summary>
        /// True when someone else wrote the commit than committed it - a rebased or cherry-picked
        /// commit, which is the case where the two names differ and both are worth showing.
        /// </summary>
        public bool CommitterDiffersFromAuthor =>
            !string.Equals(
                (AuthorName + "\0" + AuthorEmail).Trim(),
                (CommitterName + "\0" + CommitterEmail).Trim(),
                StringComparison.Ordinal);

        private static string FormatPerson(string name, string email)
        {
            if (name.Length == 0)
            {
                return email;
            }

            return email.Length == 0 ? name : name + " <" + email + ">";
        }

        /// <summary>
        /// Parses one <c>git show -s --format=...</c> record.
        /// </summary>
        /// <remarks>
        /// The message body is the last field on purpose: it is the only field that can contain the
        /// field separator, a newline or arbitrary punctuation, so everything from it onwards is
        /// rejoined rather than split. That keeps a body containing a control character or a blank
        /// line intact, instead of losing everything after the first occurrence.
        /// </remarks>
        internal static GitCommitDetail? TryParse(string record)
        {
            if (string.IsNullOrWhiteSpace(record))
            {
                return null;
            }

            string[] fields = record.Split(GitServiceConstants.FieldSeparator);

            if (fields.Length < GitCommitDetailFormat.FieldCount)
            {
                return null;
            }

            var detail = new GitCommitDetail
            {
                Hash = fields[0].Trim(),
                ShortHash = fields[1].Trim(),
                AuthorName = fields[2].Trim(),
                AuthorEmail = fields[3].Trim(),
                AuthorDateText = fields[4].Trim(),
                CommitterName = fields[5].Trim(),
                CommitterEmail = fields[6].Trim(),
                CommitterDateText = fields[7].Trim(),
                RefNames = fields[9].Trim(),
                Subject = fields[10].Trim(),
            };

            // %P is a space-separated list, and empty for a root commit. The overload taking a
            // char and StringSplitOptions together does not exist on this framework, so the
            // separators go in as an array.
            foreach (string parent in fields[8].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                detail.Parents.Add(parent);
            }

            detail.Body = string
                .Join(GitServiceConstants.FieldSeparator.ToString(), GetBodyTail(fields))
                .Trim();

            return detail;
        }

        /// <summary>The fields from the body onwards, rejoined so its separators survive.</summary>
        private static string[] GetBodyTail(string[] fields)
        {
            int start = GitCommitDetailFormat.BodyIndex;
            string[] tail = new string[fields.Length - start];
            Array.Copy(fields, start, tail, 0, tail.Length);
            return tail;
        }
    }

    /// <summary>Field layout shared by the writer and the parser of a commit detail record.</summary>
    public static class GitCommitDetailFormat
    {
        /// <summary>
        /// Number of fields in a complete record, counting the body as one field.
        /// </summary>
        /// <remarks>
        /// A commit with a single-line message still produces this many: the body field is present
        /// and empty rather than missing, because the format always writes its separator.
        /// </remarks>
        public const int FieldCount = 12;

        /// <summary>Index of the message body, which is the last field.</summary>
        public const int BodyIndex = 11;

        /// <summary>
        /// One commit's metadata, every field separated by a control character.
        /// </summary>
        /// <remarks>
        /// The body uses <c>%b</c> rather than <c>%B</c>. Both are the unwrapped raw message, but
        /// <c>%B</c> repeats the subject, which is requested separately by <c>%s</c> and already
        /// shown on the log row - asking for both would put the same first line in the dialog twice.
        /// <para>
        /// <c>%aI</c> and <c>%cI</c> are the strict ISO-8601 dates; the default <c>%ad</c> is
        /// locale-formatted and reorders itself under a different system language.
        /// </para>
        /// <para>
        /// The record separator comes first, as in the log format, so that an empty result parses to
        /// nothing rather than to one blank record.
        /// </para>
        /// </remarks>
        public const string Format =
            "--format=%x1e%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%cn%x1f%ce%x1f%cI%x1f%P%x1f%D%x1f%s%x1f%b";
    }
}
