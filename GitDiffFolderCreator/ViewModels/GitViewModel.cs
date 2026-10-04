using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using GitDiffFolderCreator.Models;
using GitDiffFolderCreator.Services;

namespace GitDiffFolderCreator.ViewModels
{
    /// <summary>Presents the commit log, the file differences and the export controls.</summary>
    public sealed class GitViewModel : BindableBase
    {
        private const int DebounceMilliseconds = 400;

        private readonly AppSettingsStore _settingsStore;
        private readonly Func<string, string, string?> _pickFolder;
        private readonly Func<string, bool> _openFolder;
        private readonly Dispatcher _dispatcher;

        private CancellationTokenSource? _logCts;
        private CancellationTokenSource? _exportCts;

        /// <summary>Guards against a slow comparison overwriting the result of a newer selection.</summary>
        private int _selectionVersion;

        private CommitRange? _range;
        private string _gitDirectory = string.Empty;
        private string _outputDirectory = string.Empty;
    private string _diffToolCommand = string.Empty;
        private int _logLimit = 500;
        private string _status = "Set a git directory to begin.";
        private string _changeFilter = string.Empty;
        private GitRepositoryStatus _repositoryStatus = new GitRepositoryStatus();
        private string _statusFilter = AllStatusFilter;
        private bool _isResultExpanded = true;
        private bool _statusIsError;
        private string _outputLog = string.Empty;
        private bool _isBusy;
        private bool _isExporting;
        private bool _openOutputWhenFinished = true;
        private string _lastRunFolder = string.Empty;
        private ObservableCollection<GitCommit> _selectedCommits;

        public GitViewModel(
            AppSettingsStore? settingsStore,
            Func<string, string, string?>? pickFolder,
            Dispatcher? dispatcher,
            Func<string, bool>? openFolder = null)
        {
            _settingsStore = settingsStore ?? new AppSettingsStore(null);
            _pickFolder = pickFolder ?? ((input, title) => null);
            _openFolder = openFolder ?? ShellFolderOpener.Open;
            _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;

            Commits = new ObservableCollection<GitCommit>();
            FilteredCommits = new ObservableCollection<GitCommit>();
            Branches = new ObservableCollection<GitBranch>();
            FilteredBranches = new ObservableCollection<GitBranch>();
            Changes = new ObservableCollection<GitFileChange>();
            FilteredChanges = new ObservableCollection<GitFileChange>();
            _selectedCommits = new ObservableCollection<GitCommit>();
            _selectedCommits.CollectionChanged += SelectedCommitsOnCollectionChanged;

            Changes.CollectionChanged += (_, e) =>
            {
                // Ticking a file changes what will be exported, so the action bar totals and the
                // enabled state of the export button have to follow it.
                if (e.NewItems != null)
                {
                    foreach (GitFileChange change in e.NewItems)
                    {
                        change.PropertyChanged += FileChangeOnPropertyChanged;
                    }
                }

                if (e.OldItems != null)
                {
                    foreach (GitFileChange change in e.OldItems)
                    {
                        change.PropertyChanged -= FileChangeOnPropertyChanged;
                    }
                }

                // Held back while a whole list is being installed. ApplyFilter reads every file in
                // Changes and rebuilds FilteredChanges from scratch, so running it per added file is
                // quadratic in the number of changed files - and a comparison runs to thousands. The
                // installer filters once, when the list is finished.
                if (!_suppressChangeFilter)
                {
                    ApplyFilter();
                }
            };

            RefreshLogCommand = new RelayCommand(_ => ForgetRefresh());
            CreateFoldersCommand = new AsyncRelayCommand(_ => CreateFoldersAsync(), _ => CanCreate());
            CancelCommand = new RelayCommand(_ => CancelExport(), _ => CanCancel());
            BrowseGitDirectoryCommand = new RelayCommand(_ => BrowseForGitDirectory(), null);
            BrowseOutputDirectoryCommand = new RelayCommand(_ => BrowseForOutputDirectory(), null);
            OpenOutputFolderCommand = new RelayCommand(_ => OpenOutputFolder(), _ => CanOpenOutputFolder);
            SwapRangeCommand = new AsyncRelayCommand(_ => SwapRangeAsync(), _ => CanSwapRange);
            ToggleBranchListCommand = new RelayCommand(_ => ToggleBranchList(), _ => CanToggleBranchList);
            RetryRemoteCommand = new RelayCommand(_ => RetryRemoteBranches(), _ => BranchesMayBeStale);

            // Takes the double-clicked row as its parameter, so the list does not have to track a
            // selection for this: the row under the pointer is the one the user asked about.
            ShowDifferenceCommand = new RelayCommand(
                p => ShowDifferenceFor(p as GitFileChange),
                _ => _range != null && !IsExporting && !IsBusy);

            ToggleSelectionCommand = new RelayCommand(_ => ToggleSelection(), _ => Changes.Count > 0);
            SelectAllCommand = new RelayCommand(_ => SetAllIncluded(true), _ => CanSelectFiles);
            DeselectAllCommand = new RelayCommand(_ => SetAllIncluded(false), _ => CanSelectFiles);

            CreateFoldersCommand.ExecutionFailed += OnCommandFailed;

            AppSettings settings = _settingsStore.Load();
            _gitDirectory = settings.GitDirectory ?? string.Empty;
            _outputDirectory = settings.OutputDirectory ?? string.Empty;
            _logLimit = settings.LogLimit > 0 ? settings.LogLimit : 500;
            _openOutputWhenFinished = settings.OpenOutputWhenFinished;
            CollapsedSectionList = settings.CollapsedSections;
            _diffToolCommand = settings.DiffToolCommand ?? string.Empty;
            OnPropertyChanged("DiffToolCommand");
            OnPropertyChanged("DiffToolCaption");

            if (GitDirectory.Length > 0)
            {
                Forget(RefreshLogAsync());
            }
        }

        public ObservableCollection<GitCommit> Commits { get; private set; }

        /// <summary>
        /// The commits matching <see cref="CommitFilter"/>. Bound to the log list.
        /// </summary>
        /// <remarks>
        /// The list is narrowed rather than the log being re-read: the commits are already in memory, and
        /// searching should not spend a git call or wait on one.
        /// </remarks>
        public ObservableCollection<GitCommit> FilteredCommits { get; private set; }

        /// <summary>
        /// Text typed into the log's filter box. Matched case-insensitively against the message, hash,
        /// author and refs.
        /// </summary>
        public string CommitFilter
        {
            get { return _commitFilter; }
            set
            {
                if (SetProperty(ref _commitFilter, value ?? string.Empty))
                {
                    ApplyCommitFilter();
                }
            }
        }

        private string _commitFilter = string.Empty;

        /// <summary>
        /// How the log reports its own filtering, so a list narrowed to nothing is distinguishable from a
        /// repository with nothing in it.
        /// </summary>
        public string CommitFilterSummary
        {
            get
            {
                if (Commits.Count == 0)
                {
                    return string.Empty;
                }

                if (FilteredCommits.Count == Commits.Count)
                {
                    return string.Format("{0} commit(s)", Commits.Count);
                }

                return string.Format("{0} of {1} commits match", FilteredCommits.Count, Commits.Count);
            }
        }

        /// <summary>
        /// Keeps <see cref="FilteredCommits"/> in step with the filter.
        /// </summary>
        /// <remarks>
        /// Runs against the full list rather than filtering what is already shown, so widening the filter
        /// restores the commits it had hidden instead of leaving the list half-built.
        /// </remarks>
        private void ApplyCommitFilter()
        {
            string needle = (_commitFilter ?? string.Empty).Trim();

            List<GitCommit> matches = new List<GitCommit>();

            foreach (GitCommit commit in Commits)
            {
                if (needle.Length == 0 || CommitMatches(commit, needle))
                {
                    matches.Add(commit);
                }
            }

            // Rebuilt rather than added to or removed from, so the selection cannot survive a change of
            // filter and leave the window describing commits the list is not showing.
            FilteredCommits.Clear();

            foreach (GitCommit commit in matches)
            {
                FilteredCommits.Add(commit);
            }

            // A commit the filter has hidden cannot stay selected: picking it again is no longer possible,
            // and leaving it would compare something the user can no longer see.
            foreach (GitCommit selected in SelectedCommits.ToList())
            {
                if (!matches.Contains(selected))
                {
                    SelectedCommits.Remove(selected);
                }
            }

            OnPropertyChanged("CommitFilterSummary");
        }

        /// <summary>
        /// Whether one commit carries the needle anywhere a reader would look for it.
        /// </summary>
        /// <remarks>
        /// The message is the reason to search, but a hash is what people paste in from elsewhere and an
        /// author or a ref is what narrows a busy log. Each term has to sit inside one field rather than
        /// the row as a whole, so that a hash does not match by appearing in a commit that mentions it.
        /// </remarks>
        internal static bool CommitMatches(GitCommit commit, string needle)
        {
            if (commit == null)
            {
                return false;
            }

            return Contains(commit.Message, needle)
                || Contains(commit.Hash, needle)
                || Contains(commit.ShortHash, needle)
                || Contains(commit.Author, needle)
                || Contains(commit.RefNames, needle);
        }

