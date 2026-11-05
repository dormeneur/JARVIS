using Jarvis.Core.Assistant;

namespace Jarvis.Tests.Assistant;

public sealed class OutboundScannerTests
{
    [Theory]
    [InlineData("my key is sk-abcdefghijklmnopqrstuvwx0123", "api-key")]
    [InlineData("use sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "api-key")]
    [InlineData("token ghp_abcdefghijklmnopqrstuvwxyz0123456789", "api-key")]
    [InlineData("AKIAIOSFODNN7EXAMPLE is the id", "api-key")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIE", "private-key")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N", "jwt")]
    [InlineData("the wifi password: hunter2hunter2", "password")]
    [InlineData("PIN = 4821 for the door", "password")]
    [InlineData("card 4111 1111 1111 1111 expires soon", "card")]
    [InlineData("card 4111-1111-1111-1111", "card")]
    public void Finds_the_kind_of_secret(string text, string kind) => Assert.Contains(kind, OutboundScanner.Scan(text));

    [Theory]
    [InlineData("commit 9fceb02d0ae598e95dc970b74767f19372d61af8 fixed it")]
    [InlineData("order 123e4567-e89b-12d3-a456-426614174000 shipped")]
    [InlineData("call me on 9876543210")]
    [InlineData("my password is weak, I should change it")]
    [InlineData("card 4111 1111 1111 1112")] // 16 digits that fail the Luhn check
    [InlineData("the year 2026 and 1234567 items")]
    [InlineData("")]
    public void Ignores_things_that_only_look_similar(string text) => Assert.Empty(OutboundScanner.Scan(text));

    [Fact]
    public void Scan_handles_null() => Assert.Empty(OutboundScanner.Scan(null));

    [Fact]
    public void Redact_keeps_the_surrounding_text()
    {
        var redacted = OutboundScanner.Redact("Pay with 4111 1111 1111 1111 today, password: hunter2hunter2 thanks");

        Assert.Equal("Pay with [redacted] today, password: [redacted] thanks", redacted);
    }

    [Fact]
    public void Redact_removes_a_whole_private_key_block()
    {
        var redacted = OutboundScanner.Redact("before\n-----BEGIN PRIVATE KEY-----\nAAAA\nBBBB\n-----END PRIVATE KEY-----\nafter");

        Assert.Equal("before\n[redacted]\nafter", redacted);
    }

    [Fact]
    public void Redact_leaves_clean_text_alone()
    {
        const string Clean = "Open the settings page and click Save. Order 123e4567-e89b-12d3-a456-426614174000.";

        Assert.Equal(Clean, OutboundScanner.Redact(Clean));
    }

    [Fact]
    public void Findings_are_kinds_never_the_text()
    {
        var found = OutboundScanner.Scan("password: hunter2hunter2");

        Assert.DoesNotContain(found, kind => kind.Contains("hunter", StringComparison.Ordinal));
    }
}
