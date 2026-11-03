using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Jarvis.Contracts.Browser;
using Jarvis.Contracts.Notch;
using Jarvis.Contracts.Text;
using Jarvis.Contracts.Vault;
using Jarvis.Core.Vault;
using Jarvis.Providers.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jarvis.Tests.Vault;

public sealed class TurnRouterTests
{
    [Theory]
    [InlineData("fill this form", TurnRoute.Form)]
    [InlineData("Can you help me fill in the form?", TurnRoute.Form)]
    [InlineData("answers for this form please", TurnRoute.Form)]
    [InlineData("what's in my notes about Lisbon", TurnRoute.Vault)]
    [InlineData("search my vault for the wifi password", TurnRoute.Vault)]
    [InlineData("what did I write in my journal yesterday", TurnRoute.Vault)]
    [InlineData("Phil this form", TurnRoute.Form)]
    [InlineData("help me with this application", TurnRoute.Form)]
    [InlineData("can you complete this survey", TurnRoute.Form)]
    [InlineData("autofill it", TurnRoute.Form)]
    [InlineData("fill these details", TurnRoute.Cloud)] // no browser in front
    [InlineData("where is the settings button", TurnRoute.Cloud)]
    [InlineData("what's my screen showing", TurnRoute.Cloud)]
    public void RoutesBeforeAnythingLeavesThePc(string request, TurnRoute expected)
        => Assert.Equal(expected, TurnRouter.Route(request, privateMode: false));

    [Theory]
    [InlineData("fill this in")]
    [InlineData("fill these details")]
    [InlineData("can you fill it for me")]
    [InlineData("fill out the questions")]
    public void WithABrowserInFront_FillThis_MeansItsForm(string request)
        => Assert.Equal(TurnRoute.Form, TurnRouter.Route(request, privateMode: false, browserInFront: true));

    [Fact]
    public void WithABrowserInFront_OtherQuestionsStillGoToTheirBrain()
        => Assert.Equal(TurnRoute.Cloud, TurnRouter.Route("what's this error on the page", privateMode: false, browserInFront: true));

    [Theory]
    [InlineData("take control of my screen and fill the form", true)]
    [InlineData("fill this form for me", true)]
    [InlineData("fill this form", false)]
    [InlineData("help me answer this form", false)]
    public void FillIn_OnlyWhenAskedToTakeControl(string request, bool expected)
        => Assert.Equal(expected, TurnRouter.WantsFillIn(request));

    [Theory]
    [InlineData("Feel this farm", true)]
    [InlineData("Phil the form", true)]
    [InlineData("what's the weather", false)]
    public void MightMeanForm_ForgivesMishearing(string request, bool expected) => Assert.Equal(expected, TurnRouter.MightMeanForm(request));

    [Fact]
    public void PrivateMode_KeepsGeneralQuestionsLocal_ButVaultAndFormsKeepTheirOwnBrain()
    {
        Assert.Equal(TurnRoute.Local, TurnRouter.Route("where is the settings button", privateMode: true));
        Assert.Equal(TurnRoute.Vault, TurnRouter.Route("what's in my notes", privateMode: true));
        Assert.Equal(TurnRoute.Form, TurnRouter.Route("fill this form", privateMode: true));
    }
}

public sealed class StandardAnswersTests
{
    [Fact]
    public void NewFile_GetsATitleAndOneHeadingPerAnswer()
        => Assert.Equal(
            "# Standard answers\n\n## Why this role?\nI like the team.\n",
            StandardAnswers.Merge(null, [new FormEdit("Why this role?", "I like the team.")]));

    [Fact]
    public void SameQuestion_ReplacesItsAnswer_AndLeavesTheRestAlone()
    {
        var existing = "# Standard answers\r\n\r\n## Notice period\r\n2 months\r\n\r\n## Greatest strength?\r\nCalm under pressure.\r\n";

        var merged = StandardAnswers.Merge(existing, [new FormEdit("notice period", "1 month"), new FormEdit("Start date", "January")]);

        Assert.Equal("# Standard answers\n\n## Notice period\n1 month\n\n## Greatest strength?\nCalm under pressure.\n\n## Start date\nJanuary\n", merged);
    }

