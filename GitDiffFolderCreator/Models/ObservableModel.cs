using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GitDiffFolderCreator.Models
{
    /// <summary>
    /// A model that can tell the UI when one of its values has changed.
    /// </summary>
    /// <remarks>
    /// The commit and file-change lists are <c>ObservableCollection</c>s of long-lived instances, so
    /// replacing a collection is not an option for telling the list that one row now reads
    /// differently - a commit that has just been resolved as the base has to repaint that row alone.
    /// </remarks>
    public abstract class ObservableModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Assigns <paramref name="value"/> and raises a change notification only if it differs.
        /// </summary>
        /// <returns>True when the field changed.</returns>
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
