using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace Fishbowl.Search;

// Owns the ONNX InferenceSession and the SentencePiece tokenizer for
// intfloat/multilingual-e5-small — a model trained for search in about a
// hundred languages, German among them (the English all-MiniLM-L6-v2 before
// it found next to nothing by meaning in German text). Load once, reuse
// forever — ORT sessions are thread-safe for Run() after construction.
// Dispose at app shutdown.
//
// Forward pass for a batch:
//   1. Tokenize each text → ids, truncated to MaxSequenceLength, padded to
//      the longest in the batch, attention_mask 1 for real tokens.
//   2. Run the ONNX graph → token-level embeddings [n, seq, 384].
//   3. Mean-pool over tokens weighted by attention_mask → one vector a text.
//   4. L2-normalise → cosine similarity collapses to dot product.
//
// The "query: " / "passage: " prefixes the model was trained with are the
// caller's (EmbeddingService); this class embeds whatever text it gets.
// These steps match the sentence-transformers reference (1_Pooling: mean).
internal sealed class E5Pipeline : IDisposable
{
    // The model reads up to 512 tokens; a note's or a mail's start says what
    // it is about, and the cost of a run grows with its length.
    public const int MaxSequenceLength = 256;
    public const int EmbeddingDim = 384;

    // XLM-R's ids are fairseq's: <s> <pad> </s> <unk> come first, and every
    // SentencePiece id moves one up (its own <unk>, 0, becomes 3).
    private const long Bos = 0, Pad = 1, Eos = 2, Unk = 3;

    // A token is at least a character, mostly several: no need to hand the
    // tokenizer more text than can make it into a sequence.
    private const int MaxChars = MaxSequenceLength * 8;

    private readonly InferenceSession _session;
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly bool _tokenTypes;
    private bool _disposed;

    public E5Pipeline(string modelPath, string tokenizerPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("ONNX model not found", modelPath);
        if (!File.Exists(tokenizerPath))
            throw new FileNotFoundException("Tokenizer model not found", tokenizerPath);

        var options = new SessionOptions
        {
            // CPU only. GPU would need runtime selection + users shipping CUDA.
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            // Measured on a 20-thread laptop: 4 threads embed as fast as 14
            // (memory, not arithmetic, is the limit) and leave the machine
            // usable while a backfill runs for hours.
            IntraOpNumThreads = Math.Min(4, Environment.ProcessorCount),
        };
        _session = new InferenceSession(modelPath, options);
        // A BERT graph takes token_type_ids; an export without them doesn't.
        _tokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");

        // The model file carries its own normalisation (NFKC and friends) and
        // the unigram vocabulary; the sentence marks are added below, in
        // fairseq's numbering.
        using var stream = File.OpenRead(tokenizerPath);
        _tokenizer = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
    }

    /// <summary>The ids the model reads for a text: &lt;s&gt; … &lt;/s&gt;,
    /// at most <see cref="MaxSequenceLength"/>.</summary>
    internal long[] Ids(string text)
    {
        text ??= string.Empty;
        if (text.Length > MaxChars) text = text[..MaxChars];
        var pieces = _tokenizer.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false, MaxSequenceLength - 2, out _, out _);
        var ids = new long[pieces.Count + 2];
        ids[0] = Bos;
        for (var i = 0; i < pieces.Count; i++)
            ids[i + 1] = pieces[i] == 0 ? Unk : pieces[i] + 1;
        ids[^1] = Eos;
        return ids;
    }

    public float[] Embed(string text) => Embed([text])[0];

    /// <summary>Several texts in one run — what a backfill wants; texts of a
    /// like length waste the least on padding.</summary>
    public float[][] Embed(IReadOnlyList<string> texts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (texts.Count == 0) return [];

        var tokens = texts.Select(Ids).ToList();
        var n = tokens.Count;
        var seq = tokens.Max(t => t.Length);
        var inputIds = new long[n * seq];
        var attentionMask = new long[n * seq];
        var tokenTypeIds = new long[n * seq];
        for (var b = 0; b < n; b++)
        {
            var row = tokens[b];
            for (var t = 0; t < seq; t++)
            {
                var at = b * seq + t;
                inputIds[at] = t < row.Length ? row[t] : Pad;
                attentionMask[at] = t < row.Length ? 1 : 0;
            }
        }

        var shape = new[] { (long)n, seq };
        using var idsTensor = OrtValue.CreateTensorValueFromMemory(inputIds, shape);
        using var maskTensor = OrtValue.CreateTensorValueFromMemory(attentionMask, shape);
        using var typesTensor = OrtValue.CreateTensorValueFromMemory(tokenTypeIds, shape);
        var inputs = new Dictionary<string, OrtValue>
        {
            ["input_ids"] = idsTensor,
            ["attention_mask"] = maskTensor,
        };
        if (_tokenTypes) inputs["token_type_ids"] = typesTensor;

        using var runOptions = new RunOptions();
        using var outputs = _session.Run(runOptions, inputs, [_session.OutputNames[0]]);

        // last_hidden_state, [n, seq, 384].
        var hidden = outputs[0].GetTensorDataAsSpan<float>();
        var result = new float[n][];
        for (var b = 0; b < n; b++)
        {
            // Mean pool over the real tokens, then L2-normalise.
            var pooled = new float[EmbeddingDim];
            var count = 0;
            for (var t = 0; t < seq; t++)
            {
                if (attentionMask[b * seq + t] == 0) continue;
                count++;
                var start = (b * seq + t) * EmbeddingDim;
                for (var d = 0; d < EmbeddingDim; d++) pooled[d] += hidden[start + d];
            }
            var inv = 1f / Math.Max(count, 1);
            var norm = 0.0;
            for (var d = 0; d < EmbeddingDim; d++)
            {
                pooled[d] *= inv;
                norm += pooled[d] * pooled[d];
            }
            var scale = (float)(1.0 / Math.Sqrt(Math.Max(norm, 1e-18)));
            for (var d = 0; d < EmbeddingDim; d++) pooled[d] *= scale;
            result[b] = pooled;
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _session.Dispose();
        _disposed = true;
    }
}
