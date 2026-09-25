using Xunit;
using Fishbowl.Data;
using Fishbowl.Core;
using System.IO;
using System.Threading.Tasks;
using System.Reflection;
using Microsoft.Extensions.Caching.Memory;
using System.Text;

namespace Fishbowl.Tests;

public class ResourceProviderTests : IDisposable
{
    private readonly string _tempModsDir;
    private readonly IMemoryCache _cache;

    public ResourceProviderTests()
    {
        _tempModsDir = Path.Combine(Path.GetTempPath(), "fishbowl_tests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempModsDir);
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    [Fact]
    public async Task GetAsync_DiskOverridesEmbedded_Test()
    {
        // Arrange
        var testPath = "test.txt";
        var diskContent = "Disk Content";
        File.WriteAllText(Path.Combine(_tempModsDir, testPath), diskContent);

        var provider = new ResourceProvider(
            cache: _cache,
            modsPath: _tempModsDir,
            embeddedAssembly: typeof(ResourceProvider).Assembly
        );

        // Act
        var resource = await provider.GetAsync(testPath, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(resource);
        Assert.Equal(ResourceSource.Disk, resource.Source);
        var content = Encoding.UTF8.GetString(resource.Data);
        Assert.Equal(diskContent, content);
    }

    [Fact]
    public async Task GetAsync_DiskResource_IsNotCached_Test()
    {
        // "Disk file wins" includes edits: the overlay tier must serve the
        // current file content on every request, not the first-served copy.
        // Only embedded resources (immutable per process) go into the cache.
        var testPath = "cache_test.txt";
        var initialContent = "Initial Content";
        var filePath = Path.Combine(_tempModsDir, testPath);
        File.WriteAllText(filePath, initialContent);

        var provider = new ResourceProvider(_cache, _tempModsDir);

        // Act 1: First read (hits disk)
        var firstResource = await provider.GetAsync(testPath, TestContext.Current.CancellationToken);
        Assert.Equal(initialContent, Encoding.UTF8.GetString(firstResource!.Data));

        // Act 2: Modify disk
        File.WriteAllText(filePath, "Modified Content");

        // Act 3: Read again (must see the modified file, not a cached copy)
        var secondResource = await provider.GetAsync(testPath, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Modified Content", Encoding.UTF8.GetString(secondResource!.Data));
        Assert.Equal(ResourceSource.Disk, secondResource.Source);
    }

    [Fact]
    public async Task ExistsAsync_DiskResourceDeleted_ReflectsDeletion_Test()
    {
        // Companion to the no-disk-caching rule above: a deleted overlay file
        // stops existing immediately instead of lingering in the cache.
        var testPath = "exists_cache_test.txt";
        var filePath = Path.Combine(_tempModsDir, testPath);
        File.WriteAllText(filePath, "exists");

        var provider = new ResourceProvider(_cache, _tempModsDir);
        await provider.GetAsync(testPath, TestContext.Current.CancellationToken);

        // Act
        File.Delete(filePath);
        var exists = await provider.ExistsAsync(testPath, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(exists, "A deleted overlay file must not survive via the cache.");
    }

    [Fact]
    public async Task GetAsync_ReturnsEmbeddedWhenDiskMissing_Test()
    {
        // Arrange
        var provider = new ResourceProvider(
            cache: _cache,
            modsPath: _tempModsDir,
            embeddedAssembly: typeof(ResourceProvider).Assembly
        );

        // Act
        var resource = await provider.GetAsync("test.txt", TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(resource);
        Assert.Equal(ResourceSource.Embedded, resource.Source);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullWhenNotFound_Test()
    {
        // Arrange
        var provider = new ResourceProvider(_cache, _tempModsDir);

        // Act
        var resource = await provider.GetAsync("non-existent-file.xyz", TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(resource);
    }

    [Fact]
    public async Task GetAsync_ResolvesSubFolderResourceWithForwardSlash_Test()
    {
        // Arrange
        var provider = new ResourceProvider(
            cache: _cache,
            modsPath: _tempModsDir,
            embeddedAssembly: typeof(ResourceProvider).Assembly
        );

        // Act
        // 'css/app.css' is embedded in Fishbowl.Data
        var resource = await provider.GetAsync("css/app.css", TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(resource);
        Assert.Equal(ResourceSource.Embedded, resource.Source);
    }

    [Fact]
    public async Task GetAsync_ResolvesSubFolderResourceWithBackslash_Test()
    {
        // Arrange
        var provider = new ResourceProvider(
            cache: _cache,
            modsPath: _tempModsDir,
            embeddedAssembly: typeof(ResourceProvider).Assembly
        );

        // Act
        // Even if requested with backslash, it should resolve (important for Windows paths)
        var resource = await provider.GetAsync(@"css\app.css", TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(resource);
        Assert.Equal(ResourceSource.Embedded, resource.Source);
    }

    [Fact]
    public async Task ExistsAsync_FindsEmbeddedResourceByForwardSlashSubPath_Test()
    {
        var provider = new ResourceProvider(
            cache: _cache,
            modsPath: _tempModsDir,
            embeddedAssembly: typeof(ResourceProvider).Assembly);

        var exists = await provider.ExistsAsync("css/app.css", TestContext.Current.CancellationToken);

        Assert.True(exists, "ExistsAsync must find embedded subfolder resources (forward slash).");
    }

    [Fact]
    public async Task ExistsAsync_FindsEmbeddedResourceByBackslashSubPath_Test()
    {
        var provider = new ResourceProvider(
            cache: _cache,
            modsPath: _tempModsDir,
            embeddedAssembly: typeof(ResourceProvider).Assembly);

        var exists = await provider.ExistsAsync(@"css\app.css", TestContext.Current.CancellationToken);

        Assert.True(exists, "ExistsAsync must find embedded subfolder resources (backslash).");
    }

    public void Dispose()
    {
        _cache.Dispose();
        if (Directory.Exists(_tempModsDir))
        {
            try { Directory.Delete(_tempModsDir, true); } catch { }
        }
    }
}
