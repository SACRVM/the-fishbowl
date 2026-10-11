using Fishbowl.Search;
using Xunit;

namespace Fishbowl.Search.Tests;

// No-network tests for ModelDownloader's behaviour around the models dir.
// The actual download flow is exercised manually (too slow / too dependent
// on network for CI); these cover the readiness and path logic so a typo in
// EmbeddingService won't silently read from the wrong place.
public class ModelDownloaderTests : IDisposable
{
    private readonly string _tempRoot;

    public ModelDownloaderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "fishbowl_model_dl_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void IsReady_ReturnsFalse_WhenFilesAbsent()
    {
        var dl = new ModelDownloader(_tempRoot);
        Assert.False(dl.IsReady());
    }

    [Fact]
    public void IsReady_ReturnsFalse_WhenOnlyOneFilePresent()
    {
        var dl = new ModelDownloader(_tempRoot);
        var modelsDir = Path.GetDirectoryName(dl.ModelPath)!;
        Directory.CreateDirectory(modelsDir);

        // Only the tokenizer present → incomplete setup, not ready.
        File.WriteAllBytes(dl.TokenizerPath, new byte[] { 0 });
        Assert.False(dl.IsReady());
    }

    [Fact]
    public void IsReady_ReturnsTrue_WhenBothFilesPresent()
    {
        var dl = new ModelDownloader(_tempRoot);
        var modelsDir = Path.GetDirectoryName(dl.ModelPath)!;
        Directory.CreateDirectory(modelsDir);

        // Stand-in bytes — we only care about presence for IsReady, not shape.
        File.WriteAllBytes(dl.ModelPath, new byte[] { 0 });
        File.WriteAllBytes(dl.TokenizerPath, new byte[] { 0 });
        Assert.True(dl.IsReady());
    }

    [Fact]
    public void ModelPath_AndTokenizerPath_AreInDataRootModelsSubfolder()
    {
        var dl = new ModelDownloader(_tempRoot);
        // Embedding callers and re-index endpoints rely on the fixed layout —
        // keep it surfaced as part of the contract.
        Assert.StartsWith(
            Path.Combine(_tempRoot, "models", "MultilingualE5Small"),
            dl.ModelPath);
        Assert.StartsWith(
            Path.Combine(_tempRoot, "models", "MultilingualE5Small"),
            dl.TokenizerPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
    }
}
