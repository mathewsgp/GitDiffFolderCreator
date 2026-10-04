using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Xunit;

namespace GitDiffFolderCreator.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public void A_command_refuses_to_run_twice_over_the_same_operation()
    {
        var gate = new ManualResetEventSlim();
        var started = 0;

        var command = new AsyncRelayCommand(
            async _ =>
            {
                Interlocked.Increment(ref started);
                await Task.Run(() => gate.Wait(TimeSpan.FromSeconds(10)));
            },
            _ => true);

        command.Execute(null);
        command.Execute(null);

        gate.Set();
        SpinUntil(() => !command.IsExecuting);

        Assert.Equal(1, started);
    }

    [Fact]
    public void A_command_that_throws_still_becomes_idle_again()
    {
        Exception? reported = null;
        var command = new AsyncRelayCommand(_ => throw new InvalidOperationException("boom"));
        command.ExecutionFailed += (_, ex) => reported = ex;

        command.Execute(null);

        Assert.False(command.IsExecuting);
        Assert.NotNull(reported);
    }

    /// <summary>
    /// An async body may end its awaits with ConfigureAwait(false), so the command's own state can be
    /// reset from the thread pool. WPF applies its CanExecuteChanged handlers straight to the bound
    /// controls, which throws unless the notification is marshalled back first.
    /// </summary>
    [Fact]
    public void State_changes_reported_off_the_ui_thread_are_marshalled_back_to_it()
    {
        var exceptions = new List<string>();
        var raised = new ManualResetEventSlim();

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var command = new AsyncRelayCommand(async _ => await Task.Run(() => { }));
                command.CanExecuteChanged += (_, __) =>
                {
                    // Stands in for a bound control, which only tolerates its own thread.
                    if (Dispatcher.CurrentDispatcher.CheckAccess() && Dispatcher.FromThread(Thread.CurrentThread) == null)
                    {
                        exceptions.Add("handler ran on a foreign thread");
                    }

                    raised.Set();
                };

                command.Execute(null);
                SpinUntil(() => !command.IsExecuting);
                raised.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The command never completed.");
        Assert.True(raised.IsSet, "CanExecuteChanged was never raised.");
        Assert.Null(failure);
        Assert.Empty(exceptions);
    }

    private static void SpinUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            Thread.Sleep(10);
        }
    }
}
