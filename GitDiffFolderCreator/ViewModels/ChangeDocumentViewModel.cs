using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using GitDiffFolderCreator.Services;

namespace GitDiffFolderCreator.ViewModels
{
    /// <summary>
    /// The change-document checker: three inputs, a verification, and the findings it produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Experimental, and deliberately not wired to the repository side of the application. It compares
    /// two folders and a Word document, and knows nothing about commits, branches or the export — which
    /// is what makes it useful for the case it was written for: a change document prepared alongside two
    /// copies of a source tree, checked before the work is delivered.
    /// </para>
    /// <para>
    /// The work runs on a background thread because comparing two source trees reads every byte of every
    /// file, and doing that on the UI thread would freeze the window for as long as it takes. Results
    /// arrive back through the synchronisation context, so only the assignment is left to the UI thread.
    /// </para>
    /// </remarks>
    public sealed class ChangeDocumentViewModel : BindableBase
    {
        private readonly Func<string, string, string?> _pickFolder;
        private readonly Func<string, string?> _pickDocument;
        private readonly Func<string, string?> _pickReportFile;
        private readonly Action<string> _copyText;
        private readonly RelayCommand _browseBaseCommand;
        private readonly RelayCommand _browseModifiedCommand;
        private readonly RelayCommand _browseDocumentCommand;
        private readonly AsyncRelayCommand _verifyCommand;
        private readonly RelayCommand _copyReportCommand;
        private readonly RelayCommand _exportReportCommand;

        private string _baseFolder = string.Empty;
        private string _modifiedFolder = string.Empty;
        private string _documentPath = string.Empty;
        private bool _ignoreBuildOutput = true;
        private string? _error;
        private ChangeVerificationResult? _result;
        private bool _isVerifying;

        public ChangeDocumentViewModel(
            Func<string, string, string?> pickFolder,
            Func<string, string?> pickDocument,
            Func<string, string?>? pickReportFile = null,
            Action<string>? copyText = null)
        {
            _pickFolder = pickFolder ?? throw new ArgumentNullException(nameof(pickFolder));
            _pickDocument = pickDocument ?? throw new ArgumentNullException(nameof(pickDocument));

            // Optional so that a caller interested only in verifying does not have to supply a
            // clipboard and a save dialog it will never use. Both default to doing nothing visible
            // rather than throwing, because a window that wired them up wrongly should still verify.
            _pickReportFile = pickReportFile ?? (_ => null);
            _copyText = copyText ?? (_ => { });

            _browseBaseCommand = new RelayCommand(_ => BrowseBase());
            _browseModifiedCommand = new RelayCommand(_ => BrowseModified());
            _browseDocumentCommand = new RelayCommand(_ => BrowseDocument());
            _verifyCommand = new AsyncRelayCommand(_ => VerifyAsync(), _ => CanVerify);
            _copyReportCommand = new RelayCommand(_ => CopyReport(), _ => CanReport);
            _exportReportCommand = new RelayCommand(_ => ExportReport(), _ => CanReport);

            Findings = new ObservableCollection<ChangeFinding>();
        }

        /// <summary>The source tree before the changes.</summary>
        public string BaseFolder
        {
            get { return _baseFolder; }
            set
            {
                if (SetProperty(ref _baseFolder, value))
                {
                    RaiseCanVerifyChanged();
                }
            }
        }

        /// <summary>The source tree containing the changes.</summary>
        public string ModifiedFolder
        {
            get { return _modifiedFolder; }
            set
            {
                if (SetProperty(ref _modifiedFolder, value))
                {
                    RaiseCanVerifyChanged();
                }
            }
        }

        /// <summary>The Word document listing the files that were changed.</summary>
        public string DocumentPath
        {
            get { return _documentPath; }
            set
            {
                if (SetProperty(ref _documentPath, value))
                {
                    RaiseCanVerifyChanged();
                }
            }
        }

        /// <summary>
        /// Whether <c>bin</c>, <c>obj</c> and the rest are left out of the comparison.
        /// </summary>
        /// <remarks>
        /// On by default because these are source trees. A compiled assembly differs on every build, so
        /// leaving build output in would report every project as changed and bury the changes the check
        /// exists to find.
        /// </remarks>
        public bool IgnoreBuildOutput
        {
            get { return _ignoreBuildOutput; }
            set { SetProperty(ref _ignoreBuildOutput, value); }
        }

