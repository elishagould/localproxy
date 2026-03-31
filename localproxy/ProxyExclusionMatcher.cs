using System;
using System.Linq;
using System.Net;
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
/// - Subnet masks: 192.168.0.0/255.255.255.0
/// - Port-specific: example.com:8080, [::1]:8080
/// </summary>
public class ProxyExclusionMatcher
{
    private readonly List<ExclusionRule> _rules;
    private readonly bool _useProxy;

    public ProxyExclusionMatcher(IEnumerable<string> noProxyList, bool useProxy)
    {
        _useProxy = useProxy;
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
        if (!_useProxy)
            return true;
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
        private readonly bool _isIpRange;
        private readonly IPAddress? _networkAddress;
        private readonly IPAddress? _subnetMask;
        private readonly int? _cidrBits;

        public ExclusionRule(string pattern)
        {
            // Extract port if specified
            // Handle IPv6 with port: [::1]:8080
            if (pattern.StartsWith('['))
            {
                var closeBracketIndex = pattern.IndexOf(']');
                if (closeBracketIndex > 0 && closeBracketIndex < pattern.Length - 1 && pattern[closeBracketIndex + 1] == ':')
                {
                    _pattern = pattern.Substring(1, closeBracketIndex - 1);
                    if (int.TryParse(pattern.Substring(closeBracketIndex + 2), out var ipv6Port))
                    {
                        _port = ipv6Port;
                    }
                }
                else
                {
                    _pattern = pattern.Trim('[', ']');
                }
            }
            // Check for CIDR or subnet mask notation
            else if (pattern.Contains('/'))
            {
                var parts = pattern.Split('/');
                if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var netAddr))
                {
                    _networkAddress = netAddr;
                    _isIpRange = true;

                    // Check if it's CIDR notation (e.g., /16) or subnet mask (e.g., /255.255.255.0)
                    if (int.TryParse(parts[1], out var cidr))
                    {
                        _cidrBits = cidr;
                        _subnetMask = CidrToSubnetMask(cidr, netAddr.AddressFamily);
                    }
                    else if (IPAddress.TryParse(parts[1], out var mask))
                    {
                        _subnetMask = mask;
                        _cidrBits = SubnetMaskToCidr(mask);
                    }
                    _pattern = parts[0];
                }
                else
                {
                    _pattern = pattern;
                }
            }
            // Handle hostname:port
            else
            {
                var colonIndex = pattern.LastIndexOf(':');
                if (colonIndex > 0 && int.TryParse(pattern.Substring(colonIndex + 1), out var port))
                {
                    _pattern = pattern.Substring(0, colonIndex);
                    _port = port;
                }
                else
                {
                    _pattern = pattern;
                    _port = null;
                }
            }

            // Skip domain suffix and wildcard checks if it's an IP range
            if (!_isIpRange)
            {
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
        }

        public bool Matches(string host, int? port)
        {
            // Check port match if port is specified in the rule
            if (_port.HasValue && port.HasValue && _port.Value != port.Value)
                return false;

            // IP range match (CIDR or subnet mask)
            if (_isIpRange && _networkAddress != null && _subnetMask != null)
            {
                if (IPAddress.TryParse(host, out var hostIp))
                {
                    return IsInSubnet(hostIp, _networkAddress, _subnetMask);
                }
                return false;
            }

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

        private static bool IsInSubnet(IPAddress address, IPAddress network, IPAddress subnetMask)
        {
            // Ensure both addresses are the same family
            if (address.AddressFamily != network.AddressFamily)
                return false;

            var addressBytes = address.GetAddressBytes();
            var networkBytes = network.GetAddressBytes();
            var maskBytes = subnetMask.GetAddressBytes();

            for (int i = 0; i < addressBytes.Length; i++)
            {
                if ((addressBytes[i] & maskBytes[i]) != (networkBytes[i] & maskBytes[i]))
                    return false;
            }

            return true;
        }

        private static IPAddress CidrToSubnetMask(int cidr, System.Net.Sockets.AddressFamily addressFamily)
        {
            if (addressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                // IPv4
                if (cidr < 0 || cidr > 32)
                    throw new ArgumentException("CIDR must be between 0 and 32 for IPv4");

                uint mask = cidr == 0 ? 0 : 0xFFFFFFFF << (32 - cidr);
                byte[] bytes = new byte[4];
                bytes[0] = (byte)((mask >> 24) & 0xFF);
                bytes[1] = (byte)((mask >> 16) & 0xFF);
                bytes[2] = (byte)((mask >> 8) & 0xFF);
                bytes[3] = (byte)(mask & 0xFF);
                return new IPAddress(bytes);
            }
            else if (addressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                // IPv6
                if (cidr < 0 || cidr > 128)
                    throw new ArgumentException("CIDR must be between 0 and 128 for IPv6");

                byte[] bytes = new byte[16];
                int fullBytes = cidr / 8;
                int remainingBits = cidr % 8;

                for (int i = 0; i < fullBytes; i++)
                {
                    bytes[i] = 0xFF;
                }

                if (fullBytes < 16 && remainingBits > 0)
                {
                    bytes[fullBytes] = (byte)(0xFF << (8 - remainingBits));
                }

                return new IPAddress(bytes);
            }

            throw new ArgumentException("Unsupported address family");
        }

        private static int? SubnetMaskToCidr(IPAddress subnetMask)
        {
            var bytes = subnetMask.GetAddressBytes();
            int cidr = 0;
            bool zeroBitFound = false;

            foreach (var b in bytes)
            {
                for (int i = 7; i >= 0; i--)
                {
                    bool bitSet = (b & (1 << i)) != 0;
                    if (bitSet)
                    {
                        if (zeroBitFound)
                            return null; // Invalid subnet mask (non-contiguous)
                        cidr++;
                    }
                    else
                    {
                        zeroBitFound = true;
                    }
                }
            }

            return cidr;
        }
    }
}
