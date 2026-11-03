using System.Text.Json;
using Jarvis.Contracts.Brain;
using Jarvis.Providers.ClaudeCli;

namespace Jarvis.Tests.Brain;

public sealed class ClaudeProtocolTests
{
    [Fact]
    public void UserMessage_PutsEachLabelBeforeItsImage_ThenTheRequest()
    {
        var turn = new BrainTurn("ignored", "where is it?", [new LabeledImage("Screen 1 of 1", new byte[] { 1, 2, 3 })]);

        using var json = JsonDocument.Parse(ClaudeProtocol.UserMessage(turn));
        var content = json.RootElement.GetProperty("message").GetProperty("content");

        Assert.Equal("user", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("Screen 1 of 1", content[0].GetProperty("text").GetString());
        Assert.Equal("image/jpeg", content[1].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), content[1].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("where is it?", content[2].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"The red"}}}""", "The red")]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":" OK button"}}}""", " OK button")]
    public void Parse_TextDeltas(string line, string expected)
        => Assert.Equal(new ClaudeOutput.Text(expected), ClaudeProtocol.Parse(line));

    [Fact]
    public void Parse_ResultAndErrors()
    {
        Assert.Equal(new ClaudeOutput.Result(false, "done"), ClaudeProtocol.Parse("""{"type":"result","subtype":"success","is_error":false,"result":"done"}"""));
        Assert.Equal(new ClaudeOutput.Result(true, "Rate limited"), ClaudeProtocol.Parse("""{"type":"result","is_error":true,"result":"Rate limited"}"""));
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"init","model":"claude-sonnet-5-5"}""")]
    [InlineData("""{"type":"assistant","message":{}}""")]
    [InlineData("not json")]
    public void Parse_IgnoresEverythingElse(string line)
        => Assert.Equal(ClaudeOutput.Other.Instance, ClaudeProtocol.Parse(line));
}
