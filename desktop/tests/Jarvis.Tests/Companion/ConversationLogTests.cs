using Jarvis.Core.Companion;

namespace Jarvis.Tests.Companion;

public sealed class ConversationLogTests
{
    [Fact]
    public void SaysWhenItChanges_AndStampsEachExchange()
    {
        var log = new ConversationLog();
        var changes = 0;
        log.Changed += () => changes++;

        log.Add("where is share", "Top right.");
        log.Add("  ", "ignored"); // nothing asked: not kept, no change
        log.Clear();

        Assert.Equal(2, changes);
        log.Add("again", "Sure.");
        Assert.NotNull(log.Recent[0].At);
    }
}
