using System.Net;
using Fishbowl.Search;
using Xunit;

namespace Fishbowl.Search.Tests;

// A model file that is there but not whole (a full disk, a copy that
// stopped) is found by the pinned hash on start and fetched again — present
// alone used to count as ready forever.
public class ModelVerificationTests : IDisposable
{
    private readonly string _tempRoot;

    public ModelVerificationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "fishbowl_model_verify_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
    }

    // Records which files are asked for and answers each with an error, so
    // nothing leaves the machine.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource<string> FirstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            FirstRequest.TrySetResult(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private ModelDownloader DamagedModel(RecordingHandler handler)
    {
        var dl = new ModelDownloader(_tempRoot, new HttpClient(handler));
        Directory.CreateDirectory(Path.GetDirectoryName(dl.ModelPath)!);
        File.WriteAllBytes(dl.ModelPath, new byte[] { 1, 2, 3 });   // truncated
        File.WriteAllText(dl.VocabPath, "[PAD]\n");
        return dl;
    }

    [Fact]
    public async Task EnsureModelAsync_DamagedModel_IsDiscardedAndFetchedAgain()
    {
        var handler = new RecordingHandler();
        var dl = DamagedModel(handler);
        Assert.True(dl.IsReady());

        await Assert.ThrowsAsync<HttpRequestException>(() => dl.EnsureModelAsync(TestContext.Current.CancellationToken));

        Assert.EndsWith("/onnx/model.onnx", await handler.FirstRequest.Task);
        Assert.False(File.Exists(dl.ModelPath));
        Assert.False(dl.IsReady());
    }

    [Fact]
    public async Task EmbeddingInitializer_VerifiesAPresentModel()
    {
        var handler = new RecordingHandler();
        var init = new EmbeddingInitializer(DamagedModel(handler));

        await init.StartAsync(TestContext.Current.CancellationToken);
        var path = await handler.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await init.StopAsync(TestContext.Current.CancellationToken);

        Assert.EndsWith("/onnx/model.onnx", path);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); } catch { }
    }
}
