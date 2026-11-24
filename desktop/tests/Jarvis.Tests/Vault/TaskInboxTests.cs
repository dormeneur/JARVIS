using Jarvis.Core.Vault;

namespace Jarvis.Tests.Vault;

public sealed class TaskInboxTests
{
    [Theory]
    [InlineData("add a task: buy milk", "buy milk")]
    [InlineData("Add a task to call mum.", "call mum")]
    [InlineData("add task renew my passport", "renew my passport")]
    [InlineData("create a to-do: finish the report", "finish the report")]
    [InlineData("add buy milk to my to-do list", "buy milk")]
    [InlineData("put call the bank on my tasks", "call the bank")]
    [InlineData("jarvis, add a task   pay   rent!", "pay rent")]
    public void Parse_ReadsTheTask(string request, string expected) => Assert.Equal(expected, TaskInbox.Parse(request));

    [Theory]
    [InlineData("what tasks do I have")]
    [InlineData("add a task")]
    [InlineData("add a task:")]
    [InlineData("how do I add a task in Jira")]
    [InlineData("what's on my calendar today")]
    public void Parse_IgnoresEverythingElse(string request) => Assert.Null(TaskInbox.Parse(request));

    [Fact]
    public void Parse_CapsLength() => Assert.Equal(300, TaskInbox.Parse("add a task: " + new string('x', 500))!.Length);

    [Fact]
    public void Append_StartsAFileWithAHeading()
        => Assert.Equal("# Tasks\n\n- [ ] buy milk  (added 2026-11-23)\n", TaskInbox.Append(null, "buy milk", new DateOnly(2026, 11, 23)));

    [Fact]
    public void Append_KeepsEverythingAlreadyThere()
    {
        var result = TaskInbox.Append("# Tasks\n\n- [x] old", "new one", new DateOnly(2026, 11, 23));
        Assert.StartsWith("# Tasks\n\n- [x] old\n- [ ] new one", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("add a task: buy milk", TurnRoute.TaskAdd)]
    [InlineData("add buy milk to my todo list", TurnRoute.TaskAdd)]
    [InlineData("add lunch with Sam tomorrow at 1 to my calendar", TurnRoute.CalendarAdd)]
    [InlineData("add a task to my calendar", TurnRoute.CalendarAdd)]
    [InlineData("fill this form for me", TurnRoute.Form)]
    [InlineData("what's in my notes", TurnRoute.Vault)]
    [InlineData("add a title to this slide", TurnRoute.Cloud)]
    public void Route_SendsTasksToTheVaultAndLeavesTheRestAlone(string request, TurnRoute expected)
        => Assert.Equal(expected, TurnRouter.Route(request, privateMode: false));
}
