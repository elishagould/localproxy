using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace localproxy;

public class SspiCredentialCache
{
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
    private readonly Dictionary<string, string> _authSchemes = new Dictionary<string, string>();
    private readonly ILogger<SspiCredentialCache> _logger;
    
    public SspiCredentialCache(ILogger<SspiCredentialCache> logger)
    {
        _logger = logger;
    }
    
    public string GetAuthScheme(string proxyHost, int proxyPort)
    {
        var key = $"{proxyHost}:{proxyPort}";
        _semaphore.Wait();
        try
        {
            if (_authSchemes.TryGetValue(key, out var scheme))
            {
                return scheme;
            }
        }
        finally
        {
            _semaphore.Release();
        }
        return null;
    }

    public void CacheAuthScheme(string proxyHost, int proxyPort, string scheme)
    {
        var key = $"{proxyHost}:{proxyPort}";
        _semaphore.Wait();
        try
        {
            if (!_authSchemes.ContainsKey(key))
            {
                _authSchemes[key] = scheme;
                _logger.LogTrace("Cached auth scheme '{Scheme}' for {Key}", scheme, key);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