        /// <summary>The findings, worst first, or empty until a verification has run.</summary>
        public ObservableCollection<ChangeFinding> Findings { get; }

        /// <summary>Why the last attempt could not produce a result, or null when it could.</summary>
        public string? Error
        {
            get { return _error; }
            private set
            {
                if (SetProperty(ref _error, value))
                {
                    OnPropertyChanged(nameof(HasError));
                }
            }
        }

        public bool HasError => !string.IsNullOrEmpty(Error);

        /// <summary>The verification, or null before one has run.</summary>
        public ChangeVerificationResult? Result
        {
            get { return _result; }
            private set
            {
                if (SetProperty(ref _result, value))
                {
                    OnPropertyChanged(nameof(HasResult));
                    OnPropertyChanged(nameof(Summary));
                    OnPropertyChanged(nameof(VerdictCaption));
                    OnPropertyChanged(nameof(VerdictIsGood));
                    OnPropertyChanged(nameof(DetailLine));

                    // CanReport reads this, and nothing else announces it. Leaving it out here is
                    // what left Copy and Export greyed out forever after a successful check: the
                    // buttons ask their command once when the window loads and are never asked again.
                    RaiseCanReportChanged();
                }
            }
        }

        public bool HasResult => _result != null;

        /// <summary>The headline: what the counts were and whether they agreed.</summary>
        public string Summary
        {
            get
            {
                if (_result == null)
                {
                    return string.Empty;
                }

                return string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} path(s) documented, {1} path(s) differ between the sources, {2} confirmed.",
                    _result.DocumentedCount,
                    _result.ActualDifferenceCount,
                    _result.Matches);
            }
        }

        public string VerdictCaption => _result == null
            ? string.Empty
            : _result.Agrees
                ? "The document matches the source."
                : _result.Findings.Count + " problem(s) found.";

        public bool VerdictIsGood => _result != null && _result.Agrees;

        /// <summary>
        /// How much was actually compared, and what was left out.
        /// </summary>
        /// <remarks>
        /// Shown because a check that silently skipped a third of the tree is not a check. When nothing
        /// was skipped the second half is omitted rather than saying "none".
        /// </remarks>
        public string DetailLine
        {
            get
            {
                if (_result == null)
                {
                    return string.Empty;
                }

                string compared = string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} file(s) present in both folders were compared.",
                    _result.FilesCompared);

                return _result.FilesIgnored > 0
                    ? compared + string.Format(
                        CultureInfo.CurrentCulture,
                        " {0} file(s) under bin, obj and the like were left out.",
                        _result.FilesIgnored)
                    : compared;
            }
        }

        public bool IsVerifying
        {
            get { return _isVerifying; }
            private set
            {
                if (SetProperty(ref _isVerifying, value))
                {
                    OnPropertyChanged(nameof(VerifyCaption));

                    // Verify is disabled while it runs and re-enabled after, and Copy and Export wait
                    // for it to finish. Neither knows about the other, so all three are announced here.
                    RaiseCanReportChanged();
                    _verifyCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string VerifyCaption => IsVerifying ? "Verifying..." : "Verify";

        public RelayCommand BrowseBaseCommand => _browseBaseCommand;

        public RelayCommand BrowseModifiedCommand => _browseModifiedCommand;

        public RelayCommand BrowseDocumentCommand => _browseDocumentCommand;

        public AsyncRelayCommand VerifyCommand => _verifyCommand;

        public RelayCommand CopyReportCommand => _copyReportCommand;

        public RelayCommand ExportReportCommand => _exportReportCommand;

        /// <summary>
        /// Whether there is anything to write out.
        /// </summary>
        /// <remarks>
        /// False until a verification has produced findings. A report of nothing is not useless, but it
        /// is not worth a button that is live before there is a result to report.
        /// </remarks>
        public bool CanReport => _result != null && !IsVerifying;

        private bool CanVerify =>
            !IsVerifying
            && BaseFolder.Trim().Length > 0
            && ModifiedFolder.Trim().Length > 0
            && DocumentPath.Trim().Length > 0;

        private void RaiseCanVerifyChanged()
        {
            _verifyCommand.RaiseCanExecuteChanged();
            RaiseCanReportChanged();
        }

        /// <summary>
        /// Announces that there may now be something to write out.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="RaiseCanVerifyChanged"/> because the answer changes when a
        /// verification finishes rather than when an input box is filled in, and a bound button only
        /// asks once unless it is told.
        /// </remarks>
        private void RaiseCanReportChanged()
        {
            _copyReportCommand.RaiseCanExecuteChanged();
            _exportReportCommand.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// Puts the findings on the clipboard as CSV, which is what a spreadsheet or a ticket wants.
        /// </summary>
        /// <remarks>
        /// CSV rather than tab-separated text, because the paths and the sentences both contain commas
        /// and a tab-separated copy would paste into the wrong columns. A clipboard that refuses the
        /// text is reported rather than thrown: it is another process holding it, not a fault here.
        /// </remarks>
        private void CopyReport()
        {
            if (_result == null)
            {
                return;
            }

            try
            {
                _copyText(ChangeReportWriter.ToCsv(_result));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException
                or InvalidOperationException)
            {
                Error = "The clipboard could not be written: " + ex.Message;
            }
        }

        /// <summary>Writes the findings to a file the reader chooses.</summary>
        private void ExportReport()
        {
            if (_result == null)
            {
                return;
            }

            string? path = _pickReportFile(SuggestedReportName());

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                ChangeReportWriter.Write(_result, path!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException)
            {
                Error = "The report could not be written: " + ex.Message;
            }
        }

        /// <summary>
        /// The name offered in the save dialog, taken from the document so the reader recognises it.
        /// </summary>
        private string SuggestedReportName()
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(DocumentPath.Trim());

            return string.IsNullOrWhiteSpace(name)
                ? "change-document-report.csv"
                : name + "-report.csv";
        }

        private void BrowseBase()
        {
            string? picked = _pickFolder(BaseFolder, "Select the base source folder");

            if (!string.IsNullOrWhiteSpace(picked))
            {
                BaseFolder = picked!;
            }
        }

        private void BrowseModified()
        {
            string? picked = _pickFolder(ModifiedFolder, "Select the modified source folder");

            if (!string.IsNullOrWhiteSpace(picked))
            {
                ModifiedFolder = picked!;
            }
        }

        private void BrowseDocument()
        {
            string? picked = _pickDocument(DocumentPath);

            if (!string.IsNullOrWhiteSpace(picked))
            {
                DocumentPath = picked!;
            }
        }

        /// <summary>
        /// Reads the document, compares the folders, and reconciles the two.
        /// </summary>
        /// <remarks>
        /// Every failure is turned into a message rather than left to propagate. A checker that throws
        /// on a missing folder says less than one that says which folder is missing, and the exceptions
        /// here are all things a user can act on.
        /// <para>
        /// Internal rather than private so a test can await the run itself. The command's entry point is
        /// <c>async void</c>, as an <c>ICommand</c> has to be, so there is otherwise nothing to await
        /// and the only way to assert afterwards is to wait and hope.
        /// </para>
        /// </remarks>
        internal async Task VerifyAsync()
        {
            IsVerifying = true;
            Error = null;

            try
            {
                string baseFolder = BaseFolder.Trim();
                string modifiedFolder = ModifiedFolder.Trim();
                string documentPath = DocumentPath.Trim();

                ChangeVerificationResult result = await Task.Run(() =>
                {
                    IList<string> lines = DocxTextReader.ReadLines(documentPath);
                    IList<DocumentedFile> documented = ChangeDocumentParser.Parse(lines);

                    var comparer = new FolderComparer(
                        IgnoreBuildOutput ? FolderComparer.DefaultIgnoredFolders : Array.Empty<string>());

                    FolderComparison comparison = comparer.Compare(baseFolder, modifiedFolder);

                    return new ChangeDocumentVerifier().Verify(documented, comparison);
                }).ConfigureAwait(true);

                Findings.Clear();

                foreach (ChangeFinding finding in result.Findings)
                {
                    Findings.Add(finding);
                }

                Result = result;
            }
            catch (ChangeDocumentException ex)
            {
                Fail(ex.Message);
            }
            catch (DirectoryNotFoundException ex)
            {
                Fail(ex.Message);
            }
            catch (IOException ex)
            {
                Fail("A file could not be read: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                Fail("Access was denied: " + ex.Message);
            }
            finally
            {
                IsVerifying = false;
            }
        }

        /// <summary>
        /// Reports a failure and clears the previous result, so a stale verdict is never read as the
        /// answer to the attempt that just failed.
        /// </summary>
        private void Fail(string message)
        {
            Findings.Clear();
            Result = null;
            Error = message;
        }
    }
}