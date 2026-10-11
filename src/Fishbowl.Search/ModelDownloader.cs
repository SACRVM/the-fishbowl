using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Search;

// First-run download of the embedding model (intfloat/multilingual-e5-small,
// MIT) and its SentencePiece tokenizer. Pinned to a revision and by SHA-256 —
// a mismatch aborts the download and deletes the partial file so the next
// start retries. Files live under `{dataRoot}/models/MultilingualE5Small/`;
// the folder of the model before it (`MiniLmL6V2/`) is left as it was.
public sealed class ModelDownloader
{
    /// <summary>What the vectors are made with — stored beside them, so a
    /// workspace embedded with another model is embedded again.</summary>
    public const string ModelName = "intfloat/multilingual-e5-small";

    // The repo at a fixed commit, so a change upstream can't move under us.
    private const string BaseUrl = "https://huggingface.co/intfloat/multilingual-e5-small/resolve/614241f622f53c4eeff9890bdc4f31cfecc418b3";

    // Upstream filenames on HuggingFace → local filenames we read back:
    // `onnx/model.onnx` is the FP32 export (~470 MB — quantised exports
    // lose accuracy on CPUs without VNNI), `sentencepiece.bpe.model` the
    // tokenizer. Both are git-LFS objects, so HuggingFace publishes their
    // SHA-256 and both are pinned.
    private static readonly ModelFile[] Files =
    {
        new(
            UpstreamPath: "onnx/model.onnx",
            LocalName: "model.onnx",
            Sha256: "ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665"),
        new(
            UpstreamPath: "sentencepiece.bpe.model",
            LocalName: "sentencepiece.bpe.model",
            Sha256: "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865"),
    };

    private readonly string _modelsDir;
    private readonly HttpClient _http;
    private readonly ILogger<ModelDownloader> _logger;

    public ModelDownloader(string dataRoot, HttpClient? http = null, ILogger<ModelDownloader>? logger = null)
    {
        _modelsDir = Path.Combine(dataRoot, "models", "MultilingualE5Small");
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _logger = logger ?? NullLogger<ModelDownloader>.Instance;
    }

    public string ModelPath => Path.Combine(_modelsDir, "model.onnx");
    public string TokenizerPath => Path.Combine(_modelsDir, "sentencepiece.bpe.model");

    // True once both files are on disk. A file only gets its final name after
    // it was downloaded completely and verified (written as .part, then
    // renamed); one damaged since is found by EnsureModelAsync, which the
    // EmbeddingInitializer runs on every start, and fetched again.
    // EmbeddingService uses this to decide whether to initialise the
    // pipeline or throw EmbeddingUnavailableException.
    public bool IsReady()
    {
        if (!Directory.Exists(_modelsDir)) return false;
        foreach (var f in Files)
        {
            var path = Path.Combine(_modelsDir, f.LocalName);
            if (!File.Exists(path)) return false;
        }
        return true;
    }

    public async Task EnsureModelAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_modelsDir);

        foreach (var file in Files)
        {
            var localPath = Path.Combine(_modelsDir, file.LocalName);

            if (File.Exists(localPath) && await IsVerifiedAsync(localPath, file.Sha256, ct))
            {
                _logger.LogDebug("Model file {Name} already present and verified", file.LocalName);
                continue;
            }

            // Partial downloads from a previous crash — toss them. We've
            // already checked the good path above.
            if (File.Exists(localPath)) File.Delete(localPath);

            var url = $"{BaseUrl}/{file.UpstreamPath}";
            _logger.LogInformation("Downloading {Name} from {Url}", file.LocalName, url);

            var partPath = localPath + ".part";
            await DownloadToFileAsync(url, partPath, file.LocalName, ct);

            if (!await IsVerifiedAsync(partPath, file.Sha256, ct))
            {
                File.Delete(partPath);
                throw new InvalidOperationException(
                    $"SHA-256 mismatch for {file.LocalName} — expected {file.Sha256}. " +
                    "Deleted partial file; next start will retry.");
            }
            // Only a complete, verified file ever has the name the loader reads.
            File.Move(partPath, localPath, overwrite: true);

            _logger.LogInformation("Verified {Name}", file.LocalName);
        }
    }

    // Null expected hash means "verification not pinned" (a plain git blob,
    // whose raw-content hash HuggingFace doesn't publish).
    private static async Task<bool> IsVerifiedAsync(string path, string? expected, CancellationToken ct)
    {
        if (expected is null) return true;
        return await MatchesHashAsync(path, expected, ct);
    }

    private async Task DownloadToFileAsync(string url, string localPath, string name, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        var logEvery = Math.Max(total / 10, 2L * 1024 * 1024);
        var nextLog = logEvery;
        long read = 0;

        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(
            localPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (read >= nextLog)
            {
                if (total > 0)
                    _logger.LogInformation("Downloading {Name}: {Done}/{Total} bytes", name, read, total);
                else
                    _logger.LogInformation("Downloading {Name}: {Done} bytes", name, read);
                nextLog += logEvery;
            }
        }
    }

    private static async Task<bool> MatchesHashAsync(string path, string expected, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        var hex = Convert.ToHexStringLower(hash);
        return hex.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private record ModelFile(string UpstreamPath, string LocalName, string? Sha256);
}
