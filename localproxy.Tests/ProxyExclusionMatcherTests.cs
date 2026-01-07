using System;
using Xunit;

namespace localproxy.Tests;

public class ProxyExclusionMatcherTests
{
    [Theory]
    [InlineData("localhost", "localhost", true)]
    [InlineData("example.com", "example.com", true)]
    [InlineData("example.com", "EXAMPLE.COM", true)] // Case insensitive
    [InlineData("example.com", "sub.example.com", false)]
    public void ExactMatch_ShouldWork(string pattern, string host, bool expected)
    {
        var matcher = new ProxyExclusionMatcher(new[] { pattern });
        Assert.Equal(expected, matcher.ShouldBypassProxy(host));
    }

    [Theory]
    [InlineData(".example.com", "example.com", true)]
    [InlineData(".example.com", "sub.example.com", true)]
    [InlineData(".example.com", "deep.sub.example.com", true)]
    [InlineData(".example.com", "notexample.com", false)]
    [InlineData(".example.com", "examplexcom", false)]
    public void DomainSuffix_ShouldWork(string pattern, string host, bool expected)
    {
        var matcher = new ProxyExclusionMatcher(new[] { pattern });
        Assert.Equal(expected, matcher.ShouldBypassProxy(host));
    }

    [Theory]
    [InlineData("*.example.com", "sub.example.com", true)]
    [InlineData("*.example.com", "deep.sub.example.com", true)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("192.168.*", "192.168.1.1", true)]
    [InlineData("192.168.*", "192.168.255.255", true)]
    [InlineData("192.168.*", "192.169.1.1", false)]
    [InlineData("192.168.1.*", "192.168.1.100", true)]
    [InlineData("192.168.1.*", "192.168.2.100", false)]
    [InlineData("10.*", "10.0.0.1", true)]
    [InlineData("10.*", "11.0.0.1", false)]
    public void Wildcard_ShouldWork(string pattern, string host, bool expected)
    {
        var matcher = new ProxyExclusionMatcher(new[] { pattern });
        Assert.Equal(expected, matcher.ShouldBypassProxy(host));
    }

    [Theory]
    [InlineData("example.com:8080", "example.com", 8080, true)]
    [InlineData("example.com:8080", "example.com", 80, false)]
    [InlineData("example.com:8080", "example.com", null, false)]
    [InlineData("example.com", "example.com", 8080, true)] // No port specified matches any port
    [InlineData("example.com", "example.com", null, true)]
    public void PortSpecific_ShouldWork(string pattern, string host, int? port, bool expected)
    {
        var matcher = new ProxyExclusionMatcher(new[] { pattern });
        Assert.Equal(expected, matcher.ShouldBypassProxy(host, port));
    }

    [Fact]
    public void MultiplePatterns_ShouldWork()
    {
        var matcher = new ProxyExclusionMatcher(new[] 
        { 
            "localhost", 
            "127.0.0.1",
            "*.local",
            "192.168.*",
            "10.*"
        });

        Assert.True(matcher.ShouldBypassProxy("localhost"));
        Assert.True(matcher.ShouldBypassProxy("127.0.0.1"));
        Assert.True(matcher.ShouldBypassProxy("test.local"));
        Assert.True(matcher.ShouldBypassProxy("192.168.1.1"));
        Assert.True(matcher.ShouldBypassProxy("10.0.0.1"));
        Assert.False(matcher.ShouldBypassProxy("example.com"));
    }

    [Fact]
    public void EmptyOrWhitespace_ShouldBeIgnored()
    {
        var matcher = new ProxyExclusionMatcher(new[] { "", " ", "  ", "localhost" });
        Assert.True(matcher.ShouldBypassProxy("localhost"));
        Assert.False(matcher.ShouldBypassProxy(""));
        Assert.False(matcher.ShouldBypassProxy(null!));
    }

    [Theory]
    [InlineData("[::1]", "::1", true)] // IPv6 with brackets
    [InlineData("::1", "::1", true)]
    public void IPv6_ShouldWork(string pattern, string host, bool expected)
    {
        var matcher = new ProxyExclusionMatcher(new[] { pattern });
        Assert.Equal(expected, matcher.ShouldBypassProxy(host));
    }
}
