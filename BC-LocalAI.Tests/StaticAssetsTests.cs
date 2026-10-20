using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BC_LocalAI.Tests;

/// <summary>
/// The chat page's CSS and JavaScript must reach a real browser in every environment. Browsers always send
/// Accept-Encoding: gzip, br; the build serves pre-compressed copies for that, and outside Development those copies were
/// not found when the app ran from build output (dotnet run), so the files arrived empty and the page had no style or script.
/// </summary>
public sealed class StaticAssetsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    public static TheoryData<string, string, string> Cases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var environment in new[] { "Development", "Production" })
        {
            foreach (var encoding in new[] { "identity", "gzip", "br", "gzip, deflate, br" })
            {
                data.Add(environment, encoding, "css/chat.css");
                data.Add(environment, encoding, "js/chat.js");
                data.Add(environment, encoding, "js/chat-core.js");
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ChatPageAssets_ArriveComplete_ForEveryEncodingABrowserSends(string environment, string acceptEncoding, string path)
    {
        using var client = factory.CreateClientWithMock(environment);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/" + path);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Decode(response);
        var expected = await File.ReadAllTextAsync(Path.Combine(WebRoot(), path.Replace('/', Path.DirectorySeparatorChar)));
        Assert.True(body.Length > 0, $"{path} arrived empty ({environment}, Accept-Encoding: {acceptEncoding}).");
        Assert.Equal(Normalize(expected), Normalize(body));
    }

    [Theory]
    [InlineData("Development", "/")]
    [InlineData("Production", "/")]
    [InlineData("Development", "/Home/Error")]
    [InlineData("Production", "/Home/Error")]
    public async Task EveryLocalLinkOnThePage_Resolves_AndIsNotEmpty(string environment, string page)
    {
        using var client = factory.CreateClientWithMock(environment);
        var html = await client.GetStringAsync(page);

        var links = System.Text.RegularExpressions.Regex.Matches(html, "(?:href|src)=\"(/[^\"#]*)\"")
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(links);
        foreach (var link in links)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, link);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
            using var response = await client.SendAsync(request);

            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{link} on {page} returned {(int)response.StatusCode} ({environment}).");
            Assert.True((await Decode(response)).Length > 0, $"{link} on {page} arrived empty ({environment}).");
        }
    }

    private static async Task<string> Decode(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsByteArrayAsync();
        Stream stream = new MemoryStream(raw);
        foreach (var encoding in response.Content.Headers.ContentEncoding.Reverse())
        {
            stream = encoding switch
            {
                "gzip" => new GZipStream(stream, CompressionMode.Decompress),
                "br" => new BrotliStream(stream, CompressionMode.Decompress),
                "deflate" => new DeflateStream(stream, CompressionMode.Decompress),
                _ => throw new InvalidOperationException($"Unexpected Content-Encoding {encoding}")
            };
        }

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static string WebRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "BC-LocalAI", "wwwroot")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("wwwroot not found"), "BC-LocalAI", "wwwroot");
    }
}
