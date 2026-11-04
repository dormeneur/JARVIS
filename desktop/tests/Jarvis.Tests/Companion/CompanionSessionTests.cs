using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Jarvis.Contracts.Audio;
using Jarvis.Contracts.Brain;
using Jarvis.Contracts.Capture;
using Jarvis.Contracts.Notch;
using Jarvis.Contracts.Overlay;
using Jarvis.Contracts.Speech;
using Jarvis.Core.Companion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jarvis.Tests.Companion;

public sealed class CompanionSessionTests
{
    [Fact]
    public async Task FullTurn_SpeaksEachSentence_PointsOnTheRightScreen_AndShowsCaptions()
    {
        var fakes = new Fakes("where are the settings", "It's the gear icon. [POINT:100,50:Settings:screen2] Click it.");
        await using var session = fakes.CreateSession();

        session.BeginListening();
        session.EndListening();
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("where are the settings", fakes.Brain.LastTurn!.UserText);
        Assert.Equal(2, fakes.Brain.LastTurn.Images.Count);
        Assert.StartsWith("Screen 1 of 2 (the user's mouse is on this one)", fakes.Brain.LastTurn.Images[0].Label, StringComparison.Ordinal);
        Assert.Equal(["It's the gear icon.", "Click it."], fakes.Speech.Spoken);
        Assert.Equal([false, true, false], fakes.Cursor.Listening.Take(3)); // cleared, shown while held, hidden on release
        // Screen 2 is a 1000x500 image of a 2000x1000 display at x=1920: (100,50) -> (1920+200, 0+100).
        Assert.Equal([(2120, 100)], fakes.Cursor.Points);
        Assert.Equal(["Settings"], fakes.Cursor.Labels);
        Assert.Contains(new NotchState.Thinking("Looking at your screen"), fakes.Notch.Shown);
        Assert.Contains(new NotchState.Response("It's the gear icon. Click it."), fakes.Notch.Shown);
    }

    [Fact]
    public async Task DrawingBlock_IsSketchedOnTheNamedScreen_AndNotSpoken()
    {
        var fakes = new Fakes("show me the steps", "Here are the two steps. [DRAW:screen2]\nnum 100 50 1\nring 200 100 100 40 \"Save\"\n[/DRAW] First the badge, then save.");
        await using var session = fakes.CreateSession();

        session.BeginListening();
        session.EndListening();
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var drawing = Assert.Single(fakes.Drawing.Shown);
        Assert.Single(drawing.Strokes); // the ring
        Assert.Equal(2, drawing.Labels.Count); // badge "1" and the ring's label
        Assert.Equal(new System.Numerics.Vector2(2120, 100), drawing.Labels[0].Anchor); // screen 2: image (100,50) -> desktop
        Assert.Equal(["Here are the two steps.", "First the badge, then save."], fakes.Speech.Spoken);
        Assert.True(fakes.Drawing.Clears >= 1, "pressing the key clears the previous drawing");
    }

    [Fact]
    public async Task AgentBlock_StartsABackgroundJob_AndOnlyTheAcknowledgementIsSpoken()
    {
        var fakes = new Fakes("plan my trip", "[AGENT]Plan a 3-day Lisbon trip, save it as output/plan.md.[/AGENT] On it, I'll let you know.");
        await using var session = fakes.CreateSession();

        session.BeginListening();
        session.EndListening();
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(["Plan a 3-day Lisbon trip, save it as output/plan.md."], fakes.Agents.Briefs);
        Assert.Equal(["On it, I'll let you know."], fakes.Speech.Spoken);
    }

    [Fact]
    public async Task SilentRecording_GoesBackToIdleWithoutAskingTheBrain()
    {
        var fakes = new Fakes("   ", "unused");
        await using var session = fakes.CreateSession();

        session.BeginListening();
        session.EndListening();
        await WaitUntil(() => fakes.Notch.Shown.LastOrDefault() == NotchState.Idle);

        Assert.Null(fakes.Brain.LastTurn);
    }

    [Fact]
    public async Task Cancel_StopsTheTurnAndClearsTheNotch()
    {
        var fakes = new Fakes("hello", "First. Second.") { BrainDelay = TimeSpan.FromSeconds(10) };
        await using var session = fakes.CreateSession();

        session.BeginListening();
        session.EndListening();
        await WaitUntil(() => fakes.Notch.Shown.OfType<NotchState.Thinking>().Any());
        session.Cancel();

        Assert.Equal(NotchState.Idle, fakes.Notch.Shown[^1]);
        Assert.Empty(fakes.Speech.Spoken);
    }

    [Fact]
    public async Task Compose_OpensTheTextBox_AndItsTextIsAskedWithoutTheMicrophone()
    {
        var fakes = new Fakes("unused", "It's top right.");
        await using var session = fakes.CreateSession();

        session.Compose();
        var composing = Assert.IsType<NotchState.Composing>(fakes.Notch.Shown[^1]);
        composing.Submit("where is the share button");
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("where is the share button", fakes.Brain.LastTurn!.UserText);
        Assert.Equal([SoundEffect.ComposerOpen, SoundEffect.ComposerSend, SoundEffect.AnswerReady], fakes.Sounds.Played);
        Assert.Equal(["It's top right."], fakes.Speech.Spoken);
    }

