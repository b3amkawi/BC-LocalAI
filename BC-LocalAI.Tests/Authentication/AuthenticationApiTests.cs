using System.Net;
using System.Text;
using System.Text.Json;
using BC_LocalAI.Models.Authentication;
using BC_LocalAI.Services.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BC_LocalAI.Tests.Authentication;

public sealed class AuthenticationApiTests
{
    internal const string Secret = "synthetic-secret-not-real";
    internal static WebApplicationFactory<Program> Factory(IAuthenticationBackend? backend = null, string environment = "Development", CapturingLoggerProvider? logs = null)
    {
        var keyPath = Path.Combine(Path.GetTempPath(), "bc-be005-test-keys");
        Directory.CreateDirectory(keyPath);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.PinOllama();
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Enabled"] = "true", ["Authentication:KeyDirectory"] = keyPath,
                ["Authentication:ReaderConnectionString"] = "Server=localhost;Database=synthetic;User ID=bc_auth_reader;Password=placeholder;SslMode=VerifyFull;Connection Timeout=3;MaximumPoolSize=4" // ci-allow-secret: fake placeholder test configuration, no database is contacted.
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticationBackend>();
                services.AddSingleton(backend ?? new StubAuthenticationBackend(AuthenticationResult.Succeeded(
                    new("00000000-0000-0000-0000-000000000005", "ผู้ใช้สมมติ", "synthetic", ["User"]))));
                services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
                if (logs is not null) services.AddLogging(logging => logging.AddProvider(logs));
            });
        });
    }

    internal static HttpClient Client(WebApplicationFactory<Program> factory, bool cookies = true, string scheme = "https") =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"{scheme}://localhost"), AllowAutoRedirect = false, HandleCookies = cookies });

    internal static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/csrf");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("token").GetString()!;
    }

    internal static async Task<HttpResponseMessage> Login(HttpClient client, string? json = null, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        request.Content = new StringContent(json ?? $$"""{"username":"synthetic","password":"{{Secret}}"}""", Encoding.UTF8, "application/json");
        request.Headers.Add("X-CSRF-TOKEN", token ?? await Csrf(client));
        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task LoginMeLogoutAndCookieReplayFollowTheApprovedContract(string environment)
    {
        await using var factory = Factory(environment: environment);
        using var client = Client(factory);
        var anonymous = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(anonymous.Headers.Location);
        var oldCsrf = await Csrf(client);
        using var login = await Login(client, token: oldCsrf);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Headers.CacheControl!.NoStore);
        var cookie = login.Headers.GetValues("Set-Cookie").Last(value => value.StartsWith("__Host-BC.LocalAI=") && !value.StartsWith("__Host-BC.LocalAI=;"));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        using var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.Content.ReadAsStringAsync();
        Assert.Equal(await login.Content.ReadAsStringAsync(), body);
        Check.NotLeaked(body, Secret, "password", "stamp", "hash");
        using var rejected = await Login(client, token: oldCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logoutRequest.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        using var logout = await client.SendAsync(logoutRequest);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var replayClient = Client(factory, cookies: false);
        replayClient.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replayClient.GetAsync("/api/auth/me")).StatusCode);
        using var idempotentRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        idempotentRequest.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(idempotentRequest)).StatusCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("{\"username\":\"synthetic\",\"username\":\"other\",\"password\":\"synthetic\"}")]
    [InlineData("{\"username\":\"synthetic\",\"password\":\"synthetic\",\"role\":\"Admin\"}")]
    [InlineData("{\"username\":123,\"password\":true}")]
    [InlineData("{\"username\":\"user' OR 1=1 --\",\"password\":\"synthetic\"}")]
    [InlineData("{\"Username\":\"synthetic\",\"password\":\"synthetic\"}")]
    public async Task MalformedDuplicateUnknownOrInvalidInputNeverCallsBackend(string json)
    {
        var stub = new StubAuthenticationBackend(AuthenticationResult.InvalidCredentials());
        await using var factory = Factory(stub);
        using var client = Client(factory);
        using var result = await Login(client, json);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal(0, stub.Calls);
        Assert.Contains("AUTH_INVALID_REQUEST", await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OversizedBodyIsRejectedBeforeBackend()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        using var response = await Login(client, new string('x', 16385));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task ChunkedBodyCannotBypassActualByteLimit()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = new UnknownLengthContent() };
        request.Headers.Add("X-CSRF-TOKEN", await Csrf(client));
        Assert.Null(request.Content.Headers.ContentLength);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("logout")]
    public async Task GetCannotMutateAuthenticationAndCookiesCannotBypassCsrf(string action)
    {
        await using var factory = Factory();
        using var client = Client(factory);
        using var get = await client.GetAsync($"/api/auth/{action}");
        Assert.Contains(get.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.False(get.Headers.Contains("Set-Cookie"));
        using var login = await Login(client);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var noToken = await client.PostAsync($"/api/auth/{action}", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task ReloginReplacesRatherThanReusesTheOldSession()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        using var first = await Login(client);
        var cookie = first.Headers.GetValues("Set-Cookie").Last(value => value.StartsWith("__Host-BC.LocalAI=") && !value.StartsWith("__Host-BC.LocalAI=;"));
        using var second = await Login(client);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var replay = Client(factory, cookies: false);
        replay.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCookieValidationEnforcesIdleAndAbsoluteExpiry(bool active)
    {
        var clock = new AuthenticationServiceTests.Clock();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }));
        using var client = Client(factory);
        using var login = await Login(client);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        if (active)
        {
            for (var minute = 20; minute <= 460; minute += 20)
            {
                clock.Now += TimeSpan.FromMinutes(20);
                using var me = await client.GetAsync("/api/auth/me");
                Assert.Equal(HttpStatusCode.OK, me.StatusCode);
            }
            clock.Now += TimeSpan.FromMinutes(20);
        }
        else clock.Now += TimeSpan.FromMinutes(30);
        using var expired = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Contains("AUTH_REQUIRED", await expired.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("http", false)]
    [InlineData("https", false)]
    [InlineData("https", true)]
    public async Task HttpMissingCsrfAndCrossOriginRequestsAreRejected(string scheme, bool crossOrigin)
    {
        var stub = new StubAuthenticationBackend(AuthenticationResult.InvalidCredentials());
        await using var factory = Factory(stub);
        using var client = Client(factory, scheme: scheme);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (crossOrigin) request.Headers.Add("Origin", "https://attacker.invalid");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(0, stub.Calls);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task SameOriginDefaultPortSpellingIsComparedCanonically()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        client.DefaultRequestHeaders.Host = "localhost:443";
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        using var login = await Login(client);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task AccountAttemptBudgetReturns429BeforeSixthVerification()
    {
        var stub = new StubAuthenticationBackend(AuthenticationResult.InvalidCredentials());
        await using var factory = Factory(stub);
        using var client = Client(factory);
        for (var i = 0; i < 5; i++) using (var failure = await Login(client)) Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        using var limited = await Login(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), limited.Headers.RetryAfter!.Delta);
        Assert.Equal(5, stub.Calls);
    }

    [Fact]
    public async Task SafeUnavailableAndUnexpectedErrorsDoNotLeakIntoLogsOrClient()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = Factory(new ThrowingBackend(), logs: logs);
        using var client = Client(factory);
        using var failure = await Login(client);
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Check.NotLeaked(await failure.Content.ReadAsStringAsync(), Secret, "raw-sql", "raw-hash");
        foreach (var entry in logs.Entries)
        {
            Check.NotLeaked(entry.Message, Secret, "raw-sql", "raw-hash");
            Check.NotLeaked(entry.Exception?.ToString() ?? "", Secret, "raw-sql", "raw-hash");
            foreach (var field in entry.Fields.Values) Check.NotLeaked(field?.ToString() ?? "", Secret, "raw-sql", "raw-hash");
        }
        await using var unavailableFactory = Factory(new StubAuthenticationBackend(AuthenticationResult.Unavailable()));
        using var unavailableClient = Client(unavailableFactory);
        using var unavailable = await Login(unavailableClient);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Contains("AUTH_UNAVAILABLE", await unavailable.Content.ReadAsStringAsync());
    }

    private sealed class ThrowingBackend : IAuthenticationBackend
    {
        public Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException($"{Secret} raw-sql raw-hash");
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        public UnknownLengthContent() => Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[16385]).AsTask();
    }
}
