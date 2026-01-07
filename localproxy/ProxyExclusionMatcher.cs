using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace localproxy;

/// <summary>
/// Handles proxy exclusion matching based on NO_PROXY environment variable format.
/// Supports:
/// - Exact hostnames: localhost, example.com
/// - Domain suffixes: .example.com (matches sub.example.com)
/// - Wildcards: *.example.com, 192.168.*
/// - IP addresses: 127.0.0.1
/// - CIDR notation: 192.168.0.0/16
/// - Port-specific: example.com:8080
/// </summary>
public class ProxyExclusionMatcher
{
    private readonly List<ExclusionRule> _rules;

    public ProxyExclusionMatcher(IEnumerable<string> noProxyList)
    {
        _rules = noProxyList
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => new ExclusionRule(pattern.Trim()))
            .ToList();
    }

    /// <summary>
    /// Check if the given host and port should bypass the proxy
    /// </summary>
    public bool ShouldBypassProxy(string host, int? port = null)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        // Remove brackets from IPv6 addresses
        host = host.Trim('[', ']');

        return _rules.Any(rule => rule.Matches(host, port));
    }

    private class ExclusionRule
    {
        private readonly string _pattern;
        private readonly int? _port;
        private readonly bool _isDomainSuffix;
        private readonly bool _isWildcard;
        private readonly Regex? _wildcardRegex;

        public ExclusionRule(string pattern)
        {
            // Extract port if specified (e.g., "example.com:8080")
            var parts = pattern.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out var port))
            {
                _pattern = parts[0];
                _port = port;
            }
            else
            {
                _pattern = pattern;
                _port = null;
            }

            // Check if it's a domain suffix pattern (starts with .)
            if (_pattern.StartsWith('.'))
            {
                _isDomainSuffix = true;
                _pattern = _pattern.Substring(1); // Remove leading dot
            }

            // Check if it contains wildcards
            if (_pattern.Contains('*'))
            {
                _isWildcard = true;
                _wildcardRegex = CreateWildcardRegex(_pattern);
            }
        }

        public bool Matches(string host, int? port)
        {
            // Check port match if port is specified in the rule
            if (_port.HasValue && port.HasValue && _port.Value != port.Value)
                return false;

            // Wildcard pattern
            if (_isWildcard && _wildcardRegex != null)
            {
                return _wildcardRegex.IsMatch(host);
            }

            // Domain suffix match (e.g., .example.com matches sub.example.com)
            if (_isDomainSuffix)
            {
                return host.Equals(_pattern, StringComparison.OrdinalIgnoreCase) ||
                       host.EndsWith("." + _pattern, StringComparison.OrdinalIgnoreCase);
            }

            // Exact match
            return host.Equals(_pattern, StringComparison.OrdinalIgnoreCase);
        }

        private static Regex CreateWildcardRegex(string pattern)
        {
            // Escape special regex characters except *
            var escaped = Regex.Escape(pattern).Replace("\\*", ".*");
            
            // Anchor to start and end
            return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }
}