    [Fact]
    public void LastQuestion_IsReplacedToTheEnd()
        => Assert.Equal(
            "# A\n\n## Q\nnew\nlines\n",
            StandardAnswers.Merge("# A\n\n## Q\nold\nmore old\n", [new FormEdit("Q", "new\r\nlines")]));
}

public sealed class FormFillerTests
{
    private static FormFiller Filler(FakeVault vault, IBrowserLink browser, INotchPresenter notch, ITextInsertion? typing = null, Func<string, bool>? isWord = null)
        => new(new FormServices(vault, browser, notch, Vocabulary: () => ["Asha"], IsWord: isWord, Typing: typing), TimeProvider.System, NullLogger<FormFiller>.Instance);

    [Fact]
    public async Task AFieldThePageDidntTake_IsNotCounted_AndSaysWhy()
    {
        var vault = new FakeVault { Answers = [new FormAnswer("q0", "Asha Rao", "Profile/about.md"), new FormAnswer("q1", "$75,000 - $100,000", "Profile/about.md")] };
        var browser = new FormBrowser { Refuse = { ["q1"] = "no option matches" } };
        var notch = new RecordingNotch();

        await Filler(vault, browser, notch).RunAsync(fillIn: true, TestContext.Current.CancellationToken);

        var last = notch.Shown.OfType<NotchState.Form>().Last();
        Assert.Equal("Filled 1 of 2 · 1 to copy by hand · check the page, then submit it yourself.", last.Status);
        Assert.False(last.Cards[1].Filled);
        Assert.Contains("no option matches", last.Cards[1].Note, StringComparison.Ordinal);
        Assert.DoesNotContain(notch.Shown.OfType<NotchState.Form>(), form => form.Status.Contains("2 in so far", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATextFieldThatIgnoresScriptedValues_GetsRealKeystrokes_ThenIsReadBack()
    {
        var vault = new FakeVault { Answers = [new FormAnswer("q0", "Asha Rao", "Profile/about.md")] };
        var browser = new FormBrowser { NeedsTyping = { "q0" } };
        var typing = new FakeTyping(() => browser.Typed.Add("q0"));
        var notch = new RecordingNotch();

        await Filler(vault, browser, notch, typing).RunAsync(fillIn: true, TestContext.Current.CancellationToken);

        Assert.Equal(["Asha Rao"], typing.Typed);
        Assert.True(notch.Shown.OfType<NotchState.Form>().Last().Cards[0].Filled);
    }

    [Fact]
    public async Task Reload_AsksAgainWithTheLatestFiles_AndKeepsAnAnswerItCantImproveOn()
    {
        var vault = new FakeVault { Answers = [new FormAnswer("q0", "Asha Rao", "Profile/about.md"), new FormAnswer("q1", null, null)] };
        var notch = new RecordingNotch();
        await Filler(vault, new FormBrowser(), notch).RunAsync(fillIn: false, TestContext.Current.CancellationToken);
        vault.Asked.Clear();
        vault.Files["Profile/resume/new-cv.pdf"] = "x"; // added outside the panel
        vault.Answers = [new FormAnswer("q0", null, null), new FormAnswer("q1", "$75,000 - $100,000", "Profile/resume/new-cv.pdf")];

        notch.Shown.OfType<NotchState.Form>().Last().Actions.Reload!(null);
        await WaitUntil(() => notch.Shown.OfType<NotchState.Form>().Last().Cards[1].Answer is not null);

        var cards = notch.Shown.OfType<NotchState.Form>().Last().Cards;
        Assert.Equal(["Name", "Salary expectations"], vault.Asked);
        Assert.Contains("Profile/resume/new-cv.pdf", vault.Attached);
        Assert.Equal(("Asha Rao", FormCardState.Answered), (cards[0].Answer, cards[0].State)); // nothing better: kept
        Assert.Equal("$75,000 - $100,000", cards[1].Answer);
    }

    [Fact]
    public async Task NewWords_SkipsEnglishAndWhatTheDictionaryKnows()
    {
        var notch = new RecordingNotch();
        await Filler(new FakeVault(), new FormBrowser(), notch, isWord: word => word is "Software" or "engineer" or "at").RunAsync(fillIn: false, TestContext.Current.CancellationToken);

        var newWords = notch.Shown.OfType<NotchState.Form>().Last().Actions.NewWords!;

        Assert.Equal(["Rao", "Zentrix"], newWords("Asha Rao, Software engineer at Zentrix 2024"));
    }

    [Fact]
    public async Task ShowsACardPerQuestion_FillsThemAsAnswersArrive_AndMarksUnknowns()
    {
        var vault = new FakeVault { Answers = [new FormAnswer("q0", "Asha Rao", "Profile/about.md"), new FormAnswer("q1", null, null)] };
        var notch = new RecordingNotch();

        await Filler(vault, new FormBrowser(), notch).RunAsync(fillIn: false, TestContext.Current.CancellationToken);

        var forms = notch.Shown.OfType<NotchState.Form>().ToList();
        Assert.All(forms[0].Cards, card => Assert.Equal(FormCardState.Pending, card.State));
        var last = forms[^1];
        Assert.Equal("Job application", last.Title);
        Assert.Equal(("Asha Rao", "Profile/about.md", FormCardState.Answered), (last.Cards[0].Answer, last.Cards[0].Source, last.Cards[0].State));
        Assert.Equal(FormCardState.Unknown, last.Cards[1].State);
        Assert.Equal(["Under $50,000", "$75,000 - $100,000"], last.Cards[1].Choices); // the form's options, to pick from
        Assert.Equal("1 of 2 ready · 1 need you", last.Status);
        Assert.Equal(1, last.Fillable);
        Assert.Equal(["Name", "Salary expectations"], vault.Asked);
    }

    [Fact]
    public async Task TheSameFormAgain_IsAnsweredFromMemory_WithoutAskingTheVaultsBrain()
    {
        var vault = new FakeVault { Answers = [new FormAnswer("q0", "Asha Rao", "Profile/about.md"), new FormAnswer("q1", null, null)] };
        await Filler(vault, new FormBrowser(), new RecordingNotch()).RunAsync(fillIn: false, TestContext.Current.CancellationToken);
        Assert.Contains("Asha Rao", vault.Files[FormMemory.FilePath], StringComparison.Ordinal);
        vault.Asked.Clear();

        var notch = new RecordingNotch();
        await Filler(vault, new FormBrowser(), notch).RunAsync(fillIn: false, TestContext.Current.CancellationToken);

        Assert.Equal(["Salary expectations"], vault.Asked); // only what it still doesn't know
        var first = notch.Shown.OfType<NotchState.Form>().First();
        Assert.Equal(("Asha Rao", FormFiller.FromMemory, FormCardState.Answered), (first.Cards[0].Answer, first.Cards[0].Source, first.Cards[0].State));
    }

    [Fact]
    public async Task FillIn_PutsEachAnswerIntoThePage_LearnsIt_AndNeverTouchesUnknowns()
    {
        var vault = new FakeVault { Answers = [new FormAnswer("q0", "Asha Rao", "Profile/about.md"), new FormAnswer("q1", null, null)] };
        var browser = new FormBrowser();
        var notch = new RecordingNotch();

        await Filler(vault, browser, notch).RunAsync(fillIn: true, TestContext.Current.CancellationToken);

        Assert.Equal([("q0", "Asha Rao")], browser.Filled);
        var last = notch.Shown.OfType<NotchState.Form>().Last();
        Assert.True(last.Cards[0].Filled);
        Assert.Equal("Filled 1 of 2 · check the page, then submit it yourself.", last.Status);
        Assert.Equal(0, last.Fillable);
        Assert.Contains(notch.Shown.OfType<NotchState.Form>(), form => form.FillProgress is > 0 and <= 1);
        Assert.Contains("\"uses\": 1", vault.Files[FormMemory.FilePath], StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenQuestion_TheVaultCantAnswer_IsDraftedOnThisPc()
    {
        var vault = new FakeVault { Answers = [new FormAnswer("q0", "Asha Rao", "Profile/about.md"), new FormAnswer("q1", null, null, Open: true)], Draft = ["I'd ", "love to."] };
        var notch = new RecordingNotch();

        await Filler(vault, new FormBrowser(), notch).RunAsync(fillIn: false, TestContext.Current.CancellationToken);

        var shown = notch.Shown.OfType<NotchState.Form>().ToList();
        Assert.Contains(shown, form => form.Cards[1].State == FormCardState.Working);
        var card = shown[^1].Cards[1];
        Assert.Equal(("I'd love to.", FormFiller.Drafted, FormCardState.Answered), (card.Answer, card.Source, card.State));
        Assert.Equal(string.Empty, vault.Research); // no research engine here, so nothing went anywhere
    }

    [Fact]
    public async Task Save_MergesIntoStandardAnswers_AndRemembersThem()
    {
        var vault = new FakeVault { Files = { [StandardAnswers.FilePath] = "# Standard answers\n\n## Salary expectations\nold\n" } };
        var notch = new RecordingNotch();
        await Filler(vault, new FormBrowser(), notch).RunAsync(fillIn: false, TestContext.Current.CancellationToken);

        var said = await notch.Shown.OfType<NotchState.Form>().Last().Actions.Save([new FormEdit("Salary expectations", "$75,000 - $100,000")]);

        Assert.Equal("Saved 1 answer to your vault.", said);
        Assert.Equal("# Standard answers\n\n## Salary expectations\n$75,000 - $100,000\n", vault.Files[StandardAnswers.FilePath]);
        Assert.Contains("$75,000 - $100,000", vault.Files[FormMemory.FilePath], StringComparison.Ordinal);
        Assert.Equal(1, vault.Reindexed);
    }

    [Fact]
    public async Task TheLatestResumeAndThisFormsFiles_AreReadInFull_AndTheAnswersAreKeptAsHistory()
    {
        var vault = new FakeVault
        {
            Answers = [new FormAnswer("q0", "Asha Rao", "Profile/about.md")],
            Files =
            {
                ["Profile/resume/cv.pdf"] = "x",
                ["Profile/forms/Job application/brief.md"] = "x",
                ["Profile/forms/Job application/answers.md"] = "old",
            },
        };

        await Filler(vault, new FormBrowser(), new RecordingNotch()).RunAsync(fillIn: false, TestContext.Current.CancellationToken);

        Assert.Equal(["Profile/resume/cv.pdf", "Profile/forms/Job application/brief.md"], vault.Attached);
        Assert.StartsWith("# Job application", vault.Files["Profile/forms/Job application/answers.md"], StringComparison.Ordinal);
        Assert.Contains("## Name\nAsha Rao", vault.Files["Profile/forms/Job application/answers.md"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddingContext_UploadsIntoThisFormsFolder_AndAnswersTheUnknownsAgain()
    {
        var vault = new FakeVault { Answers = [new FormAnswer("q0", "Asha Rao", "Profile/about.md"), new FormAnswer("q1", null, null)] };
        var notch = new RecordingNotch();
        await Filler(vault, new FormBrowser(), notch).RunAsync(fillIn: false, TestContext.Current.CancellationToken);
        vault.Asked.Clear();

        var said = await notch.Shown.OfType<NotchState.Form>().Last().Actions.Files!.AddContext([@"C:\\Users\\me\\offer.pdf"]);
        await WaitUntil(() => vault.Asked.Count > 0);

        Assert.StartsWith("Added offer.pdf.", said, StringComparison.Ordinal);
        Assert.True(vault.Files.ContainsKey("Profile/forms/Job application/offer.pdf"));
        Assert.Equal(["Salary expectations"], vault.Asked); // only the unknown one
        Assert.Contains("Profile/forms/Job application/offer.pdf", vault.Attached);
    }

    [Theory]
    [InlineData("Asha Rao", new[] { "Asha", "Rao" })]
    [InlineData("call me on 5550100001", new[] { "5550100001" })]
    [InlineData("hybrid", new[] { "hybrid" })]
    public void DictionaryWords_ArePickedFromTheAnswer(string answer, string[] words) => Assert.Equal(words, FormFiller.DictionaryWords(answer));

    [Fact]
    public void EachFormGetsAFolderNamedForIt() => Assert.Equal("Profile/forms/Acme Job Application", FormFiller.FolderFor("Acme: Job? Application"));

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task NoForm_SaysWhy_AndOffersTryAgainAndClose()
    {
        var notch = new RecordingNotch();
        var retried = 0;

        await Filler(new FakeVault(), new FormBrowser { Problem = "I can't see the window in front." }, notch).RunAsync(fillIn: false, TestContext.Current.CancellationToken, () => retried++);

        var message = notch.Shown.OfType<NotchState.Response>().Last();
        Assert.Equal("I can't see the window in front.", message.Text);
        Assert.Equal(["Try again", "Close"], message.Actions!.Select(action => action.Label));
        message.Actions![0].Invoke();
        Assert.Equal(1, retried);
    }

    [Fact]
    public async Task NotConnected_SaysHowToConnect_AndReadsNothing()
    {
        var notch = new RecordingNotch();
        var browser = new FormBrowser();
        await Filler(new FakeVault { IsConnected = false }, browser, notch).RunAsync(fillIn: false, TestContext.Current.CancellationToken);

        Assert.Contains("Settings → Vault", Assert.IsType<NotchState.Response>(Assert.Single(notch.Shown)).Text, StringComparison.Ordinal);
        Assert.Equal(0, browser.Reads);
    }

    private sealed class FormBrowser : IBrowserLink
    {
        public int Reads { get; private set; }

        public List<(string Id, string Value)> Filled { get; } = [];

        public Dictionary<string, string> Refuse { get; } = [];

        public string? Problem { get; init; }

        public HashSet<string> NeedsTyping { get; } = [];

        public HashSet<string> Typed { get; } = [];

        public Task<FormFill?> FillFormAsync(string questionId, string value, CancellationToken cancellationToken)
        {
            Filled.Add((questionId, value));
            return Task.FromResult<FormFill?>(
                Refuse.TryGetValue(questionId, out var reason) ? new FormFill(false, 10, 20, Reason: reason)
                : NeedsTyping.Contains(questionId) ? new FormFill(false, 10, 20, Focused: true, Reason: "the page didn't take a scripted value")
                : new FormFill(true, 10, 20));
        }

        public Task<bool> CheckFormAsync(string questionId, string value, CancellationToken cancellationToken) => Task.FromResult(Typed.Contains(questionId));

        public bool IsConnected => true;

        public bool IsBrowserInFront => true;

        public Task<BrowserPage?> ReadPageAsync(CancellationToken cancellationToken) => Task.FromResult<BrowserPage?>(null);

        public Task<BrowserForm?> ReadFormAsync(CancellationToken cancellationToken)
        {
            Reads++;
            if (Problem is not null)
            {
                return Task.FromResult<BrowserForm?>(new BrowserForm("https://x", "Private tab", [], Problem: Problem));
            }

            return Task.FromResult<BrowserForm?>(new BrowserForm("https://docs.google.com/forms/x", "Job application",
                [new FormQuestion("q0", "Name", "text", [], true), new FormQuestion("q1", "Salary expectations", "dropdown", ["Under $50,000", "$75,000 - $100,000"], false)]));
        }

        public Task<bool> OpenAsync(string url, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}

public sealed class FormMemoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static FormQuestion Text(string question) => new("q", question, "text", [], false);

    [Theory]
    [InlineData("Full name", "name")]
    [InlineData("What is your name?", "name")]
    [InlineData("Phone number *", "phone")]
    [InlineData("Mobile number", "phone")]
    [InlineData("E-mail address", "email")]
    [InlineData("What is your notice period?", "notice period")]
    public void DifferentWordings_ShareOneField(string question, string key) => Assert.Equal(key, FormMemory.KeyOf(question));

    [Fact]
    public void KeepsEveryDistinctValueOnce_AndRanksByUseAndRecency()
    {
        var memory = FormMemory.Parse(null);
        memory.Remember("Phone number", "5550100001", "Profile/about.md", "Form A", Now.AddDays(-200), used: true);
        memory.Remember("Mobile", "5550100001 ", null, "Form B", Now.AddDays(-199), used: true); // same value, other wording
        memory.Remember("Phone", "5550100002", "You", "Form C", Now, used: true);

        Assert.Equal(["5550100002", "5550100001"], memory.Choices(Text("Contact number"), Now)); // used once today beats twice long ago
        var reloaded = FormMemory.Parse(memory.ToJson());
        Assert.Equal("5550100002", reloaded.Best(Text("Phone number"), "Any form", Now)!.Value);
        Assert.False(reloaded.Dirty);
    }

    [Fact]
    public void ChoiceQuestions_OnlyGetValuesTheyOffer_InTheirOwnSpelling()
    {
        var memory = FormMemory.Parse(null);
        memory.Remember("Country", "india", null, "Form A", Now, used: true);
        memory.Remember("Country", "Atlantis", null, "Form A", Now, used: true);

        var question = new FormQuestion("q", "Country", "dropdown", ["India", "Thailand"], false);

        Assert.Equal(["India"], memory.Choices(question, Now));
    }

    [Fact]
    public void Paragraphs_AreReusedOnlyOnTheSameForm()
    {
        var memory = FormMemory.Parse(null);
        memory.Remember("Why do you want to work here?", "Because of Acme's rockets.", "You", "Acme application", Now, used: true);
        var question = new FormQuestion("q", "Why do you want to work here?", "paragraph", [], false);

        Assert.Null(memory.Best(question, "Globex application", Now));
        Assert.Equal("Because of Acme's rockets.", memory.Best(question, "Acme application", Now)!.Value);
        Assert.Single(memory.Choices(question, Now)); // still offered to pick elsewhere
    }

    [Fact]
    public void ABrokenFile_StartsEmpty() => Assert.Empty(FormMemory.Parse("{not json").Choices(Text("Name"), Now));
}

public sealed class JvApiVaultTests
{
    [Fact]
    public async Task Stream_BuffersLines_AndSignsInAgainOnceWhenTheTokenIsRefused()
    {
        var saved = """{"name":"Jarvis on PC","secret":"s3cret"}""";
        var handler = new ScriptedHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/auth/reconnect" => Json(HttpStatusCode.OK, """{"access_token":"fresh"}"""),
            "/ask/ai/form-answers" when request.Headers.Authorization?.Parameter != "fresh" => Json(HttpStatusCode.Unauthorized, "{}"),
            "/ask/ai/form-answers" => Json(HttpStatusCode.OK, "{\"working\":0}\n{\"id\":\"q0\",\"answer\":\"Asha\",\"source\":\"Profile/about.md\"}\nnot json\n{\"error\":\"x\"}\n{\"id\":\"q1\",\"answer\":null,\"source\":null}\n{\"done\":true}\n"),
            _ => Json(HttpStatusCode.NotFound, "{}"),
        });
        var vault = new JvApiVault(new HttpClient(handler), new Monitor(), new VaultCredentials(() => saved, _ => { }), NullLogger<JvApiVault>.Instance);

        var answers = new List<FormAnswer>();
        await foreach (var answer in vault.AnswerFormAsync("Form", [new FormQuestion("q0", "Name", "text", [], true)], [], TestContext.Current.CancellationToken))
        {
            answers.Add(answer);
        }

        Assert.Equal([new FormAnswer("q0", "Asha", "Profile/about.md"), new FormAnswer("q1", null, null)], answers);
        Assert.Equal(1, handler.Count("/auth/reconnect"));
    }

    [Fact]
    public async Task WriteFile_CreatesItWhenItDoesNotExistYet()
    {
        var handler = new ScriptedHandler(request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("POST", "/auth/reconnect") => Json(HttpStatusCode.OK, """{"access_token":"t"}"""),
            ("PUT", "/files/Profile/standard-answers.md") => Json(HttpStatusCode.NotFound, "{}"),
            ("POST", "/files/Profile/standard-answers.md") => Json(HttpStatusCode.Created, "{}"),
            _ => Json(HttpStatusCode.BadRequest, "{}"),
        });
        var vault = new JvApiVault(new HttpClient(handler), new Monitor(), new VaultCredentials(() => """{"name":"n","secret":"s"}""", _ => { }), NullLogger<JvApiVault>.Instance);

        await vault.WriteFileAsync("Profile/standard-answers.md", "# Standard answers\n", TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Count("/files/Profile/standard-answers.md", "POST"));
    }

    [Fact]
    public async Task DeletingAFolder_DeletesItsFilesFirst()
    {
        var handler = new ScriptedHandler(request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("POST", "/auth/reconnect") => Json(HttpStatusCode.OK, """{"access_token":"t"}"""),
            ("GET", "/files/Profile/forms/Acme") => Json(HttpStatusCode.OK, """{"path":"Profile/forms/Acme","entries":[{"name":"answers.md","path":"Profile/forms/Acme/answers.md","type":"file"}]}"""),
            ("GET", _) => Json(HttpStatusCode.OK, """{"path":"x","name":"answers.md","content":""}"""),
            ("DELETE", _) => Json(HttpStatusCode.OK, "{}"),
            _ => Json(HttpStatusCode.BadRequest, "{}"),
        });
        var vault = new JvApiVault(new HttpClient(handler), new Monitor(), new VaultCredentials(() => """{"name":"n","secret":"s"}""", _ => { }), NullLogger<JvApiVault>.Instance);

        await vault.DeleteAsync("Profile/forms/Acme", TestContext.Current.CancellationToken);

        Assert.Equal(["/files/Profile/forms/Acme/answers.md", "/files/Profile/forms/Acme"], handler.Deletes);
    }

    [Fact]
    public void NotRegistered_IsNotConnected()
        => Assert.False(new JvApiVault(new HttpClient(), new Monitor(), new VaultCredentials(() => null, _ => { }), NullLogger<JvApiVault>.Instance).IsConnected);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Monitor : IOptionsMonitor<VaultOptions>
    {
        public VaultOptions CurrentValue { get; } = new() { Address = "http://vault.test" };

        public VaultOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<VaultOptions, string?> listener) => null;
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        private readonly List<(string Method, string Path)> _seen = [];

        public int Count(string path, string? method = null) => _seen.Count(seen => seen.Path == path && (method is null || seen.Method == method));

        public List<string> Deletes => [.. _seen.Where(seen => seen.Method == "DELETE").Select(seen => seen.Path)];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _seen.Add((request.Method.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(reply(request));
        }
    }
}

internal sealed class FakeTyping(Action typed) : ITextInsertion
{
    public List<string> Typed { get; } = [];

    public Task<IFocusedField?> CaptureFocusAsync(CancellationToken cancellationToken) => Task.FromResult<IFocusedField?>(new Field());

    public Task<InsertOutcome> InsertAsync(IFocusedField? field, string text, CancellationToken cancellationToken)
    {
        Typed.Add(text);
        typed();
        return Task.FromResult(InsertOutcome.Inserted);
    }

    public Task<string?> ReadAsync(IFocusedField field, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    private sealed class Field : IFocusedField
    {
        public string AppName => "Brave";

        public string? ExistingText => null;
    }
}

internal sealed class FakeVault : IVault
{
    public bool IsConnected { get; set; } = true;

    public IReadOnlyList<FormAnswer> Answers { get; set; } = [];

    public IReadOnlyList<string> Draft { get; init; } = [];

    public string? Research { get; private set; }

    public int Reindexed { get; private set; }

    public async IAsyncEnumerable<string> ComposeAnswerAsync(string question, string title, string research, IReadOnlyList<string> attachments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Research = research;
        foreach (var token in Draft)
        {
            await Task.Yield();
            yield return token;
        }
    }

    public Task ReindexAsync(CancellationToken cancellationToken)
    {
        Reindexed++;
        return Task.CompletedTask;
    }

    public List<string> Asked { get; } = [];

    public Dictionary<string, string> Files { get; } = [];

    public Task ConnectAsync(string inviteCode, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Disconnect() => IsConnected = false;

    public List<string> Attached { get; } = [];

    public List<string> Deleted { get; } = [];

    public Task<IReadOnlyList<VaultEntry>> ListAsync(string folder, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<VaultEntry>>([.. Files.Keys
            .Where(path => path.StartsWith(folder + "/", StringComparison.Ordinal) && !path[(folder.Length + 1)..].Contains('/', StringComparison.Ordinal))
            .Select(path => new VaultEntry(path, path[(path.LastIndexOf('/') + 1)..], false, DateTimeOffset.UnixEpoch))]);

    public Task UploadAsync(string path, string localFile, CancellationToken cancellationToken)
    {
        Files[path] = "(uploaded)";
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        Deleted.Add(path);
        Files.Remove(path);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<FormAnswer> AnswerFormAsync(string title, IReadOnlyList<FormQuestion> questions, IReadOnlyList<string> attachments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Asked.AddRange(questions.Select(question => question.Text));
        Attached.AddRange(attachments);
        foreach (var answer in Answers.Where(answer => questions.Any(question => question.Id == answer.Id)))
        {
            await Task.Yield();
            yield return answer;
        }
    }

    public async IAsyncEnumerable<string> AskAsync(string question, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return "From your notes.";
    }

    public Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken) => Task.FromResult(Files.GetValueOrDefault(path));

    public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        Files[path] = content;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingNotch : INotchPresenter
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