        private static bool Contains(string? value, string needle) =>
            !string.IsNullOrEmpty(value)
            && value!.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        public ObservableCollection<GitFileChange> Changes { get; private set; }

        /// <summary>The subset of <see cref="Changes"/> matching <see cref="ChangeFilter"/>.</summary>
        public ObservableCollection<GitFileChange> FilteredChanges { get; private set; }

        /// <summary>
        /// Substring filter over the changed-file paths and status codes. Empty shows everything.
        /// </summary>
        public string ChangeFilter
        {
            get { return _changeFilter; }
            set
            {
                if (SetProperty(ref _changeFilter, value ?? string.Empty))
                {
                    ApplyFilter();
                }
            }
        }

        /// <summary>When set, only files with this status code are listed, e.g. <c>D</c>.</summary>
        public string StatusFilter
        {
            get { return _statusFilter; }
            set
            {
                if (SetProperty(ref _statusFilter, value ?? string.Empty))
                {
                    ApplyFilter();
                }
            }
        }

        /// <summary>Describes how many files are listed versus how many changed.</summary>
        public string FilterSummary
        {
            get
            {
                if (FilteredChanges.Count == Changes.Count)
                {
                    return Changes.Count == 1 ? "1 file" : string.Format("{0} files", Changes.Count);
                }

                return string.Format("{0} of {1} files", FilteredChanges.Count, Changes.Count);
            }
        }

        /// <summary>Combo box entry meaning "do not filter by status".</summary>
        internal const string AllStatusFilter = "All";

        /// <summary>One option per status code, labelled for the combo box.</summary>
        internal static readonly string[] AllStatusFilterOptions =
        {
            AllStatusFilter,
            "A added",
            "M modified",
            "D deleted",
            "R renamed",
            "C copied",
            "T type changed",
        };

        /// <summary>Lists of one status each, for the status filter combo box.</summary>
        public IReadOnlyList<string> StatusFilterOptions { get; } = AllStatusFilterOptions;

        /// <summary>
        /// Two-way bound to the commit list selection. The list writes into this collection
        /// directly, so changes to its contents are observed rather than relying on property
        /// replacement.
        /// </summary>
        public ObservableCollection<GitCommit> SelectedCommits
        {
            get { return _selectedCommits; }
            set
            {
                if (value == null || ReferenceEquals(_selectedCommits, value))
                {
                    return;
                }

                _selectedCommits.CollectionChanged -= SelectedCommitsOnCollectionChanged;
                if (SetProperty(ref _selectedCommits, value))
                {
                    _selectedCommits.CollectionChanged += SelectedCommitsOnCollectionChanged;
                    SelectionUpdated();
                }
            }
        }

        public string GitDirectory
        {
            get { return _gitDirectory; }
            set
            {
                if (SetProperty(ref _gitDirectory, value ?? string.Empty))
                {
                    // Everything on screen describes one repository, so all of it goes before anything
                    // new arrives. Leaving the previous repository's commits, branches or files up
                    // while the next one is still being read is how a picker ends up offering branches
                    // that do not exist in the repository now open.
                    ClearRepositoryState();
                    Forget(RefreshLogAsync());
                }
            }
        }

        /// <summary>
        /// Drops every value that came from the previous repository, so nothing can be mistaken for the
        /// new one.
        /// </summary>
        private void ClearRepositoryState()
        {
            Commits.Clear();
            FilteredCommits.Clear();
            Branches.Clear();
            FilteredBranches.Clear();
            Changes.Clear();
            SelectedCommits.Clear();

            // Both filters, for the same reason: one left over from the last repository would hide the
            // commits or branches of this one and look like they had gone missing.
            CommitFilter = string.Empty;
            BranchFilter = string.Empty;
            PendingBranch = null;
            SelectedBranch = null;
            _lastFetchedRoot = null;

            // The previous repository's remote said nothing about this one, so a warning about it would
            // be pointing at branches that are no longer on screen.
            BranchesMayBeStale = false;

            SetRange(null);
            RepositoryStatus = new GitRepositoryStatus { BranchName = string.Empty };

            OnPropertyChanged("HasBranches");
            OnPropertyChanged("SelectionSummary");
            OnPropertyChanged("CommitFilterSummary");
            OnPropertyChanged("BranchFilterSummary");
            NotifyActiveBranchChanged();
            UpdateCommandStates();
        }

        public string OutputDirectory
        {
            get { return _outputDirectory; }
            set
            {
                if (SetProperty(ref _outputDirectory, value ?? string.Empty))
                {
                    UpdateCommandStates();
                }
            }
        }

        public int LogLimit
        {
            get { return _logLimit; }
            set
            {
                if (SetProperty(ref _logLimit, value) && value > 0)
                {
                    Forget(RefreshLogAsync());
                }
            }
        }

        /// <summary>Always-visible single line describing the last outcome.</summary>
        public string Status
        {
            get { return _status; }
            private set { SetProperty(ref _status, value); }
        }

        /// <summary>Drives the status colour: red for a failure, neutral otherwise.</summary>
        public bool IsStatusError
        {
            get { return _statusIsError; }
            private set { SetProperty(ref _statusIsError, value); }
        }

