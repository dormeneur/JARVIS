using System.Net;
using System.Text;
using Jarvis.Core.Agents;

namespace Jarvis.Tests.Agents;

public sealed class GitHubSignInTests
{
    [Fact]
    public async Task ShowsACode_ThenWaitsForApproval_AndSlowsDownWhenAsked()
    {
        var github = new Replies(
            """{"device_code":"dc","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":0}""",
            """{"error":"authorization_pending"}""",
            """{"error":"slow_down"}""",
            """{"access_token":"gho_token","token_type":"bearer"}""");
        var signIn = new GitHubSignIn(new HttpClient(github), new Instant());

        var code = await signIn.StartAsync(CancellationToken.None, clientId: "client");
        var token = await signIn.WaitAsync(code, CancellationToken.None, clientId: "client");

        Assert.Equal(("ABCD-1234", "gho_token"), (code.UserCode, token));
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", github.Bodies[1], StringComparison.Ordinal);
        Assert.DoesNotContain(github.Bodies, body => body.Contains("secret", StringComparison.Ordinal)); // no secret, ever
    }

    [Fact]
    public async Task ACancelledApproval_SaysSo()
    {
        var signIn = new GitHubSignIn(new HttpClient(new Replies("""{"error":"access_denied"}""")), new Instant());
        var code = new GitHubDeviceCode("dc", "X", "https://github.com/login/device", 900, 0);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => signIn.WaitAsync(code, CancellationToken.None, clientId: "client"));
        Assert.Contains("cancelled on GitHub", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Waits take no time, so polling tests run instantly.</summary>
    private sealed class Instant : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _now += dueTime;
            return System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class Replies(params string[] replies) : HttpMessageHandler
    {
        private int _next;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(replies[Math.Min(_next++, replies.Length - 1)], Encoding.UTF8, "application/json") };
        }
    }
}
