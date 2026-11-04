using Jarvis.Contracts.Notch;
using Jarvis.Core.Notch;

namespace Jarvis.Tests.Notch;

public sealed class NotchParkingTests
{
    private static NotchState.Form Form(string status, double? progress = null)
        => new("Volunteer sign-up", [], status, new FormActions(_ => Task.FromResult(string.Empty), () => { }), progress);

    [Fact]
    public void AFormPutAside_ShowsItsProgressInThePill_AndKeepsTheLatestForComingBack()
    {
        var parked = Form("Filling the page… 2 in so far", 0.2);
        var later = Form("Filling the page… 5 in so far", 0.5);

        var (show, kept) = NotchParking.Next(later, parked, new NotchState.Parked("x", true));

        Assert.Equal(new NotchState.Parked("Filling the page… 5 in so far", true), show);
        Assert.Same(later, kept);
    }

    [Fact]
    public void WhenItsDone_ThePillSaysReady_AndStaysWhenTheNotchWouldRest()
    {
        var done = Form("Filled 12 of 12 · check the page, then submit it yourself.");

        Assert.Equal(new NotchState.Parked("Form ready · Filled 12 of 12", false), NotchParking.Next(done, Form("Filling…", 0.9), NotchState.Idle).Show);
        Assert.Equal(new NotchState.Parked("Form ready · Filled 12 of 12", false), NotchParking.Next(NotchState.Idle, done, NotchState.Idle).Show);
    }

    [Fact]
    public void WhileSomethingElseShows_ProgressWaitsQuietly_AndSomethingNewLetsGo()
    {
        var parked = Form("Filling…", 0.3);
        var (show, kept) = NotchParking.Next(Form("Filling…", 0.6), parked, new NotchState.Response("An answer"));
        Assert.Null(show); // the answer stays on screen
        Assert.NotNull(kept);

        Assert.Equal((new NotchState.Listening(), (NotchState?)null), NotchParking.Next(new NotchState.Listening(), parked, NotchState.Idle));
    }

    [Fact]
    public void WithNothingPutAside_EverythingShowsAsAsked()
    {
        var answer = new NotchState.Response("Hi");
        Assert.Equal((answer, (NotchState?)null), NotchParking.Next(answer, null, NotchState.Idle));
    }
}