    [Fact]
    public async Task ClickingTheLeftBehindCursor_TalksHandsFree()
    {
        var fakes = new Fakes("what is this", "A button.");
        await using var session = fakes.CreateSession();

        fakes.Cursor.Click(); // start listening
        Assert.Contains(new NotchState.Listening(), fakes.Notch.Shown);
        fakes.Cursor.Click(); // finish: answer
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("what is this", fakes.Brain.LastTurn!.UserText);
    }

    [Fact]
    public async Task RoutineBlock_SavesARoutine_AndOnlyTheConfirmationIsSpoken()
    {
        var fakes = new Fakes("every weekday at nine, summarise tech news", "[ROUTINE:weekdays 09:00]Summarise today's top tech news in five bullets.[/ROUTINE] Done, every weekday at nine.");
        await using var session = fakes.CreateSession();

        session.Ask("every weekday at nine, summarise tech news");
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var routine = Assert.Single(fakes.Routines.All);
        Assert.Equal("weekdays 09:00", routine.Schedule);
        Assert.Equal(["Done, every weekday at nine."], fakes.Speech.Spoken);
        Assert.Empty(fakes.Agents.Briefs); // nothing runs until the first scheduled time
    }

    [Fact]
    public async Task ARecordingNobodyEnds_StopsAtTheLimit_AndIsAnswered()
    {
        var fakes = new Fakes("what's this", "A chart.");
        await using var session = fakes.CreateSession(maxRecording: TimeSpan.FromMilliseconds(300));

        session.BeginListening(); // e.g. click-to-talk, then the user walked away
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("what's this", fakes.Brain.LastTurn!.UserText);
    }

