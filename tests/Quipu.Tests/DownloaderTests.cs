using System.Net;
using Quipu.Services;

namespace Quipu.Tests;

public sealed class DownloaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("quipu-dl-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Serves one file and honours Range requests like Hugging Face does.</summary>
    private sealed class FileServer(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var from = request.Headers.Range?.Ranges.First().From ?? 0;
            if (from >= content.Length) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            return Task.FromResult(new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content[(int)from..]),
            });
        }
    }

    private async Task<string> Download(byte[] content, byte[] existingPart)
    {
        var dest = Path.Combine(_dir, "model.gguf");
        File.WriteAllBytes(dest + ".part", existingPart);
        await Downloader.DownloadAsync(new HttpClient(new FileServer(content)), "http://hf.test/model.gguf", dest,
            new Job { Kind = "download", Title = "test" }, null, CancellationToken.None, content.Length, totalKnown: true);
        Assert.False(File.Exists(dest + ".part"));
        return dest;
    }

    [Fact]
    public async Task Interrupted_download_resumes()
    {
        var content = "0123456789"u8.ToArray();
        Assert.Equal(content, File.ReadAllBytes(await Download(content, content[..4])));
    }

    [Fact]
    public async Task Partial_file_longer_than_the_server_file_is_downloaded_again()
    {
        var content = "new upload"u8.ToArray();
        Assert.Equal(content, File.ReadAllBytes(await Download(content, "an older and longer upload"u8.ToArray())));
    }
}
