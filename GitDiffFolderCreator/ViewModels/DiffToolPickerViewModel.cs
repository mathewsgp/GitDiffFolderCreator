using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace GitDiffFolderCreator.ViewModels;

/// <summary>One row in the diff tool picker: a discovered tool, and whether it is the one in use.</summary>
public sealed class DiffToolRow : INotifyPropertyChanged
{
    private bool _isChosen;

    public DiffToolRow(Services.DiffToolChoice choice, bool isChosen)
    {
        Choice = choice;
        _isChosen = isChosen;
    }

    public Services.DiffToolChoice Choice { get; }

    public string DisplayName => Choice.DisplayName;

    public string Detail => Choice.Detail;

    public string Command => Choice.Command;

    /// <summary>True when this is the tool the application is currently set to.</summary>
    public bool IsChosen
    {
        get { return _isChosen; }
        set
        {
            if (_isChosen != value)
            {
                _isChosen = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The picker: choose which comparison tool double-clicking a changed file should open.
/// </summary>
/// <remarks>
/// Kept separate from the window so the list, the marking and the resulting command line can be
/// tested without showing a dialog. The window passes a folder-picker callback for "Browse", because
/// picking a file is the one thing here that needs a shell dialog.
/// </remarks>
public sealed class DiffToolPickerViewModel : INotifyPropertyChanged
{
    private readonly RelayCommand _acceptCommand;
    private readonly RelayCommand _browseCommand;
    private readonly RelayCommand _useGitConfigCommand;
    private readonly Func<string?, string?> _pickExecutable;

    private DiffToolRow? _selectedTool;

    public DiffToolPickerViewModel(
        IList<Services.DiffToolChoice> tools,
        string currentCommand,
        Func<string?, string?> pickExecutable)
    {
        _pickExecutable = pickExecutable;
        Tools = new ObservableCollection<DiffToolRow>();

        foreach (Services.DiffToolChoice choice in tools ?? new List<Services.DiffToolChoice>())
        {
            Tools.Add(new DiffToolRow(choice, Matches(choice.Command, currentCommand)));
        }

        // A tool chosen before but no longer found on disk is still the tool in use, so it stays in
        // the list rather than silently disappearing and leaving the setting pointing at nothing.
        if (currentCommand.Length > 0 && !Tools.Any(row => row.IsChosen))
        {
            Tools.Add(new DiffToolRow(
                new Services.DiffToolChoice(
                    "previous",
                    "Previously chosen",
                    currentCommand,
                    "No longer found on this computer"),
                true));
        }

        _acceptCommand = new RelayCommand(_ => Accept(), _ => SelectedTool != null);
        _browseCommand = new RelayCommand(_ => Browse(), null);
        _useGitConfigCommand = new RelayCommand(_ => UseGitConfiguration(), null);

        _selectedTool = Tools.FirstOrDefault(row => row.IsChosen) ?? Tools.FirstOrDefault();
    }

    public ObservableCollection<DiffToolRow> Tools { get; }

    /// <summary>The command line that will be saved, or empty to fall back to git's own setting.</summary>
    public string ResultCommand => SelectedTool?.Command ?? string.Empty;

    public DiffToolRow? SelectedTool
    {
        get { return _selectedTool; }
        set
        {
            if (!ReferenceEquals(_selectedTool, value))
            {
                _selectedTool = value;
                OnPropertyChanged();
                OnPropertyChanged("CommandPreview");
                _acceptCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CommandPreview => ResultCommand;

    public bool HasTools => Tools.Count > 0;

    public RelayCommand AcceptCommand => _acceptCommand;

    public RelayCommand BrowseCommand => _browseCommand;

    /// <summary>Clears the choice, so the tool configured for git is used instead.</summary>
    public RelayCommand UseGitConfigCommand => _useGitConfigCommand;

    /// <summary>True when the window may be closed with OK.</summary>
    public bool Confirmed { get; private set; }

    private void Accept()
    {
        Confirmed = true;
        Accepted?.Invoke(this, EventArgs.Empty);
    }

    private void Browse()
    {
        string? picked = _pickExecutable(null);

        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        Services.DiffToolChoice choice = Services.DiffToolCatalog.ForCustomTool(picked!);

        // Replace an earlier hand-picked tool rather than accumulating one per Browse.
        var existing = Tools.FirstOrDefault(
            row => row.Choice.Id.StartsWith("custom:", StringComparison.Ordinal));

        DiffToolRow row = new DiffToolRow(choice, true);

        if (existing != null)
        {
            Tools[Tools.IndexOf(existing)] = row;
        }
        else
        {
            Tools.Add(row);
        }

        foreach (DiffToolRow other in Tools)
        {
            other.IsChosen = ReferenceEquals(other, row);
        }

        SelectedTool = row;
        OnPropertyChanged("HasTools");
    }

    private void UseGitConfiguration()
    {
        foreach (DiffToolRow row in Tools)
        {
            row.IsChosen = false;
        }

        SelectedTool = null;
    }

    /// <summary>Raised when the user accepts, so the window can close itself.</summary>
    public event EventHandler? Accepted;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Whether two command lines name the same tool.
    /// </summary>
    /// <remarks>
    /// Compared as text rather than by identifier, so a tool picked by hand and then rediscovered as
    /// a known one is still recognised as the same tool once the paths agree.
    /// </remarks>
    private static bool Matches(string candidate, string current)
    {
        return current.Length > 0
            && string.Equals(candidate.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}