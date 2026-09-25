using System.Reflection;
using Fishbowl.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Data;

public class ResourceProvider : IResourceProvider
{
    private readonly string _modsPath;
    private readonly Assembly _embeddedAssembly;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ResourceProvider> _logger;

    public ResourceProvider(
        IMemoryCache cache,
        string modsPath = "fishbowl-mods",
        Assembly? embeddedAssembly = null,
        ILogger<ResourceProvider>? logger = null)
    {
        _cache = cache;
        _modsPath = modsPath;
        _embeddedAssembly = embeddedAssembly ?? Assembly.GetExecutingAssembly();
        _logger = logger ?? NullLogger<ResourceProvider>.Instance;
    }

    public async Task<Resource?> GetAsync(string path, CancellationToken ct = default, bool bypassCache = false)
    {
        if (!bypassCache && _cache.TryGetValue(path, out Resource? cached))
            return cached;

        Resource? resource = null;

        var diskPath = Path.Combine(_modsPath, path);
        if (File.Exists(diskPath))
        {
            var data = await File.ReadAllBytesAsync(diskPath, ct);
            resource = new Resource(data, path, ResourceSource.Disk);
        }

        if (resource == null)
        {
            using var stream = TryOpenEmbeddedStream(path);
            if (stream != null)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                resource = new Resource(ms.ToArray(), path, ResourceSource.Embedded);
            }
        }

        if (resource != null)
        {
            _logger.LogDebug("Resource {Path} served from {Source}", resource.Path, resource.Source);
            // Disk hits stay uncached: the disk tier is the "file wins" overlay,
            // and an eternal cache entry would pin the first-served content until
            // a restart — edits to an overlay file must show on the next request.
            if (!bypassCache && resource.Source == ResourceSource.Embedded)
                _cache.Set(path, resource);
        }

        return resource;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(path, out _))
            return Task.FromResult(true);

        var diskPath = Path.Combine(_modsPath, path);
        if (File.Exists(diskPath))
            return Task.FromResult(true);

        using var stream = TryOpenEmbeddedStream(path);
        return Task.FromResult(stream != null);
    }

    private Stream? TryOpenEmbeddedStream(string path)
    {
        var normalizedPath = path.Replace('\\', '/');

        var stream = _embeddedAssembly.GetManifestResourceStream(normalizedPath);
        if (stream != null) return stream;

        var windowsPath = normalizedPath.Replace('/', '\\');
        stream = _embeddedAssembly.GetManifestResourceStream(windowsPath);
        if (stream != null) return stream;

        var dotPath = normalizedPath.Replace('/', '.');
        var legacyName = $"{_embeddedAssembly.GetName().Name}.Resources.{dotPath}";
        return _embeddedAssembly.GetManifestResourceStream(legacyName);
    }
}
