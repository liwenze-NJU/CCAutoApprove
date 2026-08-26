using System.Windows;
using System.Windows.Threading;
using CCAutoApprove.App.ViewModels;
using CCAutoApprove.App.Views;

namespace CCAutoApprove.App.Tests.Views;

public sealed class RecordsViewTests
{
    [Fact]
    public void RecordsView_BindsReadOnlyDetailsWithoutWritingBack()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new RecordsView { DataContext = new RecordsViewModel() };
                view.Measure(new Size(980, 640));
                view.Arrange(new Rect(0, 0, 980, 640));
                view.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        bool completed = thread.Join(TimeSpan.FromSeconds(10));

        Assert.True(completed, "RecordsView layout did not complete within ten seconds.");
        Assert.Null(failure);
    }
}
