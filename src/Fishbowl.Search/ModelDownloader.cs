using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fishbowl.Search;

// First-run download of the MiniLM-L6-v2 ONNX model and vocab file. Pinned
// by SHA-256 — a mismatch aborts the download and deletes the partial file
// so the next start retries. Files live under `{dataRoot}/models/MiniLmL6V2/`.
//
// The HuggingFace URLs below point at the `onnx` subfolder of the upstream
// repo; those artefacts haven't moved since mid-2023 and the SHA-256 hashes
// are pinned so a silent upload swap would be caught.
public sealed class ModelDownloader
{
    // sentence-transformers/all-MiniLM-L6-v2 @ main, pinned by content hash.
    private const string BaseUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main";

    // Upstream filenames on HuggingFace → local filenames we read back.
    // `model.onnx` is the FP32 export (~90MB); `vocab.txt` is the WordPiece
    // vocabulary BertTokenizer expects.
    //
    // Only `model.onnx` has a pinned SHA-256 — it's stored in git-LFS so
    // HuggingFace publishes the content hash. `vocab.txt` is a regular git
    // blob (231KB) and the API only exposes the git SHA-1 of the wrapped
    // object, not a hash of the raw contents. Leaving its hash null means
    // we skip verification; the risk is bounded — a tampered vocab file
    // breaks tokenisation immediately rather than silently corrupting
    // embeddings.
    private static readonly ModelFile[] Files =
    {
        new(
            UpstreamPath: "onnx/model.onnx",
            LocalName: "model.onnx",
            Sha256: "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"),
        new(
            UpstreamPath: "vocab.txt",
            LocalName: "vocab.txt",
            Sha256: null),
    };

    private readonly string _modelsDir;
    private readonly HttpClient _http;
    private readonly ILogger<ModelDownloader> _logger;

    public ModelDownloader(string dataRoot, HttpClient? http = null, ILogger<ModelDownloader>? logger = null)
    {
        _modelsDir = Path.Combine(dataRoot, "models", "MiniLmL6V2");
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _logger = logger ?? NullLogger<ModelDownloader>.Instance;
    }

    public string ModelPath => Path.Combine(_modelsDir, "model.onnx");
    public string VocabPath => Path.Combine(_modelsDir, "vocab.txt");

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

    // Null expected hash means "verification not pinned" — see the comment on
    // Files[] for the vocab.txt rationale.
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