        /// <summary>Multi-line report of the last export.</summary>
        public string OutputLog
        {
            get { return _outputLog; }
            private set
            {
                if (SetProperty(ref _outputLog, value))
                {
                    // The log area only exists once there is a log, so this is what reveals it.
                    OnPropertyChanged("HasOutputLog");
                }
            }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    UpdateCommandStates();
                }
            }
        }

        public bool IsExporting
        {
            get { return _isExporting; }
            private set
            {
                if (SetProperty(ref _isExporting, value))
                {
                    UpdateCommandStates();
                }
            }
        }

        /// <summary>
        /// True while the commit log is being read, which is the one wait long enough to need a
        /// progress indicator: the repository root, the remote fetch, and the log itself are all
        /// separate git processes. Separate from <see cref="IsBusy"/> so the ring is never shown for
        /// work happening elsewhere.
        /// </summary>
        public bool IsLoadingLog
        {
            get { return _isLoadingLog; }
            private set { SetProperty(ref _isLoadingLog, value); }
        }

        private bool _isLoadingLog;

        /// <summary>
        /// How far through the export the bar is, from 0 to 1.
        /// </summary>
        /// <remarks>
        /// Bound to the fill column's star width, so the bar grows with the window instead of being a
        /// fixed number of pixels that stops matching its track. Zero while the total is unknown, which
        /// is also when the bar animates instead of claiming a figure it does not have.
        /// </remarks>
        public double ExportProgressFraction
        {
            get { return _exportProgressFraction; }
            private set
            {
                if (SetProperty(ref _exportProgressFraction, value))
                {
                    OnPropertyChanged(nameof(ExportProgressRemainder));
                }
            }
        }

        private double _exportProgressFraction;

        /// <summary>
        /// The unfilled part of the track, so the two star columns always add up to one.
        /// </summary>
        /// <remarks>
        /// Carried as a property rather than inverted in markup so there is one number in the view
        /// model that a test can assert on; two columns bound to the same fraction would drift apart
        /// the moment either was rounded.
        /// </remarks>
        public double ExportProgressRemainder => 1d - ExportProgressFraction;

        /// <summary>
        /// False while the export has not yet worked out how much there is to do, which is the case
        /// for the <c>git diff</c> that produces the file list.
        /// </summary>
        public bool IsExportProgressKnown
        {
            get { return _isExportProgressKnown; }
            private set { SetProperty(ref _isExportProgressKnown, value); }
        }

        private bool _isExportProgressKnown;

        /// <summary>
        /// The count behind the bar, shown next to it: how many files are written out of how many.
        /// </summary>
        public string ExportProgressCaption
        {
            get { return _exportProgressCaption; }
            private set { SetProperty(ref _exportProgressCaption, value); }
        }

        private string _exportProgressCaption = string.Empty;

        /// <summary>
        /// Applies one report from the exporter, on the UI thread.
        /// </summary>
        /// <remarks>
        /// Reports are dropped when they would not move the bar by a whole percent. An export of a few
        /// thousand files reports once per file, and posting each one to the dispatcher would put more
        /// work on the UI thread than the export itself does - the bar would then lag behind the work
        /// it is describing, which is worse than a bar that updates in whole percents.
        /// </remarks>
        private void ApplyExportProgress(ExportProgress report)
        {
            // Progress<T> posts its callbacks, so one can still be queued when the export returns and
            // the outcome is written to the status line. Letting it through would overwrite "Folders
            // created." with a stage message from work that has already finished.
            if (_exportProgressClosed)
            {
                return;
            }

            SetStatus(report.Message, false);

            if (!report.IsDeterminate)
            {
                // Before the total is known the bar animates. Any fraction carried over from a
                // previous run would show as a stale reading, so it is cleared.
                IsExportProgressKnown = false;
                ExportProgressFraction = 0d;
                ExportProgressCaption = report.Message;
                return;
            }

            int percent = (int)(report.Fraction * 100d);

            if (percent == _lastExportPercent)
            {
                return;
            }

            _lastExportPercent = percent;
            IsExportProgressKnown = true;
            ExportProgressFraction = report.Fraction;
            ExportProgressCaption = string.Format(
                "{0} of {1} file(s)  —  {2}", report.Completed, report.Total, report.Message);
        }

        private int _lastExportPercent = -1;

        private bool _exportProgressClosed;

        /// <summary>Puts the bar back to its start-of-run state.</summary>
        private void ResetExportProgress()
        {
            _lastExportPercent = -1;
            _exportProgressClosed = false;
            IsExportProgressKnown = false;
            ExportProgressFraction = 0d;
            ExportProgressCaption = string.Empty;
        }

        /// <summary>
        /// What the log load is doing right now, shown beside the ring. Stages are named because
        /// <c>git fetch</c> is the one step that touches the network and is usually the reason a
        /// first load of a large repository feels slow.
        /// </summary>
        public string LoadingMessage
        {
            get { return _loadingMessage; }
            private set { SetProperty(ref _loadingMessage, value); }
        }

        private string _loadingMessage = string.Empty;

        public bool CanCreateFolders =>
            _range != null && !IsExporting && !IsBusy && !string.IsNullOrWhiteSpace(OutputDirectory)
            && ExcludedCount < Changes.Count;

        /// <summary>
        /// True when there is a folder to open: the last run folder if one exists, otherwise the
        /// configured output directory.
        /// </summary>
        public bool CanOpenOutputFolder =>
            !IsExporting
            && !(string.IsNullOrWhiteSpace(_lastRunFolder) && string.IsNullOrWhiteSpace(OutputDirectory));

        /// <summary>
        /// What the Open button will open, in the button's own words.
        /// </summary>
        /// <remarks>
        /// Two folders, chosen by whether a run has happened, and the caption has to say which: the
        /// reader otherwise has to infer it from whether the run log has anything in it. Naming the
        /// result rather than the number keeps it stable across runs - "the last result" is as true
        /// after the fifth run as after the first.
        /// </remarks>
        public string OpenOutputCaption =>
            string.IsNullOrWhiteSpace(_lastRunFolder) ? "Open output folder" : "Open last result";

        /// <summary>The same distinction in the tooltip, with the folder each one means.</summary>
        public string OpenOutputTooltip =>
            string.IsNullOrWhiteSpace(_lastRunFolder)
                ? "Open the output folder in File Explorer. No export has run yet."
                : "Open the folder the last export produced in File Explorer.";

        /// <summary>
        /// The heading above the file list: which two commits it describes, as a compact arrow.
        /// </summary>
        public string RangeCaption =>
            _range == null
                ? "Changed Files"
                : string.Format("{0}  \u2192  {1}", _range.Base.ShortHash, _range.Modified.ShortHash);

        /// <summary>
        /// Whether the export can be run in the opposite direction. Ancestry normally decides the
        /// direction, but a manual comparison sometimes needs the other way round.
        /// </summary>
        public bool CanSwapRange => _range != null && !IsExporting && !IsBusy;

        /// <summary>
        /// The one-line summary in the action bar: how many paths are included, and the line totals.
        /// </summary>
        public string SelectionSummary
        {
            get
            {
                if (Changes.Count == 0)
                {
                    return _range == null
                        ? "No commits selected."
                        : "No changed paths.";
                }

                int included = 0;
                foreach (GitFileChange change in Changes)
                {
                    if (change.IsIncluded)
                    {
                        included++;
                    }
                }

                return string.Format(
                    "{0} of {1} file(s)  \u00b7  +{2}  -{3}{4}",
                    included,
                    Changes.Count,
                    Total(Changes, c => c.AddedLines, onlyIncluded: true),
                    Total(Changes, c => c.DeletedLines, onlyIncluded: true),
                    included == Changes.Count ? string.Empty : "  \u00b7  excluded: " + (Changes.Count - included));
            }
        }

        /// <summary>
        /// True once a run has produced a log, which is what the collapsible log area hangs off. The
        /// area stays out of the way entirely until there is something in it.
        /// </summary>
        public bool HasOutputLog => !string.IsNullOrWhiteSpace(OutputLog);

        public RelayCommand RefreshLogCommand { get; private set; }

        public AsyncRelayCommand CreateFoldersCommand { get; private set; }

        public RelayCommand CancelCommand { get; private set; }

        public RelayCommand BrowseGitDirectoryCommand { get; private set; }

        public RelayCommand BrowseOutputDirectoryCommand { get; private set; }

        public RelayCommand OpenOutputFolderCommand { get; private set; }

        public AsyncRelayCommand SwapRangeCommand { get; private set; }

        public RelayCommand ToggleBranchListCommand { get; private set; }

        /// <summary>
        /// Tries the remote again after a fetch that could not reach it. Bound to the notice in the
        /// branch picker, and disabled when there is nothing to retry.
        /// </summary>
        public RelayCommand RetryRemoteCommand { get; private set; }

        /// <summary>
        /// Opens a double-clicked changed file's two versions in the diff tool git is configured to use.
        /// </summary>
        public RelayCommand ShowDifferenceCommand { get; private set; }

        /// <summary>Shows or hides the per-file check boxes.</summary>
        public RelayCommand ToggleSelectionCommand { get; private set; }

        /// <summary>Ticks every file, so the whole range is exported.</summary>
        public RelayCommand SelectAllCommand { get; private set; }

        /// <summary>Unticks every file, leaving the export with nothing to write.</summary>
        public RelayCommand DeselectAllCommand { get; private set; }

        /// <summary>
        /// Whether the per-file check boxes are on screen.
        /// </summary>
        /// <remarks>
        /// Off to begin with, because the common case is exporting the whole range and a column of
        /// empty check boxes is a control the user has to look past on every row. Turning it on is
        /// for the case where only some files are wanted, and the buttons beside it exist so that
        /// case does not need five hundred separate clicks.
        /// <para>
        /// Not persisted. It describes what the user is doing right now rather than a preference,
        /// and a window that reopened with every file unticked because the last session ended on a
        /// "none" would be exporting nothing without saying so.
        /// </para>
        /// </remarks>
        public bool IsSelectionEnabled
        {
            get { return _isSelectionEnabled; }
            private set
            {
                if (SetProperty(ref _isSelectionEnabled, value))
                {
                    OnPropertyChanged(nameof(CanSelectFiles));
                    UpdateCommandStates();
                }
            }
        }

        private bool _isSelectionEnabled;

        /// <summary>True while there are files to choose between.</summary>
        public bool CanSelectFiles => IsSelectionEnabled && Changes.Count > 0;

        /// <summary>
        /// Whether the branch picker is open. The picker is a popup the user opens by clicking the
        /// branch name, rather than a control that is always on screen.
        /// </summary>
        public bool IsBranchListOpen
        {
            get { return _isBranchListOpen; }
            private set { SetProperty(ref _isBranchListOpen, value); }
        }

        private bool _isBranchListOpen;

        /// <summary>True once at least one branch has been found to offer.</summary>
        public bool HasBranches => Branches.Count > 0;

        /// <summary>False when there is nothing to choose from, or while the repository is being read.</summary>
        public bool CanToggleBranchList => !IsBusy && Branches.Count > 0;

        private void ToggleBranchList()
        {
            if (!CanToggleBranchList)
            {
                return;
            }

            if (IsBranchListOpen)
            {
                CloseBranchList();
                return;
            }

            // A filter left over from last time would hide the branch the user has just chosen and
            // look like the list lost it.
            BranchFilter = string.Empty;

            // Preselect before opening. The PendingBranch setter takes the branch immediately whenever
            // the picker is already open, so setting it afterwards would open and close in one step.
            PendingBranch = SelectedBranch ?? CurrentBranch();

            IsBranchListOpen = true;
        }

        /// <summary>Closes the picker and clears what was typed into it.</summary>
        public void CloseBranchList()
        {
            IsBranchListOpen = false;
            BranchFilter = string.Empty;
        }

        /// <summary>
        /// The entry for the branch the working tree is on, so opening the picker on HEAD highlights
        /// where the log is currently coming from.
        /// </summary>
        private GitBranch? CurrentBranch()
        {
            foreach (GitBranch branch in Branches)
            {
                if (branch.IsCurrent)
                {
                    return branch;
                }
            }

            return null;
        }

        /// <summary>
        /// Reads the picked branch and reloads the log from it. Nothing is checked out: every later
        /// step names a hash, so the working tree keeps whatever it had.
        /// </summary>
        private void ChooseBranch()
        {
            if (Branches.Count == 0 || IsBusy)
            {
                return;
            }

            IsBranchListOpen = false;

            // Re-reading the same branch would throw away the user's selection for nothing.
            if (ReferenceEquals(SelectedBranch, PendingBranch))
            {
                return;
            }

            SelectedBranch = PendingBranch;
            SelectionUpdated();
            ForgetRefresh();
        }

        /// <summary>
        /// The row the picker has highlighted. Choosing a row while the picker is open takes it
        /// straight away, which is why the setter acts rather than waiting for a separate confirm.
        /// </summary>
        public GitBranch? PendingBranch
        {
            get { return _pendingBranch; }
            set
            {
                if (!SetProperty(ref _pendingBranch, value))
                {
                    return;
                }

                // Keyboard navigation moves the highlight without committing to it, so it sets the
                // flag rather than having the setter re-read the log on every arrow press.
                if (IsBranchListOpen && value != null && !_pendingBranchIsNavigating)
                {
                    ChooseBranch();
                }
            }
        }

        private GitBranch? _pendingBranch;

        private bool _pendingBranchIsNavigating;

        /// <summary>
        /// Moves the highlighted row by <paramref name="offset"/> places within
        /// <see cref="FilteredBranches"/>, so the filter box can drive the list from the keyboard.
        /// </summary>
        /// <remarks>
        /// Navigation only moves the highlight. Enter commits it, which keeps arrowing through a long
        /// list from re-reading the log once per row.
        /// </remarks>
        public void MoveBranchHighlight(int offset)
        {
            if (FilteredBranches.Count == 0 || offset == 0)
            {
                return;
            }

            int index = PendingBranch == null ? -1 : FilteredBranches.IndexOf(PendingBranch);

            // From nothing selected, an upward press belongs at the bottom rather than staying put.
            if (index < 0)
            {
                index = offset > 0 ? 0 : FilteredBranches.Count - 1;
            }
            else
            {
                index += offset;
            }

            if (index < 0 || index >= FilteredBranches.Count)
            {
                return;
            }

            _pendingBranchIsNavigating = true;
            try
            {
                PendingBranch = FilteredBranches[index];
            }
            finally
            {
                _pendingBranchIsNavigating = false;
            }
        }

        /// <summary>
        /// Takes the highlighted branch, if the picker is open and has something to take.
        /// </summary>
        public void ConfirmPendingBranch()
        {
            if (IsBranchListOpen && PendingBranch != null)
            {
                ChooseBranch();
            }
        }

        /// <summary>Opens the run folder in File Explorer once the export finishes.</summary>
        public bool OpenOutputWhenFinished
        {
            get { return _openOutputWhenFinished; }
            set
            {
                if (SetProperty(ref _openOutputWhenFinished, value))
                {
                    UpdateCommandStates();
                }
            }
        }

        /// <summary>Current branch and sync state, refreshed with the log.</summary>
        public GitRepositoryStatus RepositoryStatus
        {
            get { return _repositoryStatus; }
            private set { SetProperty(ref _repositoryStatus, value); }
        }

        /// <summary>
        /// The ref whose log is being shown. <c>null</c> means HEAD, which is what the window shows
        /// until the user picks a branch; naming the ref is what lets any branch be read without
        /// checking it out.
        /// </summary>
        private string? _activeRef;

        /// <summary>The repository root whose remote-tracking refs have already been refreshed.</summary>
        private string? _lastFetchedRoot;

        /// <summary>
        /// Whether the branches on screen may be behind the remote.
        /// </summary>
        /// <remarks>
        /// Set when a fetch was attempted and could not reach the remote, and cleared by a fetch that
        /// works, by a repository with no remote at all, and by switching repository. Nothing is stale
        /// about a repository that has no remote, so it must not raise this - that is the whole reason
        /// the two cases are told apart.
        /// </remarks>
        public bool BranchesMayBeStale
        {
            get { return _branchesMayBeStale; }
            private set
            {
                if (SetProperty(ref _branchesMayBeStale, value))
                {
                    OnPropertyChanged("BranchStaleNotice");
                    OnPropertyChanged("ActiveBranchTooltip");
                    UpdateCommandStates();
                }
            }
        }

        /// <summary>
        /// What is wrong with the branch list, in words, for the branch picker to show above it.
        /// </summary>
        /// <remarks>
        /// A sentence rather than a badge, because the reader's question is not "is there a warning" but
        /// "are these the branches I think they are" - and the honest answer has to include that they
        /// are the ones on disk, which is still useful.
        /// </remarks>
        public string BranchStaleNotice =>
            BranchesMayBeStale
                ? "Could not reach the remote. These branches are the ones on disk, as of the last fetch that worked."
                : string.Empty;

        private bool _branchesMayBeStale;

        /// <summary>
        /// Tries the remote again, having forgotten that this session already tried.
        /// </summary>
        /// <remarks>
        /// The repository is forgotten rather than the flag cleared, because the reason the retry did
        /// not happen in the first place is that the root had already been fetched once. Clearing the
        /// notice without clearing that would leave the button pressing and nothing happening.
        /// </remarks>
        internal void RetryRemoteBranches()
        {
            _lastFetchedRoot = null;
            BranchesMayBeStale = false;

            ForgetRefresh();
        }

        /// <summary>
        /// Every local and remote-tracking branch, newest first. Bound to the branch picker.
        /// </summary>
        public ObservableCollection<GitBranch> Branches { get; private set; }

        /// <summary>
        /// The branches matching <see cref="BranchFilter"/>. A repository can hold hundreds, so the
        /// picker narrows by name rather than asking the user to scroll to what they want.
        /// </summary>
        public ObservableCollection<GitBranch> FilteredBranches { get; private set; }

        /// <summary>
        /// Text typed into the picker's filter box. Matched case-insensitively against the branch name
        /// and, since that is what people actually remember about a remote, the remote name too.
        /// </summary>
        public string BranchFilter
        {
            get { return _branchFilter; }
            set
            {
                if (SetProperty(ref _branchFilter, value ?? string.Empty))
                {
                    ApplyBranchFilter();
                }
            }
        }

        private string _branchFilter = string.Empty;

        /// <summary>
        /// How the picker reports its own filtering, so an empty list is distinguishable from a
        /// misspelled filter.
        /// </summary>
        public string BranchFilterSummary
        {
            get
            {
                if (Branches.Count == 0)
                {
                    return "No branches found.";
                }

                if (FilteredBranches.Count == Branches.Count)
                {
                    return string.Format("{0} branch(es)", Branches.Count);
                }

                return string.Format("{0} of {1} match", FilteredBranches.Count, Branches.Count);
            }
        }

        /// <summary>
        /// Keeps <see cref="FilteredBranches"/> in step with the filter.
        /// </summary>
        /// <remarks>
        /// Runs against the full list rather than filtering what is already shown, so widening the
        /// filter restores the branches it had hidden instead of leaving the list half-built.
        /// </remarks>
        private void ApplyBranchFilter()
        {
            string needle = (BranchFilter ?? string.Empty).Trim();

            List<GitBranch> matches = new List<GitBranch>();

            foreach (GitBranch branch in Branches)
            {
                if (needle.Length == 0
                    || branch.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matches.Add(branch);
                }
            }

            // The list is rebuilt rather than added to or removed from, so a stale highlight cannot
            // survive a change of filter.
            FilteredBranches.Clear();
            foreach (GitBranch branch in matches)
            {
                FilteredBranches.Add(branch);
            }

            // A branch that the filter has hidden cannot stay selected: choosing it is no longer
            // possible, and leaving it would describe a branch the list does not show.
            if (PendingBranch != null && !matches.Contains(PendingBranch))
            {
                PendingBranch = null;
            }

            OnPropertyChanged("BranchFilterSummary");
        }

        /// <summary>
        /// The branch the log is being read from, or <c>null</c> while it is still HEAD.
        /// </summary>
        public GitBranch? SelectedBranch
        {
            get { return _selectedBranch; }
            set
            {
                if (!SetProperty(ref _selectedBranch, value))
                {
                    return;
                }

                _activeRef = value?.RefName;
                OnPropertyChanged("ActiveRef");
                OnPropertyChanged("ActiveRefName");
                OnPropertyChanged("IsShowingHead");
                OnPropertyChanged("RangeCaption");
                NotifyActiveBranchChanged();
            }
        }

        /// <summary>
        /// Republishes everything the branch badge binds to. The badge describes whichever branch is on
        /// screen, so it has to follow both a change of branch and a refreshed list of branches.
        /// </summary>
        private void NotifyActiveBranchChanged()
        {
            OnPropertyChanged("ActiveBranch");
            OnPropertyChanged("HasActiveBranch");
            OnPropertyChanged("ActiveBranchName");
            OnPropertyChanged("ActiveBranchSyncText");
            OnPropertyChanged("ActiveBranchDetail");
            OnPropertyChanged("IsActiveBranchOutOfSync");
            OnPropertyChanged("IsActiveBranchInSync");
            OnPropertyChanged("IsActiveBranchUntracked");
        }

        private GitBranch? _selectedBranch;

        /// <summary>The ref to read the log from, or <c>null</c> for HEAD.</summary>
        public string? ActiveRef => _activeRef;

        /// <summary>The name of the ref being shown, for the status line and the picker heading.</summary>
        public string ActiveRefName => _selectedBranch?.Name ?? "HEAD";

        /// <summary>
        /// True while the log is the checked-out branch, which is the default state and is worth
        /// stating because picking a branch changes it.
        /// </summary>
        public bool IsShowingHead => _activeRef == null;

        /// <summary>
        /// The branch the badge describes: the one whose log is on screen, or the checked-out one while
        /// HEAD is being read. Null before any repository has loaded.
        /// </summary>
        public GitBranch? ActiveBranch => _selectedBranch ?? CurrentBranch();

        /// <summary>True once a branch is known, so the badge has a name to show.</summary>
        public bool HasActiveBranch => ActiveBranch != null;

        /// <summary>
        /// The name on the badge. It follows the branch being read rather than the checkout, because a
        /// sync warning about a branch nobody is looking at is noise.
        /// </summary>
        public string ActiveBranchName
        {
            get
            {
                GitBranch? branch = ActiveBranch;

                // Nothing loaded yet: the checked-out branch from git status is all that is known.
                return branch != null ? branch.Name : RepositoryStatus.BranchName;
            }
        }

        /// <summary>True when the branch on screen disagrees with its upstream, in either direction.</summary>
        public bool IsActiveBranchOutOfSync => ActiveBranch?.IsOutOfSync ?? false;

        /// <summary>
        /// True when the branch on screen tracks an upstream and is level with it. Kept as a separate
        /// value so the badge holds its width as the warning appears and goes away.
        /// </summary>
        public bool IsActiveBranchInSync =>
            ActiveBranch != null && ActiveBranch.HasUpstream && !ActiveBranch.IsOutOfSync;

        /// <summary>
        /// True when there is no sync state to show at all, so the badge falls back to the bare name.
        /// That is a branch with no upstream, which can be neither in sync nor out of sync, and the empty
        /// state before anything is loaded. The other two rules both need an upstream, so without this
        /// one an untracked branch would match no rule and its name would disappear entirely.
        /// </summary>
        public bool IsActiveBranchUntracked =>
            ActiveBranch == null || !ActiveBranch.HasUpstream;

        /// <summary>
        /// The divergence shown beside the name, e.g. <c>+2 -1</c>. Empty when level, so the ordinary
        /// case draws nothing.
        /// </summary>
        public string ActiveBranchSyncText => ActiveBranch?.SyncText ?? string.Empty;

        /// <summary>
        /// The full explanation, in words, for the badge's tooltip: what the numbers mean and where
        /// they are measured against.
        /// </summary>
        public string ActiveBranchDetail
        {
            get
            {
                GitBranch? branch = ActiveBranch;
                if (branch == null)
                {
                    return string.Empty;
                }

                string context = branch.IsCurrent
                    ? "Checked out and on screen."
                    : string.Format("On screen, but not checked out. HEAD is on {0}.", CurrentBranchName());

                if (!branch.HasUpstream)
                {
                    return context + " No upstream is configured, so there is nothing to compare against.";
                }

                if (!branch.IsOutOfSync)
                {
                    return string.Format("{0} Level with {1}.", context, branch.UpstreamName);
                }

                return string.Format(
                    "{0} {1} ahead of and {2} behind {3}.",
                    context,
                    branch.AheadCount,
                    branch.BehindCount,
                    branch.UpstreamName);
            }
        }

        /// <summary>
        /// <see cref="ActiveBranchDetail"/> with the remote's reachability appended, so hovering the
        /// badge is enough to learn that the branch list could not be refreshed.
        /// </summary>
        /// <remarks>
        /// The badge is where the branch is named, and the tooltip is the only place a badge this size
        /// can carry a sentence. It matters because every number in the tooltip is measured against an
        /// upstream ref that may itself be out of date: a branch can read "level" here and be behind
        /// the remote, and nothing else on screen would say so.
        /// </remarks>
        public string ActiveBranchTooltip
        {
            get
            {
                string detail = ActiveBranchDetail;

                if (!BranchesMayBeStale)
                {
                    return detail;
                }

                return detail.Length == 0
                    ? BranchStaleNotice
                    : detail + " " + BranchStaleNotice;
            }
        }

        private string CurrentBranchName()
        {
            GitBranch? branch = CurrentBranch();
            return branch != null ? branch.Name : "another branch";
        }

        /// <summary>
        /// Replaces the branch list and keeps the current selection only if it is still present.
        /// </summary>
        private void ApplyBranches(IList<GitBranch> branches)
        {
            GitBranch? previous = _selectedBranch;

            Branches.Clear();
            foreach (GitBranch branch in branches)
            {
                Branches.Add(branch);
            }

            if (previous != null)
            {
                GitBranch? match = null;
                foreach (GitBranch branch in Branches)
                {
                    if (string.Equals(branch.RefName, previous.RefName, StringComparison.Ordinal))
                    {
                        match = branch;
                        break;
                    }
                }

                // A branch that has gone away must not leave the log reading a ref that no longer
                // resolves, so the selection falls back to HEAD.
                SelectedBranch = match;
            }

            // A freshly fetched list carries new sync state, so the badge has to redraw.
            NotifyActiveBranchChanged();
            ApplyBranchFilter();

            OnPropertyChanged("HasBranches");

            // The picker opens only when there are branches to choose from, so a change to the list
            // has to reach the command: WPF polls CanExecute once and then keeps the answer until the
            // command says otherwise, which leaves the badge disabled if the first poll happened
            // while the list was still empty.
            UpdateCommandStates();
        }

        /// <summary>Uncommitted work description, empty when the working tree is clean.</summary>
        public string WorkingTreeText => _repositoryStatus.WorkingTreeText;

        public bool IsResultExpanded
        {
            get { return _isResultExpanded; }
            set { SetProperty(ref _isResultExpanded, value); }
        }

        /// <summary>Names of the panels the user has folded away, for the settings file.</summary>
        private string CollapsedSectionList
        {
            get
            {
                List<string> collapsed = new List<string>();
                if (!IsResultExpanded) { collapsed.Add("result"); }

                return string.Join(",", collapsed.ToArray());
            }

            set
            {
                List<string> collapsed = new List<string>();
                foreach (string name in (value ?? string.Empty).Split(','))
                {
                    collapsed.Add(name.Trim());
                }

                // Names of panels that no longer fold are still read and ignored, so an older
                // settings file keeps working instead of losing the log's state with them.
                _isResultExpanded = !collapsed.Contains("result");
            }
        }

        public void SaveSettings()
        {
            // Read first and overwrite only what this window owns. The change-document markers are
            // written by another window out of the same file, and a file rebuilt from these six fields
            // alone would drop them every time this one closed.
            AppSettings settings = _settingsStore.Load();

            settings.GitDirectory = GitDirectory;
            settings.OutputDirectory = OutputDirectory;
            settings.LogLimit = LogLimit;
            settings.OpenOutputWhenFinished = OpenOutputWhenFinished;
            settings.CollapsedSections = CollapsedSectionList;
            settings.DiffToolCommand = DiffToolCommand;

            _settingsStore.Save(settings);
        }

        /// <summary>
        /// The diff tool chosen in the picker, or empty to use the one configured for git.
        /// </summary>
        public string DiffToolCommand
        {
            get { return _diffToolCommand; }
            private set
            {
                if (SetProperty(ref _diffToolCommand, value ?? string.Empty))
                {
                    OnPropertyChanged("DiffToolCaption");
                }
            }
        }

        /// <summary>
        /// What the diff tool button says: the chosen tool's name, or that git's own is in use.
        /// </summary>
        public string DiffToolCaption =>
            DiffToolCommand.Length > 0
                ? string.Format("Diff tool: {0}", ProgramNameOf(DiffToolCommand))
                : "Diff tool: git's";

        /// <summary>
        /// Records a tool chosen in the picker, or clears the choice to fall back to git's.
        /// </summary>
        /// <remarks>
        /// Saved at once rather than left to <see cref="SaveSettings"/> on window close, so the choice
        /// survives a crash and is not lost when the dialog is dismissed by shutting the window.
        /// </remarks>
        public void SetDiffToolCommand(string? command)
        {
            DiffToolCommand = command ?? string.Empty;

            try
            {
                AppSettings settings = _settingsStore.Load();
                settings.DiffToolCommand = DiffToolCommand;
                _settingsStore.Save(settings);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A settings file that cannot be written is not worth refusing the choice over: it
                // still applies for this session, and is reported by the next save attempt failing.
            }

            SetStatus(
                DiffToolCommand.Length > 0
                    ? "Double-clicking a changed file now opens it in " + ProgramNameOf(DiffToolCommand) + "."
                    : "Double-clicking a changed file now uses the diff tool configured for git.",
                false);
        }

        /// <summary>
        /// The display name of a command line's program, for the caption and the status line.
        /// </summary>
        /// <remarks>
        /// The quotes come off before the file name is taken, because <c>Path</c> does not strip them:
        /// on a quoted "C:\Program Files\...\BCompare.exe" it would otherwise yield "Program" rather
        /// than the tool's name.
        /// </remarks>
        private static string ProgramNameOf(string command)
        {
            string program = ExecutableOf(command).Trim('"');

            if (program.Length == 0)
            {
                return "the chosen tool";
            }

            try
            {
                string name = Path.GetFileNameWithoutExtension(program);

                return name.Length > 0 ? name : program;
            }
            catch (ArgumentException)
            {
                // A command line that is not a usable path at all - which a user can type into the
                // settings file by hand - must not stop the window from drawing. The whole command is
                // better than nothing, since the caption still says what is set.
                return program;
            }
        }

        /// <summary>
        /// The program part of a command line, which is the first token of it.
        /// </summary>
        /// <remarks>
        /// The search for the end of the token has to skip over a quoted run: every tool worth naming
        /// installs under "C:\Program Files", so stopping at the first space would yield
        /// "\"C:\Program" and the caption would then read "Diff tool: Program".
        /// </remarks>
        private static string ExecutableOf(string command)
        {
            string trimmed = command.Trim();
            int index = 0;
            bool inQuotes = false;

            while (index < trimmed.Length)
            {
                if (trimmed[index] == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (!inQuotes && (trimmed[index] == ' ' || trimmed[index] == '\t'))
                {
                    break;
                }

                index++;
            }

            return trimmed.Substring(0, index);
        }

        /// <summary>
        /// Loads the commit log. Consecutive calls supersede each other, so typing in the directory
        /// box cannot let an older, slower git process overwrite a newer result.
        /// </summary>
        public async Task RefreshLogAsync()
        {
            CancellationTokenSource cts = BeginOperation(ref _logCts);

            try
            {
                await Task.Delay(DebounceMilliseconds, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            string directory = GitDirectory;

            try
            {
                // Inside the try, and before the loading state is set, so that the return below still
                // runs the finally. It used to sit outside: a refresh that cancelled an earlier one and
                // then found the path invalid returned without clearing anything, and the earlier
                // refresh's finally had already skipped because it was cancelled. The spinner was
                // then left turning on an idle window with nothing behind it.
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    await OnUiAsync(() =>
                    {
                        // The repository may never have changed to an invalid value - it may have been
                        // typed into the box - so this path clears too rather than trusting the setter to
                        // have done it.
                        Commits.Clear();
                        FilteredCommits.Clear();
                        Branches.Clear();
                        Changes.Clear();
                        BranchesMayBeStale = false;
                        OnPropertyChanged("HasBranches");
                        OnPropertyChanged("SelectionSummary");
                        OnPropertyChanged("CommitFilterSummary");
                        SetRange(null);
                        SetStatus(string.Format("'{0}' is not an existing directory.", directory), true);
                    }).ConfigureAwait(false);
                    return;
                }

                await SetLoadStateAsync(true, "Reading the repository...").ConfigureAwait(false);

                GitService git = new GitService(directory);
                string root = await git.GetRepositoryRootAsync(cts.Token).ConfigureAwait(false);

                // Updating the remote-tracking refs is what makes a branch somebody else pushed show
                // up. Done only when the repository itself changed, because it is the one call here
                // that touches the network. Named as its own stage because it is the slowest one and
                // the only one that can block on a slow or unreachable remote.
                await SetLoadingMessageAsync("Updating branches from the remote...").ConfigureAwait(false);
                FetchOutcome fetch = await FetchIfNewRepositoryAsync(git, root, cts.Token).ConfigureAwait(false);

                // The branch list and the log are independent reads; taking them together saves a
                // round trip through the repository, and a failure in one is reported without
                // stopping the other from loading.
                await SetLoadingMessageAsync("Reading the commit log...").ConfigureAwait(false);
                IList<GitBranch> branches = await ReadBranchesAsync(git, cts.Token).ConfigureAwait(false);
                IList<GitCommit> commits = await git
                    .GetLogAsync(LogLimit, ActiveRef, cts.Token)
                    .ConfigureAwait(false);

                // Read after the log so a newer cancellation has already been checked; a failure here
                // must not stop the log from loading.
                GitRepositoryStatus repositoryStatus = await ReadStatusAsync(git, cts.Token).ConfigureAwait(false);

                if (cts.IsCancellationRequested)
                {
                    return;
                }

                await OnUiAsync(() =>
                {
                    Commits.Clear();
                    foreach (GitCommit commit in commits)
                    {
                        Commits.Add(commit);
                    }

                    // Re-applied rather than assumed: a filter left over from the previous repository
                    // still stands, and a fresh list has to be narrowed by it.
                    ApplyCommitFilter();

                    ApplyBranches(branches);

                    Changes.Clear();
                    RepositoryStatus = repositoryStatus;
                    SetRange(null);
                    OnPropertyChanged("SelectionSummary");

                    // Only a fetch that failed leaves the branches possibly behind. One that was not
                    // needed, and one for a repository with no remote, are both ordinary and are left
                    // unmentioned.
                    BranchesMayBeStale = fetch == FetchOutcome.Failed;

                    SetStatus(
                        commits.Count == 0
                            ? "No commits found."
                            : string.Format(
                                "{0} commit(s) loaded from {1}{2}{3}. Select two commits to compare.",
                                commits.Count,
                                root,
                                ActiveRef == null ? string.Empty : " on " + ActiveRefName,
                                FetchClause(fetch)),
                        false);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer request.
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                await OnUiAsync(() =>
                {
                    Commits.Clear();
                    FilteredCommits.Clear();
                    RepositoryStatus = new GitRepositoryStatus();
                    OnPropertyChanged("CommitFilterSummary");
                    SetStatus(ex.Message, true);
                }).ConfigureAwait(false);
            }
            finally
            {
                // Skipped when this refresh has been superseded: the newer one owns the state now, and
                // clearing it here would stop its spinner and re-enable controls while it is still
                // reading. Both flags in one dispatcher hop - cleared separately there is a moment
                // where the load is no longer busy but still claims to be loading, which shows a
                // spinning ring on a window that is already idle.
                if (!cts.IsCancellationRequested)
                {
                    await SetLoadStateAsync(false, string.Empty).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// The bracketed clause the load summary carries about the remote, or nothing when there is
        /// nothing to say.
        /// </summary>
        /// <remarks>
        /// Said in the status line as well as in the branch picker, because the status line is where the
        /// load that just happened is described: a reader who only ever reads that line still learns
        /// that the branches came from disk rather than from the remote.
        /// </remarks>
        private static string FetchClause(FetchOutcome outcome)
        {
            if (outcome == FetchOutcome.Updated)
            {
                return " (branches updated from the remote)";
            }

            return outcome == FetchOutcome.Failed
                ? " (could not reach the remote; branches are as of the last fetch)"
                : string.Empty;
        }

        /// <summary>
        /// Reads the branch state, treating a failure as "unknown" rather than fatal. A repository
        /// without an upstream, or an older git that lacks porcelain v2, must still show its log.
        /// </summary>
        private static async Task<GitRepositoryStatus> ReadStatusAsync(
            GitService git,
            CancellationToken cancellationToken)
        {
            try
            {
                return await git.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                GitRepositoryStatus unknown = new GitRepositoryStatus
                {
                    BranchName = "branch unknown",
                };

                return unknown;
            }
        }

        /// <summary>
        /// Fetches once per repository, not once per refresh.
        /// </summary>
        /// <remarks>
        /// The comparison is on the repository <em>root</em>, not the typed path, so moving from a
        /// subfolder to its parent - or editing the path inside the same repository - does not fetch
        /// again. The root is recorded whatever the outcome, so a repository that cannot be reached is
        /// not retried on every keystroke; the notice's retry button is what clears it.
        /// </remarks>
        private async Task<FetchOutcome> FetchIfNewRepositoryAsync(
            GitService git,
            string root,
            CancellationToken cancellationToken)
        {
            if (_lastFetchedRoot != null
                && string.Equals(_lastFetchedRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                // Already tried for this repository, so there is nothing new to learn and nothing to
                // warn about: the branches on screen were settled by that attempt.
                return FetchOutcome.NoRemote;
            }

            FetchOutcome outcome = await git.FetchAsync(cancellationToken).ConfigureAwait(false);

            _lastFetchedRoot = root;

            return outcome;
        }

        /// <summary>
        /// Reads the branch list, returning nothing rather than failing the refresh if git refuses:
        /// the log is still worth showing when the branches cannot be listed.
        /// </summary>
        private static async Task<IList<GitBranch>> ReadBranchesAsync(
            GitService git,
            CancellationToken cancellationToken)
        {
            try
            {
                return await git.GetBranchesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                return new List<GitBranch>();
            }
        }

        private async Task CreateFoldersAsync()
        {
            if (_range == null || IsExporting)
            {
                return;
            }

            CommitRange range = _range;
            CancellationTokenSource cts = BeginOperation(ref _exportCts);

            IsExporting = true;
            await OnUiAsync(() =>
            {
                OutputLog = string.Empty;
                ResetExportProgress();
            }).ConfigureAwait(false);

            try
            {
                GitService git = new GitService(GitDirectory);
                DiffExporter exporter = new DiffExporter();

                // Posted to the dispatcher because the exporter reports from a git read on a thread
                // pool work item, and the bar's bindings belong to the UI thread.
                Progress<ExportProgress> progress = new Progress<ExportProgress>(report =>
                    _dispatcher.BeginInvoke(
                        DispatcherPriority.Normal,
                        (Action)(() => ApplyExportProgress(report))));

                ExportRequest request = new ExportRequest
                {
                    Git = git,
                    BaseHash = range.Base.Hash,
                    ModifiedHash = range.Modified.Hash,
                    OutputRoot = OutputDirectory,
                    ExcludedPaths = ExcludedPaths(),
                };

                ExportResult result = await exporter.ExportAsync(request, progress, cts.Token).ConfigureAwait(true);

                await OnUiAsync(() =>
                {
                    // Closed before the outcome is written, so a progress report still in the queue
                    // cannot land on top of it.
                    _exportProgressClosed = true;

                    if (result.Success)
                    {
                        _lastRunFolder = result.RootFolderPath;

                        // The Open button now means the result rather than the output folder, so its
                        // caption has to be re-read by the bindings rather than only by a reader.
                        OnPropertyChanged("OpenOutputCaption");
                        OnPropertyChanged("OpenOutputTooltip");

                        UpdateCommandStates();
                    }

                    OutputLog = Join(result.Messages, result.Warnings);
                    SetStatus(
                        result.Success
                            ? (result.Warnings.Count == 0
                                ? "Folders created."
                                : string.Format("Folders created with {0} warning(s).", result.Warnings.Count))
                            : (result.FailureReason ?? "Export failed."),
                        !result.Success);
                }).ConfigureAwait(false);

                if (result.Success && OpenOutputWhenFinished)
                {
                    _openFolder(result.RootFolderPath);
                }
            }
            catch (OperationCanceledException)
            {
                await OnUiAsync(() =>
                {
                    OutputLog = "Cancelled. No folders were published.";
                    SetStatus("Cancelled.", true);
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                await OnUiAsync(() =>
                {
                    OutputLog = ex.Message;
                    SetStatus(ex.Message, true);
                }).ConfigureAwait(false);
            }
            finally
            {
                await OnUiAsync(() => IsExporting = false).ConfigureAwait(false);
            }
        }

        /// <summary>Sums one side of the line counts, skipping files git could not count.</summary>
        private static int Total(IEnumerable<GitFileChange> changes, Func<GitFileChange, int?> selector, bool onlyIncluded = false)
        {
            int total = 0;

            foreach (GitFileChange change in changes)
            {
                // The action bar reports what will actually be exported, so an unticked file's lines
                // are left out of the totals rather than counted and then not written.
                if (onlyIncluded && !change.IsIncluded)
                {
                    continue;
                }

                if (selector(change).HasValue)
                {
                    total += selector(change)!.Value;
                }
            }

            return total;
        }

        /// <summary>
        /// Repopulates <see cref="FilteredChanges"/> from <see cref="Changes"/>. Rebuilt wholesale
        /// rather than diffed: the collections are small relative to the git work that produced them,
        /// and this keeps the filtering rules in one place.
        /// </summary>
        private void ApplyFilter()
        {
            string text = _changeFilter.Trim();
            char status = StatusFilterCode(_statusFilter);

            FilteredChanges.Clear();
            foreach (GitFileChange change in Changes)
            {
                if (Matches(change, text, status))
                {
                    FilteredChanges.Add(change);
                }
            }

            OnPropertyChanged("FilterSummary");
        }

        internal static bool Matches(GitFileChange change, string text, char status)
        {
            if (status != '\0' && change.StatusCode.Length > 0 && change.StatusCode[0] != status)
            {
                return false;
            }

            return text.Length == 0
                || change.Path.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
                || (change.OldPath != null
                    && change.OldPath.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// The status code a combo box option stands for, or NUL for "no status filter".
        /// </summary>
        /// <remarks>
        /// Compared as a whole word on purpose: testing only the first character made "A added"
        /// indistinguishable from "All", and the added-files filter silently became a no-op.
        /// </remarks>
        internal static char StatusFilterCode(string? option)
        {
            if (string.IsNullOrWhiteSpace(option) || AllStatusFilter.Equals(option!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return '\0';
            }

            return char.ToUpperInvariant(option.Trim()[0]);
        }

        private void SelectedCommitsOnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            SelectionUpdated();
        }

        private void FileChangeOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GitFileChange.IsIncluded) && !_suppressFileChangeNotifications)
            {
                OnPropertyChanged("SelectionSummary");
                OnPropertyChanged("ExcludedCount");
                UpdateCommandStates();
            }
        }

        /// <summary>
        /// Set while a bulk change is being applied, so the summary is rebuilt once rather than once
        /// per file.
        /// </summary>
        private bool _suppressFileChangeNotifications;

        private void SelectionUpdated()
        {
            int version = Interlocked.Increment(ref _selectionVersion);
            Forget(UpdateSelectionAsync(version));
        }

        /// <summary>
        /// Re-runs the comparison with base and modified exchanged. The file list is rebuilt,
        /// because which folder a path lands in depends on the direction.
        /// </summary>
        private async Task SwapRangeAsync()
        {
            if (_range == null)
            {
                return;
            }

            CommitRange swapped = new CommitRange(
                _range.Modified,
                _range.Base,
                _range.Confidence);

            int version = Interlocked.Increment(ref _selectionVersion);

            try
            {
                GitService git = new GitService(GitDirectory);

                IList<GitFileChange> changes = await git
                    .GetChangesAsync(swapped.Base.Hash, swapped.Modified.Hash, CancellationToken.None)
                    .ConfigureAwait(true);

                if (version != Volatile.Read(ref _selectionVersion))
                {
                    return;
                }

                await OnUiAsync(() =>
                {
                    ReplaceChanges(changes);
                    SetRange(swapped);
                    OnPropertyChanged("SelectionSummary");
                    SetStatus(
                        string.Format(
                            "Comparing the other way round: {0} as base, {1} as modified.",
                            swapped.Base.ShortHash,
                            swapped.Modified.ShortHash),
                        false);
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                await OnUiAsync(() => SetStatus(ex.Message, true)).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Double-clicking a row in the changed-file list asks for that file's difference to be
        /// shown. Bound to the list, so the view passes the row rather than going looking for it.
        /// </summary>
        public void ShowDifferenceFor(GitFileChange? change)
        {
            if (change != null)
            {
                Forget(ShowFileDifferenceAsync(change));
            }
        }

        /// <summary>
        /// Hands one changed file to whatever diff tool git is configured to use, showing the two
        /// versions of that single file.
        /// </summary>
        /// <remarks>
        /// The extracted copies are left in the temporary folder: the diff tool is still reading them
        /// when this returns, and deleting them now would leave it showing two empty files. The
        /// temporary folder is reclaimed by the operating system eventually, which is the right
        /// trade for files this small.
        /// </remarks>
        private async Task ShowFileDifferenceAsync(GitFileChange change)
        {
            CommitRange? range = _range;

            if (range == null || change == null || IsBusy)
            {
                return;
            }

            try
            {
                GitService git = new GitService(GitDirectory);
                DiffToolLauncher launcher = new DiffToolLauncher();

                DiffToolLaunchResult result = await launcher
                    .LaunchAsync(git, change, range.Base.Hash, range.Modified.Hash, DiffToolCommand, CancellationToken.None)
                    .ConfigureAwait(true);

                await OnUiAsync(() => SetStatus(result.Message, result.IsError)).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                await OnUiAsync(() => SetStatus(ex.Message, true)).ConfigureAwait(false);
            }
        }

        private async Task UpdateSelectionAsync(int version)
        {
            List<GitCommit> selected = SelectedCommits.ToList();

            if (selected.Count != 2)
            {
                await OnUiAsync(() =>
                {
                    Changes.Clear();
                    SetRange(null);
                    OnPropertyChanged("SelectionSummary");
                    SetStatus(
                        selected.Count == 0
                            ? "Pick two commits to compare."
                            : "Pick one more commit to compare.",
                        false);
                }).ConfigureAwait(false);
                return;
            }

            try
            {
                GitService git = new GitService(GitDirectory);
                CommitRange range = await CommitRangeResolver
                    .ResolveAsync(git, selected[0], selected[1], CancellationToken.None)
                    .ConfigureAwait(true);

                IList<GitFileChange> changes =
                    await git.GetChangesAsync(range.Base.Hash, range.Modified.Hash, CancellationToken.None).ConfigureAwait(true);

                if (version != Volatile.Read(ref _selectionVersion))
                {
                    // The selection changed while git was running.
                    return;
                }

                await OnUiAsync(() =>
                {
                    ReplaceChanges(changes);
                    SetRange(range);
                    OnPropertyChanged("SelectionSummary");

                        string totals = string.Format(
                            ", {0} added, {1} removed",
                            Total(changes, c => c.AddedLines),
                            Total(changes, c => c.DeletedLines));

                        SetStatus(
                            string.Format(
                                "{0} changed path(s) between {1} and {2}{3}. {4}",
                                changes.Count,
                                range.Base.ShortHash,
                                range.Modified.ShortHash,
                                totals,
                                range.ConfidenceNote).TrimEnd(),
                            false);
                    }).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                if (version != Volatile.Read(ref _selectionVersion))
                {
                    return;
                }

                await OnUiAsync(() =>
                {
                    Changes.Clear();
                    SetRange(null);
                    SetStatus(ex.Message, true);
                }).ConfigureAwait(false);
            }
        }

        private void ForgetRefresh()
        {
            Forget(RefreshLogAsync());
        }

        private void BrowseForGitDirectory()
        {
            // IsNullOrWhiteSpace has no nullable annotations on the 4.6.1 reference assemblies, so
            // the compiler cannot narrow the type here.
            string? picked = _pickFolder(GitDirectory, "Select the git working folder");
            if (!string.IsNullOrWhiteSpace(picked))
            {
                GitDirectory = picked!;
            }
        }

        private void BrowseForOutputDirectory()
        {
            string? picked = _pickFolder(OutputDirectory, "Select the output folder");
            if (!string.IsNullOrWhiteSpace(picked))
            {
                OutputDirectory = picked!;
            }
        }

        private void OpenOutputFolder()
        {
            string target = string.IsNullOrWhiteSpace(_lastRunFolder) ? OutputDirectory : _lastRunFolder;

            if (!_openFolder(target))
            {
                SetStatus(
                    string.Format("Could not open '{0}'. Check that the folder still exists.", target),
                    true);
            }
        }

        private bool CanCreate()
        {
            return CanCreateFolders;
        }

        private bool CanCancel()
        {
            return IsExporting;
        }

        private void CancelExport()
        {
            if (_exportCts != null)
            {
                _exportCts.Cancel();
            }
        }

        private void OnCommandFailed(object sender, Exception exception)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
            {
                OutputLog = exception.Message;
                SetStatus(exception.Message, true);
            }));
        }

        private static string Join(IList<string> messages, IList<string> warnings)
        {
            if (warnings.Count == 0)
            {
                return string.Join(Environment.NewLine, messages.ToArray());
            }

            List<string> lines = new List<string>(messages);
            lines.Add(string.Empty);
            lines.Add("Warnings:");
            lines.AddRange(warnings);
            return string.Join(Environment.NewLine, lines.ToArray());
        }

        /// <summary>The paths the user has unticked, which the exporter leaves out.</summary>
        private List<string> ExcludedPaths()
        {
            List<string> excluded = new List<string>();

            foreach (GitFileChange change in Changes)
            {
                if (!change.IsIncluded)
                {
                    excluded.Add(change.Path);
                }
            }

            return excluded;
        }

        /// <summary>
        /// Shows the check boxes when they are hidden and hides them when they are shown.
        /// </summary>
        /// <remarks>
        /// Turning them on leaves every file ticked. Starting from "nothing selected" would look like
        /// a deliberate choice, and the export would quietly produce an empty folder the first time
        /// someone turned the option on to look at it.
        /// </remarks>
        private void ToggleSelection()
        {
            IsSelectionEnabled = !IsSelectionEnabled;

            if (IsSelectionEnabled)
            {
                SetAllIncluded(true);
            }
        }

        /// <summary>
        /// Ticks or unticks every changed file.
        /// </summary>
        /// <remarks>
        /// Each row raises its own change, and every one of those recomputes the summary and
        /// re-evaluates every command. Doing that once per file makes "Select all" on a large
        /// comparison visibly slow, so the notifications are held back and sent once at the end.
        /// The rows still repaint individually, because that is the binding doing its own work.
        /// </remarks>
        private void SetAllIncluded(bool included)
        {
            if (Changes.Count == 0)
            {
                return;
            }

            _suppressFileChangeNotifications = true;

            try
            {
                foreach (GitFileChange change in Changes)
                {
                    change.IsIncluded = included;
                }
            }
            finally
            {
                _suppressFileChangeNotifications = false;
            }

            OnPropertyChanged("SelectionSummary");
            OnPropertyChanged("ExcludedCount");
            UpdateCommandStates();
        }

        /// <summary>How many files are currently left out of the export.</summary>
        public int ExcludedCount
        {
            get
            {
                int count = 0;

                foreach (GitFileChange change in Changes)
                {
                    if (!change.IsIncluded)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        private static bool IsExpected(Exception ex)        {
            return ex is GitCommandException
                || ex is IOException
                || ex is UnauthorizedAccessException
                || ex is InvalidOperationException
                || ex is ArgumentException;
        }

        private void SetRange(CommitRange? range)
        {
            if (!SetProperty(ref _range, range))
            {
                return;
            }

            ApplyCommitRoles();
            OnPropertyChanged("RangeCaption");
            OnPropertyChanged("CanSwapRange");
            UpdateCommandStates();
        }

        /// <summary>
        /// Replaces the changed-file list without reading a repository.
        /// </summary>
        /// <remarks>
        /// For the tests of the selection behaviour, which are about ticking and unticking files and
        /// do not need real git output to be meaningful. Nothing is stubbed out to make this work:
        /// <see cref="Changes"/> is an observable collection carrying the same wiring the real path
        /// uses, so the per-file notifications, the filtered view and the summary all update exactly
        /// as they do when a comparison finishes.
        /// </remarks>
        internal void SetChangesForTest(IList<GitFileChange> files)
        {
            ReplaceChanges(files);
            OnPropertyChanged("SelectionSummary");
            OnPropertyChanged("ExcludedCount");
            OnPropertyChanged("CanCreateFolders");
            UpdateCommandStates();
        }

        /// <summary>
        /// Labels the two selected commits in the log itself, so the resolved direction is visible
        /// where the user makes the pick instead of only in a heading on the other side of the window.
        /// </summary>
        private void ApplyCommitRoles()
        {
            // Every commit is reset, not just the selected ones, so a commit that was the base
            // before and is no longer selected does not keep a stale badge.
            foreach (GitCommit commit in Commits)
            {
                commit.Role = GitCommitRole.None;
            }

            if (_range != null)
            {
                _range.Base.Role = GitCommitRole.Base;
                _range.Modified.Role = GitCommitRole.Modified;
            }
        }

        private void SetStatus(string message, bool isError)
        {
            Status = message;
            IsStatusError = isError;
        }

        private Task SetLoadStateAsync(bool loading, string message)
        {
            return OnUiAsync(() =>
            {
                IsBusy = loading;
                LoadingMessage = message;
                IsLoadingLog = loading;
            });
        }

        /// <summary>
        /// Replaces the whole changed-file list, then filters it once.
        /// </summary>
        /// <remarks>
        /// Every collection event still arrives, because the rows bind to the collection and the
        /// per-file handlers have to be attached and detached as items come and go. What is held back
        /// is only the derived work: <see cref="ApplyFilter"/> reads every file in the list and
        /// rebuilds the filtered one from scratch, so applying it per added file is quadratic in the
        /// number of changed files - and a comparison between two branches runs to thousands.
        /// </remarks>
        private void ReplaceChanges(IList<GitFileChange> changes)
        {
            _suppressChangeFilter = true;

            try
            {
                Changes.Clear();

                foreach (GitFileChange change in changes)
                {
                    Changes.Add(change);
                }
            }
            finally
            {
                _suppressChangeFilter = false;
            }

            ApplyFilter();
        }

        /// <summary>
        /// Set while the changed-file list is being replaced wholesale, so the filter is applied once to
        /// the finished list rather than once per file added to it.
        /// </summary>
        private bool _suppressChangeFilter;

        private Task SetLoadingMessageAsync(string message)
        {
            return OnUiAsync(() => LoadingMessage = message);
        }

        private void UpdateCommandStates()
        {
            RefreshLogCommand.RaiseCanExecuteChanged();
            CreateFoldersCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            OpenOutputFolderCommand.RaiseCanExecuteChanged();
            SwapRangeCommand.RaiseCanExecuteChanged();
            ToggleBranchListCommand.RaiseCanExecuteChanged();
            RetryRemoteCommand.RaiseCanExecuteChanged();
            ToggleSelectionCommand.RaiseCanExecuteChanged();
            SelectAllCommand.RaiseCanExecuteChanged();
            DeselectAllCommand.RaiseCanExecuteChanged();
            OnPropertyChanged("CanCreateFolders");
            OnPropertyChanged("CanOpenOutputFolder");
            OnPropertyChanged("CanSwapRange");
        }

        private static CancellationTokenSource BeginOperation(ref CancellationTokenSource? field)
        {
            CancellationTokenSource? previous = field;
            field = new CancellationTokenSource();

            if (previous != null)
            {
                previous.Cancel();
                previous.Dispose();
            }

            return field;
        }

        private Task OnUiAsync(Action action)
        {
            if (_dispatcher.CheckAccess())
            {
                action();
                return Task.FromResult(0);
            }

            return _dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
        }

        private static void Forget(Task task)
        {
            GC.KeepAlive(task);
        }
    }
}