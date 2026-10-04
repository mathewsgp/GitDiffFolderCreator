using System;
using System.Windows;
using GitDiffFolderCreator.ViewModels;

namespace GitDiffFolderCreator
{
    /// <summary>
    /// Picks the comparison tool that double-clicking a changed file opens.
    /// </summary>
    /// <remarks>
    /// Holds no logic: the list and the choice belong to <see cref="DiffToolPickerViewModel"/>, and
    /// the only thing this has to supply is a file picker for the Browse button.
    /// </remarks>
    public partial class DiffToolPickerWindow : Window
    {
        public DiffToolPickerWindow()
        {
            InitializeComponent();
        }

        /// <summary>The command line to start with, which is the tool currently in use.</summary>
        public string? CurrentCommand { get; set; }

        /// <summary>The command line chosen, or empty to fall back to git's own setting.</summary>
        public string ChosenCommand =>
            DialogResult == true && DataContext is DiffToolPickerViewModel model
                ? model.ResultCommand
                : string.Empty;

        /// <summary>
        /// Opens the picker over the given owner and returns the chosen command line, or
        /// <c>null</c> when it was cancelled.
        /// </summary>
        public static string? Choose(Window owner, string currentCommand)
        {
            DiffToolPickerViewModel model = new DiffToolPickerViewModel(
                new Services.DiffToolCatalog().Discover(),
                currentCommand ?? string.Empty,
                PickExecutable);

            DiffToolPickerWindow window = new DiffToolPickerWindow
            {
                DataContext = model,
                CurrentCommand = currentCommand,
                Owner = owner,
            };

            // The view model signals acceptance, so one place decides the dialog is finished rather
            // than this waiting on a result the caller then has to interpret.
            model.Accepted += (sender, e) => window.DialogResult = true;

            return window.ShowDialog() == true ? model.ResultCommand : null;
        }

        private static string? PickExecutable(string? initial) =>
            Services.ShellFilePicker.PickExecutable(initial);
    }
}