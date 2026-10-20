using System.Net;
using System.Text;
using BC_LocalAI.Models.Authentication;
using BC_LocalAI.Services.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BC_LocalAI.Tests.Authentication;

// Preparation-stage gate. Replace only after approved SEC-001 + complete session/CSRF integration.
public sealed class AuthenticationPreparationBoundaryTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task PreparationDoesNotExposeAuthRoutesOrRegisterARealBackend(string environment)
    {
        await using var factory = Factory(environment);
        using var client = factory.CreateClient();
        Assert.Null(factory.Services.GetService<IAuthenticationBackend>());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/auth/me")).StatusCode);
        using var login = await client.PostAsync("/api/auth/login", new StringContent("{}", Encoding.UTF8, "application/json"));
        using var logout = await client.PostAsync("/api/auth/logout", new StringContent("{}", Encoding.UTF8, "application/json"));
        AssertRejected(login.StatusCode);
        AssertRejected(logout.StatusCode);
        Assert.False(login.Headers.Contains("Set-Cookie"));
        Assert.False(logout.Headers.Contains("Set-Cookie"));
        AssertNoAuthRoute(factory);
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
        AssertNoAuthRoute(factory);
    }

    private static WebApplicationFactory<Program> Factory(string environment) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.PinOllama();
        });

    private static void AssertRejected(HttpStatusCode status) =>
        Assert.Contains(status, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });

    private static void AssertNoAuthRoute(WebApplicationFactory<Program> factory) =>
        Assert.DoesNotContain(factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>(),
            endpoint => (endpoint.RoutePattern.RawText ?? "").TrimStart('/').StartsWith("api/auth", StringComparison.OrdinalIgnoreCase));
}
