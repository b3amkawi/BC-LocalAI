using System.Net;
using System.Text;
using BC_LocalAI.Models.Authentication;
using BC_LocalAI.Services.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BC_LocalAI.Tests.Authentication;

// Configuration gate: adding implementations or a test stub must never activate Authentication by default.
public sealed class AuthenticationPreparationBoundaryTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task DisabledConfigurationBlocksEveryAuthEndpointWithoutCookies(string environment)
    {
        await using var factory = Factory(environment);
        using var client = factory.CreateClient();
        Assert.False(factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BC_LocalAI.Configuration.AuthenticationOptions>>().Value.Enabled);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/auth/me")).StatusCode);
        using var login = await client.PostAsync("/api/auth/login", new StringContent("{}", Encoding.UTF8, "application/json"));
        using var logout = await client.PostAsync("/api/auth/logout", new StringContent("{}", Encoding.UTF8, "application/json"));
        AssertRejected(login.StatusCode);
        AssertRejected(logout.StatusCode);
        Assert.False(login.Headers.Contains("Set-Cookie"));
        Assert.False(logout.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task InjectingATestOnlyBackendCannotAccidentallyEnableLoginOrIssueCookies()
    {
        var stub = new StubAuthenticationBackend(AuthenticationResult.Succeeded(new("synthetic-id", "synthetic")));
        await using var factory = Factory("Production").WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IAuthenticationBackend>(stub)));
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/auth/login", new StringContent("{}", Encoding.UTF8, "application/json"));
        AssertRejected(response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, stub.Calls);
    }

    private static WebApplicationFactory<Program> Factory(string environment) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.PinOllama();
        });

    private static void AssertRejected(HttpStatusCode status) =>
        Assert.Contains(status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });

}
