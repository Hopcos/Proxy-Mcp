using Xunit;

namespace BridgeMcp.Proxy.Tests;

/// <summary>
/// Unit tests for the URL-bootstrap logic in Program.cs. When hosted by IIS
/// in-process, ASP.NET Core exposes the address collection as read-only (backed
/// by an array — bindings come from IIS), and calling Clear() throws
/// NotSupportedException ("Collection is read-only"). Program must skip the
/// configured URL in that case.
/// </summary>
public class HostUrlsTests
{
    [Fact]
    public void Applies_ConfiguredUrl_WhenCollectionIsEmpty()
    {
        var urls = new List<string>();
        Program.ApplyUrls(urls, args: Array.Empty<string>(), configuredUrl: "http://localhost:8787");
        Assert.Equal(new[] { "http://localhost:8787" }, urls);
    }

    [Fact]
    public void Keeps_CommandLineUrls_WhenExplicit()
    {
        var urls = new List<string> { "http://localhost:6000" };
        Program.ApplyUrls(urls, args: new[] { "--urls=http://localhost:6000" }, configuredUrl: "http://localhost:8787");
        Assert.Equal("http://localhost:6000", urls.Single());
    }

    [Fact]
    public void Skips_ConfiguredUrl_WhenCollectionIsReadOnly()
    {
        // IIS in-process exposes the addresses as a read-only array-backed
        // collection. Before the fix, Program called Clear() on it and threw
        // NotSupportedException; it must now leave the addresses untouched.
        var urls = new[] { "http://localhost:80" };
        Program.ApplyUrls(urls, args: Array.Empty<string>(), configuredUrl: "http://localhost:8787");
        Assert.Equal("http://localhost:80", Assert.Single(urls));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Leaves_EmptyConfiguredUrl_Alone(string? configuredUrl)
    {
        var urls = new List<string>();
        Program.ApplyUrls(urls, args: Array.Empty<string>(), configuredUrl: configuredUrl ?? string.Empty);
        Assert.Empty(urls);
    }
}
