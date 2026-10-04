using System;
using System.Threading;

namespace GitDiffFolderCreator.Tests;

/// <summary>
/// Runs a test body on a single-threaded-apartment thread.
/// </summary>
/// <remarks>
/// A WPF <see cref="System.Windows.DependencyObject"/> refuses to be constructed off an STA thread,
/// because creating one reaches into the input manager and keyboard navigation. xUnit runs test
/// bodies on thread-pool threads, so anything that instantiates a control has to get onto a thread
/// of its own first.
/// </remarks>
internal static class Sta
{
    public static void Run(Action body)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("The STA test body did not finish within 30 seconds.");
        }

        if (failure != null)
        {
            throw failure;
        }
    }
}
