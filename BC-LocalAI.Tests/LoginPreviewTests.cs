using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

namespace BC_LocalAI.Tests;

public sealed class LoginPreviewTests
{
    private static WebApplicationFactory<Program> Factory(string environment, bool enabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.PinOllama();
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["FrontendPreview:Enabled"] = enabled.ToString() }));
        });

    private static async Task<(int Status, string Body, string Csp, string Cache)> Page(WebApplicationFactory<Program> factory,
        string? address = "127.0.0.1", string path = "/preview/login")
    {
        var context = await factory.Server.SendAsync(context =>
        {
            context.Request.Method = "GET"; context.Request.Path = path;
            context.Request.Scheme = "https"; context.Request.Host = new("localhost");
            context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);
        });
        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync(), context.Response.Headers.ContentSecurityPolicy.ToString(),
            context.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("Production", true, "127.0.0.1")]
    [InlineData("Production", false, "127.0.0.1")]
    [InlineData("Staging", true, "127.0.0.1")]
    [InlineData("Development", false, "127.0.0.1")]
    [InlineData("Development", true, "192.0.2.10")]
    [InlineData("Development", true, null)]
    public async Task PreviewFailsClosedUnlessDevelopmentExplicitFlagAndLoopback(string environment, bool enabled, string? address)
    {
        await using var factory = Factory(environment, enabled);
        var result = await Page(factory, address);
        Assert.Equal(404, result.Status); Assert.DoesNotContain("login-password", result.Body);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task PreviewHasExplicitMockWarningAndNoNetworkOrNativeFormSubmission(string address)
    {
        await using var factory = Factory("Development", true);
        var result = await Page(factory, address);
        Assert.Equal(200, result.Status); Assert.Contains("no-store", result.Cache);
        Assert.Contains("Mock Preview", result.Body);
        Assert.Contains("ไม่ใช่ระบบ Login จริง", result.Body);
        Assert.Contains("connect-src 'none'", result.Csp); Assert.Contains("form-action 'none'", result.Csp);
        Assert.Contains("frame-ancestors 'none'", result.Csp);
        Assert.Contains("type=\"password\"", result.Body); Assert.Contains("role=\"alert\"", result.Body);
        Assert.Contains("auth-core.js", result.Body); Assert.Contains("auth-mock.js", result.Body);
        Assert.Contains("auth-preview.js", result.Body); Assert.Contains("lang=\"th\"", result.Body);
    }

    [Fact]
    public async Task ConventionalRouteCannotBypassThePreviewGate()
    {
        await using var factory = Factory("Development", true);
        Assert.Equal(404, (await Page(factory, path: "/LoginPreview/Index")).Status);
    }

    [Fact]
    public async Task ChatRootDoesNotLoadMockAuthenticationAndAuthRemainsDisabled()
    {
        await using var factory = Factory("Development", true);
        var root = await Page(factory, path: "/");
        Assert.Equal(200, root.Status); Assert.DoesNotContain("auth-mock", root.Body);
        Assert.Contains("chat-stream.js", root.Body);
        Assert.Equal(404, (await Page(factory, path: "/api/auth/csrf")).Status);
    }

    [Fact]
    public async Task PreviewScriptsUseSafeDomAndNeverNetworkOrBrowserStorage()
    {
        await using var factory = Factory("Development", true);
        foreach (var path in new[] { "/js/auth-core.js", "/js/auth-mock.js", "/js/auth-preview.js" })
        {
            var script = await Page(factory, path: path);
            Assert.Equal(200, script.Status);
            Check.NotLeaked(script.Body, "innerHTML", "localStorage", "sessionStorage", "console.log", "fetch(", "document.cookie");
        }
    }
}
