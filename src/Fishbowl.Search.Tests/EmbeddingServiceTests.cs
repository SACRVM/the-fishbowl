using Fishbowl.Core.Search;
using Fishbowl.Search;
using Xunit;

namespace Fishbowl.Search.Tests;

// Two flavours:
//   * Always-on contract tests — no model needed.
//   * Model-dependent semantic tests — skip when the embedding model's files
//     aren't on disk at the shared cache path below. The cache survives
//     across runs so a developer (or CI job) runs `EnsureModelAsync` once
//     and subsequent runs exercise the semantic properties.
public class EmbeddingServiceTests : IDisposable
{
    private readonly string _tempRoot;
    // Stable cache path under the system temp dir so the downloaded model
    // persists across test runs. Tests pointing here never mutate the files;
    // they only read.
    private static readonly string SharedModelCache =
        Path.Combine(Path.GetTempPath(), "fishbowl-test-models");

    public EmbeddingServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "fishbowl_emb_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task EmbedAsync_ThrowsEmbeddingUnavailable_WhenModelNotReady()
    {
        var downloader = new ModelDownloader(_tempRoot);
        Assert.False(downloader.IsReady());

        using var service = new EmbeddingService(downloader);
        await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            async () => await service.EmbedAsync("hello", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Dimensions_Is384()
    {
        var downloader = new ModelDownloader(_tempRoot);
        using var service = new EmbeddingService(downloader);
        // The contract with sqlite-vec's `vec_notes` FLOAT[384] column —
        // hard-pinned so a future swap to a different model doesn't silently
        // mismatch the schema.
        Assert.Equal(384, service.Dimensions);
    }

    [Fact]
    public async Task EmbedAsync_ReturnsNormalisedVector_OfDimension384()
    {
        SkipIfModelMissing();
        using var service = BuildService();

        var vec = await service.EmbedAsync(
            "The quick brown fox jumps over the lazy dog",
            TestContext.Current.CancellationToken);

        Assert.Equal(384, vec.Length);

        // L2 norm ≈ 1 — the pipeline normalises so downstream can treat
        // cosine similarity as a plain dot product.
        var normSquared = 0.0;
        foreach (var x in vec) normSquared += x * x;
        Assert.InRange(Math.Sqrt(normSquared), 0.99, 1.01);
    }

    [Fact]
    public async Task EmbedAsync_IsDeterministic_ForSameInput()
    {
        SkipIfModelMissing();
        using var service = BuildService();

        var a = await service.EmbedAsync("hello world", TestContext.Current.CancellationToken);
        var b = await service.EmbedAsync("hello world", TestContext.Current.CancellationToken);

        Assert.Equal(a.Length, b.Length);
        for (var i = 0; i < a.Length; i++)
        {
            // FP rounding is irrelevant across a single process but we keep
            // the window wide enough for future CPU/ORT tweaks.
            Assert.InRange(Math.Abs(a[i] - b[i]), 0.0f, 1e-5f);
        }
    }

    [Fact]
    public async Task EmbedAsync_SemanticallySimilarInputs_ScoreCloserThanUnrelated()
    {
        SkipIfModelMissing();
        using var service = BuildService();
        var ct = TestContext.Current.CancellationToken;

        var cat = await service.EmbedAsync("cat", ct);
        var kitten = await service.EmbedAsync("kitten", ct);
        var motorcycle = await service.EmbedAsync("motorcycle", ct);

        // Cosine = dot product for L2-normalised vectors.
        var closeSim = Dot(cat, kitten);
        var farSim = Dot(cat, motorcycle);

        // e5's similarities sit close together (most pairs land above 0.7),
        // so the gap is small but steady. A tight inequality is enough to
        // catch a broken pipeline without being fragile to model updates.
        Assert.True(closeSim > farSim + 0.02,
            $"Expected cat↔kitten similarity > cat↔motorcycle + 0.02, got {closeSim} vs {farSim}");
    }

    // The reason for the model: a German question finds the German text it
    // is about by meaning — none of its words are in the text.
    [Fact]
    public async Task EmbedQueryAsync_FindsAGermanText_ByMeaning()
    {
        SkipIfModelMissing();
        using var service = BuildService();
        var ct = TestContext.Current.CancellationToken;

        var query = await service.EmbedQueryAsync("Wann kommt mein Paket?", ct);
        var parcel = await service.EmbedAsync("Ihre Sendung wird morgen zwischen 10 und 14 Uhr zugestellt.", ct);
        var bill = await service.EmbedAsync("Ihre Stromrechnung für März beträgt 84,20 Euro.", ct);

        Assert.True(Dot(query, parcel) > Dot(query, bill) + 0.02,
            $"Expected the delivery text first, got {Dot(query, parcel)} vs {Dot(query, bill)}");
    }

    // A batch gives each text the vector it gets alone (padding is masked).
    [Fact]
    public async Task EmbedManyAsync_MatchesOneAtATime()
    {
        SkipIfModelMissing();
        using var service = BuildService();
        var ct = TestContext.Current.CancellationToken;
        string[] texts = ["kurz", "Ein etwas längerer Satz über das Wetter in Köln und die Bahn."];

        var many = await service.EmbedManyAsync(texts, ct);
        for (var i = 0; i < texts.Length; i++)
        {
            var one = await service.EmbedAsync(texts[i], ct);
            Assert.True(Dot(one, many[i]) > 0.9999f, $"text {i}: {Dot(one, many[i])}");
        }
    }

    // The tokenizer matches HuggingFace's own (the `tokenizers` library over
    // the repo's tokenizer.json) — fairseq's id shift, an unknown piece (the
    // emoji) and NFKC included; the ids below come from it.
    [Theory]
    [InlineData("query: Rechnung für die Stromkosten im März",
        new long[] { 0, 41, 1294, 12, 122035, 643, 68, 77185, 33357, 566, 58409, 2 })]
    [InlineData("passage: Ihre Bestellung bei Amazon.de ist unterwegs — Lieferung Dienstag 14.10.",
        new long[] { 0, 46692, 12, 8309, 102193, 1079, 15075, 5, 112, 443, 101893, 292, 143692, 120913, 616, 10179, 2 })]
    [InlineData("passage: Grüße aus Köln!  Ein   Test\twith ÄÖÜß, emoji 🐟 and https://example.com/a?b=1",
        new long[] { 0, 46692, 12, 184191, 1312, 77399, 38, 2991, 8647, 678, 20056, 8655, 8047, 20036, 4, 28, 121505, 6, 3, 136, 3975, 696, 3355, 11, 33209, 5, 277, 64, 11, 32, 275, 33000, 2 })]
    [InlineData("query: ①②③ ﬁne Ｆｕｌｌｗｉｄｔｈ",
        new long[] { 0, 41, 1294, 12, 37638, 5885, 9312, 146984, 2 })]
    public void Tokenizer_MatchesTheReference(string text, long[] expected)
    {
        SkipIfModelMissing();
        var dl = new ModelDownloader(SharedModelCache);
        using var pipeline = new E5Pipeline(dl.ModelPath, dl.TokenizerPath);
        Assert.Equal(expected, pipeline.Ids(text));
    }

    private EmbeddingService BuildService()
        => new(new ModelDownloader(SharedModelCache));

    private static void SkipIfModelMissing()
    {
        var downloader = new ModelDownloader(SharedModelCache);
        if (!downloader.IsReady())
        {
            Assert.Skip(
                $"Embedding model not present at {Path.GetDirectoryName(downloader.ModelPath)}. " +
                "Run `dotnet run --project src/Fishbowl.Host` once to populate, " +
                "or copy an existing download into the shared cache.");
        }
    }

    private static float Dot(float[] a, float[] b)
    {
        var s = 0.0;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return (float)s;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }
    }
}
