using System.Collections.ObjectModel;
using System.Globalization;
using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.Services;

namespace GitDiffFolderCreator.ViewModels;

/// <summary>
/// Backs the commit details dialog: the metadata the log row has no room for, and the files that
/// commit touched.
/// </summary>
/// <remarks>
/// Separate from the window so the loading, the totals and the filter can be tested without showing
/// a dialog, in the same way <see cref="DiffToolPickerViewModel"/> is.
/// <para>
/// The subject is seeded from the log row before anything is read, so the dialog opens with something
/// on screen instead of an empty frame while two git calls run.
/// </para>
/// </remarks>
public sealed class CommitDetailViewModel : BindableBase
{
    private readonly GitService _git;
    private readonly GitCommit _commit;
    private readonly string? _diffToolCommand;
    private readonly ObservableCollection<GitFileChange> _allFiles = new();

    private GitCommitDetail? _detail;
    private string _filter = string.Empty;
    private bool _isLoading;
    private string? _errorMessage;
    private string? _diffMessage;
    private bool _isDiffing;

    public CommitDetailViewModel(
        GitService git,
        GitCommit commit,
        string? diffToolCommand = null)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));

        // Null rather than empty when the user has not chosen a tool, because that is the difference
        // between "use whatever git is configured with" and "use nothing". See DiffToolLauncher.
        _diffToolCommand = string.IsNullOrWhiteSpace(diffToolCommand) ? null : diffToolCommand;

        Files = new ObservableCollection<GitFileChange>();

        // Takes the double-clicked row as its parameter, so the list does not have to track a selection
        // for this: the row under the pointer is the one the user asked about.
        ShowDifferenceCommand = new RelayCommand(
            parameter => ShowDifferenceFor(parameter as GitFileChange),
            _ => CanShowDifference);

        // Shown immediately, from the row the user clicked. Overwritten by the real value once the
        // call returns, which is when the rest of the fields appear with it.
        Subject = commit.Message;
        ShortHash = commit.ShortHash;
        RefNames = commit.RefNames;
    }

    /// <summary>
    /// Hands one of the commit's files to the diff tool, showing its two versions.
    /// </summary>
    /// <remarks>
    /// The two sides are the commit and its <em>first</em> parent, which is the same pair the file
    /// list itself came from — so what the tool shows is what the list is a summary of. A merge has
    /// two parents and diffing against the first is the conventional "what did this branch do here",
    /// which is also what <c>git show</c> reports.
    /// </remarks>
    public RelayCommand ShowDifferenceCommand { get; }

    /// <summary>The files that pass the current filter, in the order git reported them.</summary>
    public ObservableCollection<GitFileChange> Files { get; }

    public string Subject { get; private set; }

    public string ShortHash { get; private set; }

    public string Body => _detail?.Body ?? string.Empty;

    public bool HasBody => _detail?.HasBody == true;

    public string FullHash => _detail?.Hash ?? string.Empty;

    public string AuthorLine => _detail?.AuthorLine ?? string.Empty;

    public string AuthorDateText => _detail?.AuthorDateText ?? string.Empty;

    public string CommitterLine => _detail?.CommitterLine ?? string.Empty;

    public string CommitterDateText => _detail?.CommitterDateText ?? string.Empty;

    /// <summary>
    /// Whether the committer is worth a field of its own. A rebased or cherry-picked commit has a
    /// different one from the author, which is the whole reason for showing both.
    /// </summary>
    public bool IsCommitterDifferent => _detail?.CommitterDiffersFromAuthor == true;

    public string ParentsText => FormatParents(_detail);

    /// <summary>Refs pointing at this commit, falling back to the log row until the read lands.</summary>
    public string RefNames { get; private set; }

    public bool HasRefNames => RefNames.Length > 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>Set when the commit could not be read, and the reason to show instead of it.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(CanShowDifference));
                ShowDifferenceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasError => ErrorMessage != null;

    /// <summary>
    /// What the last diff launch said, shown beside the list rather than thrown away.
    /// </summary>
    /// <remarks>
    /// A launch either opens a tool or does not, and neither is visible from a window that only
    /// reports failures from its own reads. Without this, a double-click on a commit with no diff tool
    /// configured would look like nothing had happened.
    /// </remarks>
    public string? DiffMessage
    {
        get => _diffMessage;
        private set
        {
            if (SetProperty(ref _diffMessage, value))
            {
                OnPropertyChanged(nameof(HasDiffMessage));
                OnPropertyChanged(nameof(DiffMessageIsError));
            }
        }
    }

    public bool HasDiffMessage => DiffMessage != null;

    /// <summary>
    /// Whether the last launch failed, so the message is shown as a problem rather than as news.
    /// </summary>
    public bool DiffMessageIsError { get; private set; }

    /// <summary>
    /// The commit's first parent, which is the other side of every difference in the file list.
    /// </summary>
    public string BaseHash =>
        _detail != null && _detail.Parents.Count > 0 ? _detail.Parents[0] : string.Empty;

    /// <summary>
    /// Whether there is a pair of commits to compare, and nothing in flight that would make it stale.
    /// </summary>
    /// <remarks>
    /// A root commit has no parent, so there is nothing to compare it against and the list is a list of
    /// files rather than of changes. Saying so is better than letting the double-click fail.
    /// </remarks>
    public bool CanShowDifference =>
        !IsLoading && !IsDiffing && !HasError && BaseHash.Length > 0 && Files.Count > 0;

    private bool IsDiffing
    {
        get => _isDiffing;
        set
        {
            if (SetProperty(ref _isDiffing, value))
            {
                OnPropertyChanged(nameof(CanShowDifference));
                ShowDifferenceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Double-clicking a file row asks for that file's two versions.</summary>
    public void ShowDifferenceFor(GitFileChange? change)
    {
        if (change != null)
        {
            _ = ShowFileDifferenceAsync(change);
        }
    }

    /// <summary>
    /// Extracts one file as it was in the parent and as it is in this commit, and hands both to the
    /// diff tool.
    /// </summary>
    /// <remarks>
    /// The extracted copies are left in the temporary folder, for the same reason the main window leaves
    /// them: the tool is still reading them when this returns.
    /// <para>
    /// Fire and forget, and failures are reported in the window rather than thrown. The user opened a
    /// window to read a commit, and a diff tool that is missing or misconfigured is not worth taking
    /// the application down for.
    /// </para>
    /// </remarks>
    private async Task ShowFileDifferenceAsync(GitFileChange change)
    {
        string baseHash = BaseHash;

        if (baseHash.Length == 0 || IsDiffing || IsLoading)
        {
            return;
        }

        IsDiffing = true;
        DiffMessage = null;

        try
        {
            DiffToolLauncher launcher = new DiffToolLauncher();

            DiffToolLaunchResult result = await launcher
                .LaunchAsync(
                    _git,
                    change,
                    baseHash,
                    _commit.Hash,
                    _diffToolCommand,
                    CancellationToken.None)
                .ConfigureAwait(true);

            DiffMessageIsError = result.IsError;
            DiffMessage = result.Message;
        }
        catch (Exception ex)
        {
            DiffMessageIsError = true;
            DiffMessage = ex.Message;
        }
        finally
        {
            IsDiffing = false;
        }
    }

    /// <summary>
    /// Narrows the file list. Empty shows everything.
    /// </summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>Total number of files the commit touched, before the filter.</summary>
    public int TotalFileCount => _allFiles.Count;

    /// <summary>Files currently listed, after the filter.</summary>
    public int ShownFileCount => Files.Count;

    /// <summary>Lines added across the commit's files, counting only those git gave a count for.</summary>
    public int TotalAddedLines => SumLineCounts(static file => file.AddedLines);

    public int TotalDeletedLines => SumLineCounts(static file => file.DeletedLines);

    public int BinaryFileCount => _allFiles.Count(static file => file.IsBinary);

    /// <summary>
    /// A one-line summary of what the commit did, e.g. <c>12 files changed, +340 -56</c>.
    /// </summary>
    public string FileSummary
    {
        get
        {
            if (TotalFileCount == 0)
            {
                return "No files changed";
            }

            string files = TotalFileCount == 1
                ? "1 file changed"
                : string.Format(
                    CultureInfo.InvariantCulture, "{0} files changed", TotalFileCount);

            string text = string.Format(CultureInfo.InvariantCulture, "{0}, +{1} -{2}",
                files, TotalAddedLines, TotalDeletedLines);

            // Said separately because binary files contribute no lines at all, and their absence
            // from the totals would otherwise look like they were not counted.
            return BinaryFileCount > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0} ({1} binary)", text, BinaryFileCount)
                : text;
        }
    }

    /// <summary>
    /// True when a filter is hiding some of the files, so the counts can be reconciled.
    /// </summary>
    public bool IsFiltered => ShownFileCount != TotalFileCount;

    /// <summary>
    /// Adds up one of the two line counts over every file the commit touched.
    /// </summary>
    /// <remarks>
    /// Files git reported as binary have no count at all. They contribute nothing rather than a
    /// zero, which is the same answer for the sum but is stated here so that the absence is not
    /// later mistaken for a bug in the totals.
    /// </remarks>
    private int SumLineCounts(Func<GitFileChange, int?> select)
    {
        int sum = 0;

        foreach (GitFileChange file in _allFiles)
        {
            if (select(file) is int value)
            {
                sum += value;
            }
        }

        return sum;
    }

    private static string FormatParents(GitCommitDetail? detail)
    {
        if (detail == null || detail.Parents.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(", ", detail.Parents);
    }

    /// <summary>
    /// Raises the change notifications for everything derived from the loaded detail.
    /// </summary>
    /// <remarks>
    /// Grouped rather than spread through the load, because they all change together the moment the
    /// detail lands; a reader of this code should see them as one step.
    /// </remarks>
    private void OnDetailLoaded()
    {
        Subject = _detail?.Subject ?? Subject;
        ShortHash = _detail?.ShortHash ?? ShortHash;

        // The log row's refs are a reasonable value until the read lands, and git reports them again
        // here, so the dialog ends up agreeing with the row the user clicked.
        if (_detail != null && _detail.RefNames.Length > 0)
        {
            RefNames = _detail.RefNames;
        }

        OnPropertyChanged(nameof(Subject));
        OnPropertyChanged(nameof(ShortHash));
        OnPropertyChanged(nameof(FullHash));
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(HasBody));
        OnPropertyChanged(nameof(AuthorLine));
        OnPropertyChanged(nameof(AuthorDateText));
        OnPropertyChanged(nameof(CommitterLine));
        OnPropertyChanged(nameof(CommitterDateText));
        OnPropertyChanged(nameof(IsCommitterDifferent));
        OnPropertyChanged(nameof(ParentsText));
        OnPropertyChanged(nameof(RefNames));
        OnPropertyChanged(nameof(HasRefNames));
        OnPropertyChanged(nameof(TotalFileCount));
        OnPropertyChanged(nameof(FileSummary));

        // The command's answer depends on the parents, which only arrive with the read.
        OnPropertyChanged(nameof(BaseHash));
        OnPropertyChanged(nameof(CanShowDifference));
        ShowDifferenceCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Republishes the file totals, which change when the list does.
    /// </summary>
    private void OnFilesChanged()
    {
        OnPropertyChanged(nameof(TotalFileCount));
        OnPropertyChanged(nameof(ShownFileCount));
        OnPropertyChanged(nameof(TotalAddedLines));
        OnPropertyChanged(nameof(TotalDeletedLines));
        OnPropertyChanged(nameof(BinaryFileCount));
        OnPropertyChanged(nameof(FileSummary));
        OnPropertyChanged(nameof(IsFiltered));

        // The file count is part of whether a diff is possible at all.
        OnPropertyChanged(nameof(CanShowDifference));
        ShowDifferenceCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Reads the commit and its file list.
    /// </summary>
    /// <remarks>
    /// The two calls are independent and are made together rather than one after the other, because
    /// on a large repository either can take a noticeable moment and the dialog is already on screen
    /// by then.
    /// <para>
    /// A failure is reported in the dialog rather than thrown: the user opened a window to read a
    /// commit, and an exception here would take the whole application down for something that only
    /// affects one row.
    /// </para>
    /// </remarks>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            Task<GitCommitDetail> detailTask = _git.GetCommitDetailAsync(_commit.Hash, cancellationToken);
            Task<IList<GitFileChange>> filesTask =
                _git.GetChangesForCommitAsync(_commit.Hash, cancellationToken);

            await Task.WhenAll(detailTask, filesTask).ConfigureAwait(true);

            _detail = await detailTask.ConfigureAwait(true);

            _allFiles.Clear();
            foreach (GitFileChange file in await filesTask.ConfigureAwait(true))
            {
                _allFiles.Add(file);
            }

            OnDetailLoaded();
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed mid-read; there is nobody left to tell.
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;

            OnPropertyChanged(nameof(CanShowDifference));
            ShowDifferenceCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Rebuilds <see cref="Files"/> from the unfiltered list.
    /// </summary>
    /// <remarks>
    /// The filter matches anywhere in the path and ignores case, so typing a folder name narrows the
    /// list to that folder's files without having to reproduce the folder's exact spelling.
    /// </remarks>
    private void ApplyFilter()
    {
        string needle = Filter.Trim();

        Files.Clear();

        foreach (GitFileChange file in _allFiles)
        {
            if (needle.Length == 0 || Matches(file, needle))
            {
                Files.Add(file);
            }
        }

        OnFilesChanged();
    }

    /// <summary>
    /// Whether a file passes the filter.
    /// </summary>
    /// <remarks>
    /// The old path is checked too, so a rename can be found by the name it had. This is
    /// <c>IndexOf</c> rather than <c>string.Contains(value, comparison)</c> because that overload
    /// does not exist on the framework this targets, and pulling in a newer base just for it would
    /// change what the application runs on.
    /// </remarks>
    private static bool Matches(GitFileChange file, string needle)
    {
        return file.Path.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
            || (file.OldPath != null
                && file.OldPath.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