    [Fact]
    public async Task EveryLook_IsLoggedWithItsReason_NeverWhatWasSaid()
    {
        var fakes = new Fakes("unused", "It's there.");
        await using var session = fakes.CreateSession();

        session.Ask("where is my secret project");
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var look = Assert.Single(fakes.Privacy.Recent());
        Assert.Equal(2, look.Displays);
        Assert.Equal("You typed a question", look.Reason);
        Assert.DoesNotContain("secret", File.ReadAllText(fakes.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScreenAccessOff_TakesNoScreenshot_AndTellsTheBrain()
    {
        var fakes = new Fakes("unused", "Tell me what you see.");
        fakes.PrivacyOptions.ScreenAccess = false;
        await using var session = fakes.CreateSession();

        session.Ask("what is this");
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(fakes.Brain.LastTurn!.Images);
        Assert.StartsWith(CompanionPrompt.ScreenOff, fakes.Brain.LastTurn.UserText, StringComparison.Ordinal);
        Assert.Empty(fakes.Privacy.Recent());
        Assert.DoesNotContain(fakes.Notch.Shown.OfType<NotchState.Thinking>(), thinking => thinking.Caption is not null);
    }

    [Fact]
    public async Task WhatTheUserCircled_IsToldToTheBrain()
    {
        var fakes = new Fakes("what's this", "That's the logo.");
        fakes.Cursor.Highlight = new Jarvis.Contracts.Overlay.ScreenHighlight(100, 100, 300, 200, IsPoint: false);
        await using var session = fakes.CreateSession();

        session.BeginListening();
        session.EndListening();
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.StartsWith("[While talking, the user circled the area", fakes.Brain.LastTurn!.UserText, StringComparison.Ordinal);
        Assert.EndsWith("what's this", fakes.Brain.LastTurn.UserText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Answers_GoIntoTheChat_AndTheTextBoxShowsThem()
    {
        var fakes = new Fakes("unused", "It's top right. [POINT:10,10:Share]");
        await using var session = fakes.CreateSession();

        session.Ask("where is share");
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await WaitUntil(() => fakes.History.Recent.Count == 1);
        session.Compose();

        var composing = Assert.IsType<NotchState.Composing>(fakes.Notch.Shown[^1]);
        Assert.Equal([("where is share", "It's top right.")], composing.History!.Select(turn => (turn.Question, turn.Answer)));
    }

    [Fact]
    public async Task DriveSteps_HappenOnlyAfterDoIt_AndStopEndsThem()
    {
        var fakes = new Fakes("unused", "I'll open it. [DO:click 100,50:Settings:screen2] [DO:type:hello]");
        fakes.PrivacyOptions.LetJarvisDrive = true;
        await using var session = fakes.CreateSession();

        session.Ask("open settings");
        await WaitUntil(() => fakes.Notch.Shown.OfType<NotchState.Response>().Any(r => r.Actions is { Count: 2 }));
        var first = fakes.Notch.Shown.OfType<NotchState.Response>().Last(r => r.Actions is { Count: 2 });
        Assert.Equal("Step 1 of 2: Click \"Settings\"?", first.Text);
        Assert.Empty(fakes.Control.Done); // nothing before approval

        first.Actions![0].Invoke(); // Do it
        await WaitUntil(() => fakes.Notch.Shown.OfType<NotchState.Response>().Count(r => r.Actions is { Count: 2 }) == 2);
        Assert.Equal([new Jarvis.Contracts.Control.ComputerAction(Jarvis.Contracts.Control.ComputerActionKind.Click, 2120, 100, null)], fakes.Control.Done);

        fakes.Notch.Shown.OfType<NotchState.Response>().Last(r => r.Actions is { Count: 2 }).Actions![1].Invoke(); // Stop
        await WaitUntil(() => fakes.Notch.Shown.Contains(new NotchState.Response("Stopped. I didn't do the rest.")));
        Assert.Single(fakes.Control.Done); // the typing never happened
    }

    [Fact]
    public async Task BrowserSteps_NameWhatTheyActOn_AndGoThroughTheExtensionAfterDoIt()
    {
        var fakes = new Fakes("unused", "Pressing it. [DO:press:e2]");
        fakes.PrivacyOptions.LetJarvisDrive = true;
        fakes.Browser.IsConnected = true;
        fakes.Browser.IsBrowserInFront = true;
        fakes.Browser.Page = new Jarvis.Contracts.Browser.BrowserPage("https://x.test", "Shop", string.Empty, "Buy things",
            [new("e1", "link", "Home", true), new("e2", "button", "Add to list", true)]);
        await using var session = fakes.CreateSession();

        session.Ask("add it to my list");
        await WaitUntil(() => fakes.Notch.Shown.OfType<NotchState.Response>().Any(r => r.Actions is { Count: 2 }));
        var ask = fakes.Notch.Shown.OfType<NotchState.Response>().Last(r => r.Actions is { Count: 2 });

        Assert.Equal("Press \"Add to list\" on the page?", ask.Text);
        Assert.Contains("e2 button \"Add to list\"", fakes.Brain.LastTurn!.UserText, StringComparison.Ordinal);
        Assert.Empty(fakes.Browser.Acts);
        ask.Actions![0].Invoke(); // Do it
        await WaitUntil(() => fakes.Browser.Acts.Count == 1);
        Assert.Equal(new Jarvis.Contracts.Browser.BrowserAct("press", "e2", null), fakes.Browser.Acts[0]);
        Assert.Empty(fakes.Control.Done); // not the mouse
    }

    [Fact]
    public async Task AStepThatFails_GetsAFreshLookAndAnotherWay_InsteadOfEndingTheTask()
    {
        var fakes = new Fakes("unused", "Opening it. [DO:press:e2] [MORE]");
        fakes.PrivacyOptions.LetJarvisDrive = true;
        fakes.Browser.IsConnected = true;
        fakes.Browser.IsBrowserInFront = true;
        fakes.Browser.Page = new Jarvis.Contracts.Browser.BrowserPage("https://mail.test", "Inbox", string.Empty, "Inbox",
            [new("e1", "link", "Inbox", true), new("e2", "item", "noreply Verify your email", true)]);
        fakes.Browser.Results.Enqueue(new(false, null, null, Reason: "it didn't respond to the press"));
        fakes.Brain.Next.Enqueue("Opening it. [DO:press:e2] [MORE]");
        fakes.Brain.Next.Enqueue("Trying the link instead. [DO:press:e1]");
        await using var session = fakes.CreateSession();

        session.Ask("open the verification email");
        await WaitUntil(() => fakes.Notch.Shown.OfType<NotchState.Response>().Any(r => r.Actions is { Count: 2 }));
        fakes.Notch.Shown.OfType<NotchState.Response>().Last(r => r.Actions is { Count: 2 }).Actions![0].Invoke(); // Take control / Do it
        await WaitUntil(() => fakes.Browser.Acts.Count >= 1 && fakes.Notch.Shown.OfType<NotchState.Response>().Last(r => r.Actions is { Count: 2 }) is var ask && ask.Text.Contains("Inbox", StringComparison.Ordinal));
        fakes.Notch.Shown.OfType<NotchState.Response>().Last(r => r.Actions is { Count: 2 }).Actions![0].Invoke();
        await WaitUntil(() => fakes.Browser.Acts.Count == 2);

        Assert.Contains("didn't work (it didn't respond to the press)", fakes.Brain.LastTurn!.UserText, StringComparison.Ordinal);
        Assert.Equal("e1", fakes.Browser.Acts[1].Target);
    }

    [Fact]
    public async Task DriveOff_StepsAreIgnored_AndTheBrainIsNotInvited()
    {
        var fakes = new Fakes("unused", "Click Settings. [DO:click 100,50:Settings]");
        await using var session = fakes.CreateSession();

        session.Ask("open settings");
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(CompanionPrompt.DriveOn, fakes.Brain.LastTurn!.UserText, StringComparison.Ordinal);
        Assert.Empty(fakes.Control.Done);
        Assert.DoesNotContain(fakes.Notch.Shown.OfType<NotchState.Response>(), r => r.Actions is not null);
    }

    [Fact]
    public async Task BrowserInFront_ItsPageIsReadForTheBrain_AndLogged()
    {
        var fakes = new Fakes("unused", "It's a recipe.");
        fakes.Browser.IsConnected = fakes.Browser.IsBrowserInFront = true;
        fakes.Browser.Page = new Jarvis.Contracts.Browser.BrowserPage("https://example.com/soup", "Soup", "", "Tomato soup recipe");
        await using var session = fakes.CreateSession();

        session.Ask("what is this page");
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.StartsWith("[The browser tab in front of the user: \"Soup\" (https://example.com/soup).", fakes.Brain.LastTurn!.UserText, StringComparison.Ordinal);
        Assert.Contains("Tomato soup recipe", fakes.Brain.LastTurn.UserText, StringComparison.Ordinal);
        Assert.Contains(fakes.Privacy.Recent(), look => look.Reason == "Read the page open in your browser");
    }

    [Fact]
    public async Task BrowserReading_Off_LeavesThePageAlone()
    {
        var fakes = new Fakes("unused", "Ok.");
        fakes.Browser.IsConnected = fakes.Browser.IsBrowserInFront = true;
        fakes.Browser.Page = new Jarvis.Contracts.Browser.BrowserPage("https://example.com", "Bank", "", "balance 1000");
        fakes.BrowserOptions.ReadPage = false;
        await using var session = fakes.CreateSession();

        session.Ask("hi");
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("balance", fakes.Brain.LastTurn!.UserText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlockedMicrophone_SaysWhy_AndOffersTheWindowsSetting()
    {
        var fakes = new Fakes("unused", "unused") { Microphone = new BlockedMicrophone() };
        await using var session = fakes.CreateSession();

        session.BeginListening();
        session.EndListening();

        var shown = Assert.IsType<NotchState.Response>(fakes.Notch.Shown[^1]);
        Assert.Contains("Let desktop apps access your microphone", shown.Text, StringComparison.Ordinal);
        Assert.Equal("Open Windows settings", shown.Actions![0].Label);
        Assert.Null(fakes.Brain.LastTurn);
    }

    [Fact]
    public async Task AskOnce_TakesControl_RunsRoundsUntilDone_WithTheBorder()
    {
        var fakes = new Fakes("unused", "unused");
        fakes.Brain.Next.Enqueue("On it. [DO:type:a] [MORE]");
        fakes.Brain.Next.Enqueue("Nearly. [DO:type:b]");
        fakes.PrivacyOptions.LetJarvisDrive = true;
        fakes.PrivacyOptions.AskEveryStep = false;
        await using var session = fakes.CreateSession();

        session.Ask("do the thing");
        await WaitUntil(() => fakes.Notch.Shown.OfType<NotchState.Response>().Any(r => r.Actions is { Count: 2 }));
        var ask = fakes.Notch.Shown.OfType<NotchState.Response>().Last(r => r.Actions is { Count: 2 });
        Assert.Equal("Take control", ask.Actions![0].Label);
        Assert.Empty(fakes.Control.Done);

        ask.Actions[0].Invoke();
        await WaitUntil(() => fakes.Notch.Shown.Contains(new NotchState.Response("Done.")));

        Assert.Equal(["a", "b"], fakes.Control.Done.Select(action => action.Text));
        Assert.Equal([true, false], fakes.Cursor.Border);
        Assert.Contains(CompanionPrompt.ContinueTask("do the thing", 1)[..30], fakes.Brain.LastTurn!.UserText, StringComparison.Ordinal);
        Assert.Single(fakes.Notch.Shown.OfType<NotchState.Response>(), r => r.Actions is { Count: 2 }); // asked once
    }

    [Fact]
    public async Task TakingBackTheMouse_StopsTheTaskAtOnce()
    {
        var fakes = new Fakes("unused", "On it. [DO:type:a] [DO:type:b] [DO:type:c]");
        fakes.PrivacyOptions.LetJarvisDrive = true;
        fakes.PrivacyOptions.AskEveryStep = false;
        fakes.Control.TakeBackAfter = 1;
        await using var session = fakes.CreateSession();

        session.Ask("do the thing");
        await WaitUntil(() => fakes.Notch.Shown.OfType<NotchState.Response>().Any(r => r.Actions is { Count: 2 }));
        fakes.Notch.Shown.OfType<NotchState.Response>().Last(r => r.Actions is { Count: 2 }).Actions![0].Invoke();
        await WaitUntil(() => fakes.Notch.Shown.Contains(new NotchState.Response("You took back control. I stopped there.")));

        Assert.Single(fakes.Control.Done);
        Assert.Equal([true, false], fakes.Cursor.Border);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task VaultQuestion_IsAnsweredOnThisPc_WithoutAScreenshotOrClaude()
    {
        var fakes = new Fakes("what's in my notes about Lisbon", "Claude would say this.");
        var vaultBrain = new FakeBrain("Your notes say May.");
        fakes.UseLocal(vaultBrain, new FakeBrain("Local would say this."));
        await using var session = fakes.CreateSession();

        session.BeginListening();
        session.EndListening();
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Null(fakes.Brain.LastTurn);
        Assert.Empty(vaultBrain.LastTurn!.Images);
        Assert.Equal(["Your notes say May."], fakes.Speech.Spoken);
        Assert.Equal("Answered from your vault on this PC", fakes.Privacy.Recent()[0].Reason);
    }

    [Fact]
    public async Task CalendarQuestion_IsAnsweredFromTheFeed_WithoutAScreenshotOrClaude()
    {
        var fakes = new Fakes("what's on my calendar on 26th october", "Claude would say this.");
        var calendar = new FakeBrain("1 on your calendar on Sunday, 26 October: 10:00 AM, Standup.");
        fakes.UseLocal(new FakeBrain("unused"), new FakeBrain("unused"), calendar);
        await using var session = fakes.CreateSession();

        session.BeginListening();
        session.EndListening();
        await fakes.Player.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Null(fakes.Brain.LastTurn);
        Assert.Empty(calendar.LastTurn!.Images);
        Assert.DoesNotContain(fakes.Notch.Shown, state => state is NotchState.Thinking { Caption: "Looking at your screen" });
        Assert.Equal("Answered from your calendar on this PC", fakes.Privacy.Recent()[0].Reason);
    }

    private const string GoogleIcal = "https://calendar.google.com/calendar/ical/asha%40example.test/private-abc/basic.ics";

    [Fact]
    public async Task CalendarAnswer_OffersToOpenThatDayInTheCalendar()
    {
        var fakes = new Fakes("what's on my calendar on 26th october", "unused");
        fakes.UseLocal(new FakeBrain("unused"), new FakeBrain("unused"), new FakeBrain("Nothing on your calendar on Monday, 26 October."), GoogleIcal);
        await using var session = fakes.CreateSession();

        session.BeginListening();
        session.EndListening();
        await WaitUntil(() => fakes.Notch.Shown.OfType<NotchState.Response>().Any(r => r.Actions is { Count: 1 }));
        var answer = fakes.Notch.Shown.OfType<NotchState.Response>().Last();
        answer.Actions![0].Invoke();

        Assert.Equal("Open Google Calendar", answer.Actions[0].Label);
        Assert.Contains(fakes.Control.Done, action => action.Text is { } url && url.Contains("/day/", StringComparison.Ordinal) && url.EndsWith("/10/26?authuser=asha%40example.test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddingAnEvent_ShowsItToCheck_AndOnlyAddOpensTheCalendar()
    {
        var fakes = new Fakes("add lunch with Sam tomorrow at 1pm to my calendar", """{"title": "Lunch with Sam", "date": "2030-01-15", "start": "13:00", "end": null, "allDay": false, "location": "", "notes": ""}""");
        fakes.UseLocal(new FakeBrain("unused"), new FakeBrain("unused"), null, GoogleIcal);
        await using var session = fakes.CreateSession();

        session.BeginListening();
        session.EndListening();
        await WaitUntil(() => fakes.Notch.Shown.OfType<NotchState.EventDraft>().Any());
        var draft = fakes.Notch.Shown.OfType<NotchState.EventDraft>().Last();

        Assert.Equal("Lunch with Sam", draft.Event.Title);
        Assert.Equal(13, draft.Event.Start.Hour);
        Assert.Equal(TimeSpan.FromHours(1), draft.Event.End - draft.Event.Start); // no end said: an hour
        Assert.Equal("Google Calendar (asha@example.test)", draft.Destination);
        Assert.Empty(fakes.Control.Done); // nothing before Add

        draft.Add(draft.Event with { Title = "Lunch with Sam and Ana" }); // edited in the card
        await WaitUntil(() => fakes.Control.Done.Count == 1);
        Assert.Contains("text=Lunch%20with%20Sam%20and%20Ana", fakes.Control.Done[0].Text, StringComparison.Ordinal);
        Assert.Contains("action=TEMPLATE", fakes.Control.Done[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingTheEvent_AddsNothing()
    {
        var fakes = new Fakes("schedule a call with the bank on friday", "not json at all");
        fakes.UseLocal(new FakeBrain("unused"), new FakeBrain("unused"), null, GoogleIcal);
        await using var session = fakes.CreateSession();

        session.BeginListening();
        session.EndListening();
        await WaitUntil(() => fakes.Notch.Shown.OfType<NotchState.EventDraft>().Any());
        var draft = fakes.Notch.Shown.OfType<NotchState.EventDraft>().Last();
        Assert.Equal(DayOfWeek.Friday, draft.Event.Start.DayOfWeek); // read without the brain
        draft.Cancel();
        await WaitUntil(() => fakes.Notch.Shown.Contains(new NotchState.Response("Okay, I didn't add it.")));

        Assert.Empty(fakes.Control.Done);
    }

    [Fact]
    public async Task OpeningAnApp_HappensOnThisPc_WithoutClaudeOrAScreenshot_AndOffersNextSteps()
    {
        var fakes = new Fakes("open spotify", "Claude would say this.");
        var apps = new OneApp();
        fakes.UseLocal(new FakeBrain("unused"), new FakeBrain("unused"), apps: apps);
        await using var session = fakes.CreateSession();

        session.BeginListening();
        session.EndListening();
        await WaitUntil(() => fakes.Notch.Shown.OfType<NotchState.Response>().Any(r => r.Actions is { Count: > 0 }));
        var offer = fakes.Notch.Shown.OfType<NotchState.Response>().Last();
        offer.Actions![0].Invoke();

        Assert.Null(fakes.Brain.LastTurn); // Claude never asked
        Assert.Equal(["Spotify"], apps.Launched);
        Assert.Equal("Opened Spotify.", offer.Text);
        Assert.Equal(["spotify:collection:tracks"], apps.Opened);
    }

    private sealed class OneApp : Jarvis.Core.Assistant.IApps
    {
        public List<string> Launched { get; } = [];

        public List<string> Opened { get; } = [];

        public IReadOnlyList<Jarvis.Core.Assistant.InstalledApp> All { get; } = [new("Spotify", "spotify")];

        public bool PressKey(string key) => true;

        public void EnsureVolume(int percent)
        {
        }

        public bool Launch(Jarvis.Core.Assistant.InstalledApp app)
        {
            Launched.Add(app.Name);
            return true;
        }

        public bool Open(string uri)
        {
            Opened.Add(uri);
            return true;
        }
    }

    [Fact]
    public async Task PrivateMode_AnswersLocally_AndAsksBeforeSendingTheScreenToClaude()
    {
        var fakes = new Fakes("what does this error mean", "It means the file is locked.");
        fakes.PrivacyOptions.PrivateMode = true;
        var local = new FakeBrain("[SCREEN]");
        fakes.UseLocal(new FakeBrain("unused"), local);
        await using var session = fakes.CreateSession();

        session.BeginListening();
        session.EndListening();
        await WaitUntil(() => fakes.Notch.Shown.OfType<NotchState.Response>().Any(r => r.Actions is { Count: 2 }));

        Assert.Null(fakes.Brain.LastTurn); // nothing went to Claude yet
        Assert.Equal(CompanionPrompt.PrivateInstructions, local.LastTurn!.Instructions);
        Assert.Empty(local.LastTurn.Images);
        fakes.Notch.Shown.OfType<NotchState.Response>().Last(r => r.Actions is { Count: 2 }).Actions![0].Invoke(); // Send to Claude
        await WaitUntil(() => fakes.Speech.Spoken.Contains("It means the file is locked."));

        Assert.Equal(2, fakes.Brain.LastTurn!.Images.Count);
    }

    [Fact]
    public async Task PrivateMode_DecliningTheScreenshot_KeepsTheTurnOnThisPc()
    {
        var fakes = new Fakes("what does this error mean", "Claude would say this.");
        fakes.PrivacyOptions.PrivateMode = true;
        fakes.UseLocal(new FakeBrain("unused"), new FakeBrain("[SCREEN]"));
        await using var session = fakes.CreateSession();

        session.BeginListening();
        session.EndListening();
        await WaitUntil(() => fakes.Notch.Shown.OfType<NotchState.Response>().Any(r => r.Actions is { Count: 2 }));
        fakes.Notch.Shown.OfType<NotchState.Response>().Last(r => r.Actions is { Count: 2 }).Actions![1].Invoke(); // No
        await WaitUntil(() => fakes.Notch.Shown.LastOrDefault() == NotchState.Idle);

        Assert.Null(fakes.Brain.LastTurn);
        Assert.Contains(fakes.Notch.Shown, state => state is NotchState.Response { Text: var text } && text.StartsWith("Okay, I kept it on this PC", StringComparison.Ordinal));
    }

    private sealed class Fakes(string transcript, string reply)
    {
        public LocalTurns? Local { get; private set; }

        public void UseLocal(IAssistantBrain vaultBrain, IAssistantBrain localBrain, IAssistantBrain? calendar = null, string calendarAddress = "", Jarvis.Core.Assistant.IApps? apps = null)
        {
            var vault = new Jarvis.Tests.Vault.FakeVault();
            Local = new LocalTurns(vault, vaultBrain, localBrain, new Jarvis.Core.Vault.FormFiller(new Jarvis.Core.Vault.FormServices(vault, Browser, Notch), TimeProvider.System, NullLogger<Jarvis.Core.Vault.FormFiller>.Instance), calendar ?? new FakeBrain("No calendar."), () => calendarAddress, Actions: apps is null ? null : new Jarvis.Core.Assistant.LocalActions(apps));
        }

        public TimeSpan BrainDelay { get; init; }

        public FakeBrain Brain { get; } = new(reply);

        public FakeSpeech Speech { get; } = new();

        public FakePlayer Player { get; } = new();

        public FakeCursor Cursor { get; } = new();

        public FakeSounds Sounds { get; } = new();

        public FakeRoutines Routines { get; } = new();

        private ScreenPrivacy? _privacy;

        public PrivacyOptions PrivacyOptions { get; } = new();

        public ConversationLog History { get; } = new();

        public FakeControl Control { get; } = new();

        public IMicrophone Microphone { get; set; } = new FakeMicrophone();

        public FakeBrowser Browser { get; } = new();

        public BrowserOptions BrowserOptions { get; } = new();

        public string LogPath { get; } = Path.Combine(Path.GetTempPath(), $"jarvis-looks-{Guid.NewGuid():N}.log");

        public ScreenPrivacy Privacy => _privacy ??= new ScreenPrivacy(new PrivacyMonitor(PrivacyOptions), LogPath, TimeProvider.System);

        public FakeNotch Notch { get; } = new();

        public FakeDrawing Drawing { get; } = new();

        public FakeAgents Agents { get; } = new();

        public CompanionSession CreateSession(TimeSpan? maxRecording = null)
        {
            Brain.Delay = BrainDelay;
            var options = new OptionsMonitor(new CompanionOptions { CaptionSeconds = 0 });
            var services = new CompanionServices(Microphone, new FakeStt(transcript), new FakeCapture(), Brain, Speech, Player, Cursor, Drawing, Notch, Agents, Sounds, Routines, Privacy, History, Control, Browser, new BrowserMonitor(BrowserOptions), Local);
            return new CompanionSession(services, options, new CursorMonitor(), TimeProvider.System, NullLogger<CompanionSession>.Instance)
            {
                MaxRecording = maxRecording ?? TimeSpan.FromSeconds(120),
            };
        }
    }

    private sealed class FakeMicrophone : IMicrophone
    {
        public string DeviceName => "Test microphone";

        public IMicrophoneSession Start(AudioFormat format) => new Session();

        private sealed class Session : IMicrophoneSession
        {
            private readonly Channel<ReadOnlyMemory<byte>> _audio = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

            public Session() => _audio.Writer.TryWrite(new byte[320]);

            public ChannelReader<ReadOnlyMemory<byte>> Audio => _audio.Reader;

            public ValueTask DisposeAsync()
            {
                _audio.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeStt(string transcript) : ISpeechToText
    {
        public AudioFormat InputFormat => AudioFormat.SpeechMono16K;

        public Task<ITranscriptionSession> StartSessionAsync(TranscriptionRequest request, CancellationToken cancellationToken)
            => Task.FromResult<ITranscriptionSession>(new Session(transcript));

        private sealed class Session(string transcript) : ITranscriptionSession
        {
            public ValueTask WriteAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken) => ValueTask.CompletedTask;

            public Task<string> CompleteAsync(CancellationToken cancellationToken) => Task.FromResult(transcript);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCapture : IScreenCapture
    {
        public Task<IReadOnlyList<CapturedScreen>> CaptureAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CapturedScreen>>(
            [
                new CapturedScreen(new ScreenImage(new byte[] { 1 }, 1280, 720, HasCursor: true), 0, 0, 1920, 1080),
                new CapturedScreen(new ScreenImage(new byte[] { 2 }, 1000, 500, HasCursor: false), 1920, 0, 2000, 1000),
            ]);
    }

    private sealed class FakeBrain(string reply) : IAssistantBrain
    {
        public TimeSpan Delay { get; set; }

        public BrainTurn? LastTurn { get; private set; }

        /// <summary>Replies for successive turns; once empty, the constructor's reply.</summary>
        public Queue<string> Next { get; } = new();

        public async IAsyncEnumerable<BrainEvent> RunTurnAsync(BrainTurn turn, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastTurn = turn;
            await Task.Delay(Delay, cancellationToken);
            var text = Next.TryDequeue(out var queued) ? queued : reply;
            foreach (var word in text.Split(' '))
            {
                yield return new BrainEvent.TextDelta(word + " ");
            }

            yield return new BrainEvent.Completed();
        }
    }

    private sealed class FakeSpeech : ITextToSpeech
    {
        public List<string> Spoken { get; } = [];

        public AudioFormat OutputFormat => new(24_000, 1);

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, SpeechStyle style, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            Spoken.Add(text);
            yield return new byte[48];
        }
    }

    private sealed class FakePlayer : IAudioPlayer
    {
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PlayAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> pcm16, AudioFormat format, CancellationToken cancellationToken)
        {
            await foreach (var _ in pcm16.WithCancellation(cancellationToken))
            {
            }

            Finished.TrySetResult();
        }
    }

    private sealed class FakeCursor : ICursorOverlay
    {
        public List<(int X, int Y)> Points { get; } = [];

        public void SetThinking(bool thinking)
        {
        }

        public event Action? Clicked;

        public List<bool> Listening { get; } = [];

        public List<bool> Border { get; } = [];

        public void ShowControlBorder(bool on) => Border.Add(on);

        public void Click() => Clicked?.Invoke();

        public Jarvis.Contracts.Overlay.ScreenHighlight? Highlight { get; set; }

        public Jarvis.Contracts.Overlay.ScreenHighlight? TakeHighlight()
        {
            var highlight = Highlight;
            Highlight = null;
            return highlight;
        }

        public void SetListening(bool listening) => Listening.Add(listening);

        public void ShowLevel(double level)
        {
        }

        public List<string?> Labels { get; } = [];

        public void PointAt(int x, int y, string? label = null)
        {
            Points.Add((x, y));
            Labels.Add(label);
        }

        public void ReturnToMouse()
        {
        }
    }

    private sealed class FakeDrawing : IScreenDrawing
    {
        public List<ScreenDrawing> Shown { get; } = [];

        public int Clears { get; private set; }

        public void Show(ScreenDrawing drawing) => Shown.Add(drawing);

        public void Clear() => Clears++;
    }

    private sealed class FakeAgents : Jarvis.Core.Agents.IAgentJobs
    {
        public List<string> Briefs { get; } = [];

        public IReadOnlyList<Jarvis.Core.Agents.AgentJobInfo> Jobs => [];

        public event Action<Jarvis.Core.Agents.AgentJobInfo>? Changed
        {
            add { }
            remove { }
        }

        public Jarvis.Core.Agents.AgentJobInfo Start(string brief)
        {
            Briefs.Add(brief);
            return new(Guid.NewGuid(), brief, "folder", Jarvis.Core.Agents.AgentJobStatus.Queued, DateTimeOffset.Now);
        }

        public void Cancel(Guid id)
        {
        }
    }

    private sealed class CursorMonitor : IOptionsMonitor<Jarvis.Core.Cursor.CursorOptions>
    {
        public Jarvis.Core.Cursor.CursorOptions CurrentValue { get; } = new();

        public Jarvis.Core.Cursor.CursorOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<Jarvis.Core.Cursor.CursorOptions, string?> listener) => null;
    }

    private sealed class FakeNotch : INotchPresenter
    {
        private readonly Lock _gate = new();
        private readonly List<NotchState> _shown = [];

        public List<NotchState> Shown
        {
            get
            {
                lock (_gate)
                {
                    return [.. _shown];
                }
            }
        }

        public void Show(NotchState state)
        {
            lock (_gate)
            {
                _shown.Add(state);
            }
        }
    }

    private sealed class OptionsMonitor(CompanionOptions value) : IOptionsMonitor<CompanionOptions>
    {
        public CompanionOptions CurrentValue => value;

        public CompanionOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<CompanionOptions, string?> listener) => null;
    }

    private sealed class FakeSounds : ISoundEffects
    {
        public List<SoundEffect> Played { get; } = [];

        public void Play(SoundEffect effect) => Played.Add(effect);
    }

    private sealed class FakeRoutines : Jarvis.Core.Agents.IRoutineStore
    {
        private readonly List<Jarvis.Core.Agents.Routine> _routines = [];

        public IReadOnlyList<Jarvis.Core.Agents.Routine> All => _routines;

        public void Add(Jarvis.Core.Agents.Routine routine) => _routines.Add(routine);

        public void Remove(string id) => _routines.RemoveAll(routine => routine.Id == id);

        public void MarkRun(string id, DateTimeOffset at)
        {
        }
    }

    private sealed class PrivacyMonitor(PrivacyOptions value) : IOptionsMonitor<PrivacyOptions>
    {
        public PrivacyOptions CurrentValue => value;

        public PrivacyOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<PrivacyOptions, string?> listener) => null;
    }

    private sealed class FakeControl : Jarvis.Contracts.Control.IComputerControl
    {
        public List<Jarvis.Contracts.Control.ComputerAction> Done { get; } = [];

        /// <summary>Simulates the user grabbing the mouse right after this many steps.</summary>
        public int? TakeBackAfter { get; set; }

        private Action? _tookBack;

        public bool Perform(Jarvis.Contracts.Control.ComputerAction action)
        {
            Done.Add(action);
            if (Done.Count == TakeBackAfter)
            {
                _tookBack?.Invoke();
            }

            return true;
        }

        public IDisposable WatchForUser(Action tookBack)
        {
            _tookBack = tookBack;
            return new Unwatch(() => _tookBack = null);
        }

        private sealed class Unwatch(Action stop) : IDisposable
        {
            public void Dispose() => stop();
        }
    }

    private sealed class FakeBrowser : Jarvis.Contracts.Browser.IBrowserLink
    {
        public bool IsConnected { get; set; }

        public bool IsBrowserInFront { get; set; }

        public Jarvis.Contracts.Browser.BrowserPage? Page { get; set; }

        public List<string> Opened { get; } = [];

        public List<Jarvis.Contracts.Browser.BrowserAct> Acts { get; } = [];

        /// <summary>How the next steps go; once empty, they work.</summary>
        public Queue<Jarvis.Contracts.Browser.BrowserActResult> Results { get; } = new();

        public Task<Jarvis.Contracts.Browser.BrowserActResult?> ActAsync(Jarvis.Contracts.Browser.BrowserAct act, CancellationToken cancellationToken)
        {
            Acts.Add(act);
            return Task.FromResult<Jarvis.Contracts.Browser.BrowserActResult?>(Results.TryDequeue(out var result) ? result : new(true, 10, 20));
        }

        public Task<Jarvis.Contracts.Browser.BrowserPage?> ReadPageAsync(CancellationToken cancellationToken) => Task.FromResult(Page);

        public Task<bool> OpenAsync(string url, CancellationToken cancellationToken)
        {
            Opened.Add(url);
            return Task.FromResult(true);
        }
    }

    private sealed class BrowserMonitor(BrowserOptions value) : IOptionsMonitor<BrowserOptions>
    {
        public BrowserOptions CurrentValue => value;

        public BrowserOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<BrowserOptions, string?> listener) => null;
    }

    private sealed class BlockedMicrophone : IMicrophone
    {
        public string DeviceName => "Blocked";

        public IMicrophoneSession Start(AudioFormat format) => throw new MicrophoneBlockedException();
    }
}
