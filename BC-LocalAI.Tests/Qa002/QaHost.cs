using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BC_LocalAI.Models.Authentication;
using BC_LocalAI.Security;
using BC_LocalAI.Services;
using BC_LocalAI.Services.Authentication;
using BC_LocalAI.Services.ChatHistory;
using BC_LocalAI.Tests.ChatHistory;
using BC_LocalAI.Tests.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Tests.Qa002;

/// <summary>Fake Ollama: counts every request that reaches it and behaves as the test says. Never a real network call.</summary>
internal sealed class FakeOllama : HttpMessageHandler
{
    public const string InternalCanary = "OLLAMA-INTERNAL-CANARY C:\\Users\\svc\\.ollama\\runner 127.0.0.1:11434";

    private int _count;
    private int _streams;

    public int Count => Volatile.Read(ref _count);
    public int Streams => Volatile.Read(ref _streams);
    public string Mode { get; set; } = "ok"; // ok | http500 | down | garbage
    public Func<bool, CancellationToken, Task<HttpResponseMessage>>? Custom { get; set; }

    public void Reset() { Interlocked.Exchange(ref _count, 0); Interlocked.Exchange(ref _streams, 0); }

    public static string Record(string fragment, bool done = false) =>
        JsonSerializer.Serialize(new { model = "qwen3:4b", response = fragment, done }) + "\n";

    public static string Final() =>
        JsonSerializer.Serialize(new { model = "qwen3:4b", response = "", done = true, done_reason = "stop", eval_count = 5 }) + "\n";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _count);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var stream = JsonDocument.Parse(body).RootElement.GetProperty("stream").GetBoolean();
        if (stream) Interlocked.Increment(ref _streams);
        if (Custom is not null) return await Custom(stream, cancellationToken);
        switch (Mode)
        {
            case "down": throw new HttpRequestException("No connection could be made because the target machine actively refused it. 127.0.0.1:11434");
            case "http500": return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(InternalCanary) };
            case "garbage":
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(InternalCanary + " <<not json>>", Encoding.UTF8, stream ? "application/x-ndjson" : "application/json")
                };
        }

        if (!stream)
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model = "qwen3:4b", response = "{\"answer\":\"synthetic answer from the fake model\"}", done = true, done_reason = "stop"
                }), Encoding.UTF8, "application/json")
            };

        var text = Record("{\"answer\":\"First piece of the synthetic answer, long enough to be sent. ")
                   + Record("Second piece of the synthetic answer, also long enough. ")
                   + Record("Third and last piece of the synthetic answer.\"}") + Final();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/x-ndjson") };
    }
}

/// <summary>A stream the test paces: pieces, then a named gate the test releases (or never releases) at the moment it chooses.</summary>
internal sealed class ScriptedStream
{
    private sealed class Tracking(Stream inner, TaskCompletionSource closed) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { closed.TrySetResult(); inner.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private readonly Pipe _pipe = new();
    private readonly List<Func<Task>> _steps = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _reached = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _released = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;

    public ScriptedStream Open() => Add(() => Write(FakeOllama.Record("{\"answer\":\"")));

    public ScriptedStream Text(string text) => Add(() => Write(FakeOllama.Record(text)));

    public ScriptedStream Gate(string name) => Add(async () =>
    {
        Source(_reached, name).TrySetResult();
        await Source(_released, name).Task;
    });

    public ScriptedStream Finish() => Add(() => Write(FakeOllama.Record("\"}") + FakeOllama.Final(), complete: true));

    /// <summary>Completes when the model has produced everything before this gate.</summary>
    public Task Reached(string name) => Source(_reached, name).Task.WaitAsync(TimeSpan.FromSeconds(20));

    public void Release(string name) => Source(_released, name).TrySetResult();

    /// <summary>Completes when the application closed its side of the Ollama connection.</summary>
    public Task Closed => _closed.Task;

    public HttpResponseMessage ToResponse()
    {
        if (!_started)
        {
            _started = true;
            _ = Task.Run(async () =>
            {
                try { foreach (var step in _steps) await step(); }
                catch (Exception) { /* the application closed the connection */ }
                finally { await _pipe.Writer.CompleteAsync(); }
            });
        }

        var content = new StreamContent(new Tracking(_pipe.Reader.AsStream(), _closed));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson") { CharSet = "utf-8" };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private ScriptedStream Add(Func<Task> step) { _steps.Add(step); return this; }

    private async Task Write(string text, bool complete = false)
    {
        var result = await _pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
        if (result.IsCompleted) throw new IOException("closed");
        if (complete) await _pipe.Writer.CompleteAsync();
    }

    private static TaskCompletionSource Source(ConcurrentDictionary<string, TaskCompletionSource> map, string name) =>
        map.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}

internal sealed record Acct(string Name, string Id, string Role, string Password);

/// <summary>Stand-in for BE-005's MySQL backend: checks the synthetic account table and the account state, like the real one answers.</summary>
internal sealed class QaBackend(QaHost host) : IAuthenticationBackend
{
    public bool Unavailable { get; set; }

    public Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        if (Unavailable) return Task.FromResult(AuthenticationResult.Unavailable());
        if (!host.Accounts.TryGetValue(request.Username!.ToUpperInvariant(), out var account) || account.Password != request.Password)
            return Task.FromResult(AuthenticationResult.InvalidCredentials());
        if (!host.Users.Users.TryGetValue(account.Id, out var state) || !state.IsActive || state.LockoutEnd > host.Clock.Now)
            return Task.FromResult(AuthenticationResult.InvalidCredentials());
        return Task.FromResult(AuthenticationResult.Succeeded(new(account.Id, "ผู้ใช้สมมติ " + account.Name, account.Name, [state.Role])));
    }
}

internal sealed record Resp(HttpStatusCode Status, string Body, HttpResponseMessage Message)
{
    public int Code => (int)Status;

    public string? ErrorCode
    {
        get
        {
            try { return JsonDocument.Parse(Body).RootElement.GetProperty("error").GetProperty("code").GetString(); }
            catch (Exception) { return null; }
        }
    }

    public string? Header(string name) =>
        Message.Headers.TryGetValues(name, out var values) ? string.Join(",", values)
        : Message.Content.Headers.TryGetValues(name, out var contentValues) ? string.Join(",", contentValues) : null;

    public IReadOnlyList<string> SetCookies => Message.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];

    public string? SessionSetCookie => SetCookies.LastOrDefault(c => c.StartsWith(BcAuthentication.CookieName + "=", StringComparison.Ordinal));

    public bool IssuesSession => SessionSetCookie is { } cookie && !cookie.StartsWith(BcAuthentication.CookieName + "=;", StringComparison.Ordinal);

    public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
}

internal sealed record Session(string Pair, string SetCookie, string Name)
{
    public string Value => Pair[(Pair.IndexOf('=') + 1)..];
}

internal sealed class SseEvent(string name, string data)
{
    public string Name { get; } = name;
    public string Data { get; } = data;
    public JsonElement Json => JsonDocument.Parse(Data).RootElement;
    public string? Code => Name == "error" ? Json.GetProperty("code").GetString() : null;
}

internal sealed class QaHost
{
    public const string SameOrigin = "https://localhost";

    public static readonly IReadOnlyDictionary<string, string> BrowserHeaders = new Dictionary<string, string>
    {
        ["Origin"] = SameOrigin, ["Sec-Fetch-Site"] = "same-origin", ["Sec-Fetch-Mode"] = "cors"
    };

    public ConcurrentDictionary<string, Acct> Accounts { get; } = new();
    public FakeOllama Ollama { get; } = new();
    public QaBackend Backend { get; private set; } = null!;
    public InMemoryChatHistoryRepository? History { get; private set; }
    public SecurityHost Sec { get; private set; } = null!;
    public WebApplicationFactory<Program> Factory => Sec.Factory;
    public FakeUserStore Users => Sec.Users;
    public ManualClock Clock => Sec.Clock;
    public CapturingLoggerProvider Logs => Sec.Logs;
    public string BaseAddress { get; private set; } = SameOrigin;
    public bool AutoCsrf { get; set; } = true;

    /// <summary>Random per run: synthetic accounts never share a password with anything real or with an earlier run.</summary>
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static QaHost Create(WebApplicationFactory<Program> factory, bool history = false, string environment = "Production",
        bool authentication = true, string host = "localhost", Action<IServiceCollection>? services = null,
        IDictionary<string, string?>? settings = null)
    {
        var qa = new QaHost { BaseAddress = "https://" + host };
        qa.Sec = SecurityHost.Create(factory, enabled: authentication);
        qa.Backend = new QaBackend(qa);
        if (history) qa.History = new InMemoryChatHistoryRepository(() => qa.Clock.Now);
        var configured = new Dictionary<string, string?>();
        if (history)
        {
            configured["ChatHistory:Enabled"] = "true";
            configured["ChatHistory:ConnectionString"] = "Server=localhost;Database=synthetic;User ID=bc_history_rw;Password=placeholder-history;SslMode=VerifyFull;Connection Timeout=3;MaximumPoolSize=4"; // ci-allow-secret: fake placeholder history configuration, no database is contacted.
        }

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>()) configured[key] = value;
        qa.Sec = qa.Sec with
        {
            Factory = qa.Sec.Factory.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configured));
                builder.ConfigureTestServices(s =>
                {
                    s.AddHttpClient<IAIService, OllamaAIService>().ConfigurePrimaryHttpMessageHandler(() => qa.Ollama);
                    s.AddHttpClient<IStreamingAIService, OllamaStreamingService>().ConfigurePrimaryHttpMessageHandler(() => qa.Ollama);
                    s.RemoveAll<IAuthenticationBackend>();
                    s.AddSingleton<IAuthenticationBackend>(qa.Backend);
                    if (qa.History is not null)
                    {
                        s.RemoveAll<IChatHistoryRepository>();
                        s.AddSingleton<IChatHistoryRepository>(qa.History);
                    }

                    services?.Invoke(s);
                });
            })
        };
        return qa;
    }

    public Acct AddUser(string name, string role = BcRoles.User, bool active = true, string? stamp = null)
    {
        var id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes("qa002-" + name))).ToString("D");
        var account = new Acct(name, id, role, NewSecret());
        Accounts[name.ToUpperInvariant()] = account;
        Users.Put(id, role, active, stamp ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        return account;
    }

    public HttpClient Client(bool http = false) => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri((http ? "http://" : "https://") + new Uri(BaseAddress).Host),
        AllowAutoRedirect = false,
        HandleCookies = false
    });

    public async Task<Resp> Send(HttpMethod method, string path, string? cookie = null, object? json = null, string? body = null,
        string contentType = "application/json", IEnumerable<KeyValuePair<string, string>>? headers = null, bool http = false,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var extra = new List<KeyValuePair<string, string>>(headers ?? []);
        // FE-003: cookie-backed history mutations need HTTPS and a valid X-CSRF-TOKEN. The page does this; the harness does it for
        // every older case so their Expected results stay unchanged. Tests of the token itself switch AutoCsrf off.
        var payload = body ?? (json is null ? "" : JsonSerializer.Serialize(json));
        if (AutoCsrf && cookie is not null && !http && method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options &&
            !path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase) && !extra.Any(h => h.Key.Equals("X-CSRF-TOKEN", StringComparison.OrdinalIgnoreCase)) &&
            (path.StartsWith("/api/conversations", StringComparison.OrdinalIgnoreCase) || payload.Contains("\"conversationId\"", StringComparison.Ordinal)))
        {
            var (pair, token) = await Csrf(cookie);
            cookie += "; " + pair;
            extra.Add(new("X-CSRF-TOKEN", token));
        }

        headers = extra;
        using var client = Client(http);
        var request = new HttpRequestMessage(method, path);
        if (json is not null || body is not null)
            request.Content = new StringContent(body ?? JsonSerializer.Serialize(json), Encoding.UTF8, new MediaTypeHeaderValue(contentType.Split(';')[0]));
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        foreach (var (key, value) in headers ?? []) request.Headers.TryAddWithoutValidation(key, value);
        var response = await client.SendAsync(request, completion);
        var text = completion == HttpCompletionOption.ResponseContentRead ? await response.Content.ReadAsStringAsync() : "";
        return new Resp(response.StatusCode, text, response);
    }

    public Task<Resp> Get(string path, string? cookie = null, IEnumerable<KeyValuePair<string, string>>? headers = null) =>
        Send(HttpMethod.Get, path, cookie, headers: headers);

    /// <summary>A same-site browser request (Origin and Fetch Metadata like fetch() from the chat page).</summary>
    public Task<Resp> Browser(HttpMethod method, string path, string? cookie, object? json = null) =>
        Send(method, path, cookie, json, headers: BrowserHeaders);

    public Task<Resp> Chat(string? cookie, string message = "hello", string? conversationId = null, IEnumerable<KeyValuePair<string, string>>? headers = null, bool stream = false) =>
        Send(HttpMethod.Post, stream ? "/api/chat/stream" : "/api/chat", cookie,
            conversationId is null ? new { message } : new { message, conversationId }, headers: headers ?? BrowserHeaders);

    /// <summary>The CSRF cookie of GET /api/auth/csrf (bound to the session cookie when one is given) and its matching token.</summary>
    public async Task<(string CookiePair, string Token)> Csrf(string? sessionCookie = null)
    {
        var response = await Get("/api/auth/csrf", sessionCookie);
        var set = response.SetCookies.Last(c => c.StartsWith("__Host-BC.Csrf=", StringComparison.Ordinal)).Split(';', 2)[0];
        return (set, response.Json.GetProperty("token").GetString()!);
    }

    public async Task<Resp> LoginRaw(string username, string password, string? sessionCookie = null, (string CookiePair, string Token)? csrf = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null, string? body = null, string contentType = "application/json", bool http = false)
    {
        var (pair, token) = csrf ?? await Csrf(sessionCookie);
        var all = new List<KeyValuePair<string, string>>(headers ?? []) { new("X-CSRF-TOKEN", token) };
        var cookie = sessionCookie is null ? pair : sessionCookie + "; " + pair;
        return await Send(HttpMethod.Post, "/api/auth/login", cookie, body is null ? new { username, password } : null, body, contentType, all, http);
    }

    public async Task<Session> Login(Acct account, string? previousCookie = null)
    {
        var response = await LoginRaw(account.Name, account.Password, previousCookie);
        if (response.Code != 200) throw new Xunit.Sdk.XunitException($"Synthetic login of {account.Name} failed: {response.Code} {response.ErrorCode}");
        var set = response.SessionSetCookie ?? throw new Xunit.Sdk.XunitException("Login set no session cookie.");
        return new Session(set.Split(';', 2)[0], set, account.Name);
    }

    public async Task<Resp> Logout(Session session, bool withToken = true)
    {
        var (pair, token) = await Csrf(session.Pair);
        return await Send(HttpMethod.Post, "/api/auth/logout", session.Pair + "; " + pair, headers: withToken ? [new("X-CSRF-TOKEN", token)] : []);
    }

    /// <summary>The server-side session id inside a cookie value (for scanning logs for it).</summary>
    public string SessionIdOf(Session session)
    {
        var options = Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(BcAuthentication.Scheme);
        return options.TicketDataFormat.Unprotect(session.Value)!.Principal!.FindFirst(BcClaimTypes.SessionId)!.Value;
    }

    // ------------------------------------------------------------------ streams
    public async Task<(Resp Head, List<SseEvent> Events)> Stream(string? cookie, string message = "hello", string? conversationId = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        var head = await Chat(cookie, message, conversationId, headers, stream: true);
        return (head, head.Message.Content.Headers.ContentType?.MediaType == "text/event-stream" ? Parse(head.Body) : []);
    }

    public static List<SseEvent> Parse(string sse) => sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
        .Select(block => block.Split('\n'))
        .Where(lines => lines.Length >= 2 && lines[0].StartsWith("event: ") && lines[1].StartsWith("data: "))
        .Select(lines => new SseEvent(lines[0]["event: ".Length..], lines[1]["data: ".Length..])).ToList();

    /// <summary>Starts a stream and returns once the response headers are in; the body is read by <see cref="ReadAllAsync"/>.</summary>
    public async Task<HttpResponseMessage> OpenStream(string cookie, string message = "hello", string? conversationId = null)
    {
        using var client = Client();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
        {
            Content = new StringContent(JsonSerializer.Serialize(conversationId is null ? new { message } : (object)new { message, conversationId }), Encoding.UTF8, "application/json")
        };
        if (conversationId is not null && AutoCsrf)
        {
            var (pair, token) = await Csrf(cookie); // FE-003: a stream that saves history needs the CSRF header
            cookie += "; " + pair;
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
        }

        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        foreach (var (key, value) in BrowserHeaders) request.Headers.TryAddWithoutValidation(key, value);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    public static async Task<(string Raw, List<SseEvent> Events)> ReadAllAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (raw, Parse(raw));
    }

    /// <summary>Everything the application logged (messages, structured fields and exceptions): used to prove a value never reached a log.</summary>
    public string LogText => Logs.Text + "\n" + string.Join("\n", Logs.Entries.Select(e => e.Exception?.ToString() ?? "")) +
                             "\n" + string.Join("\n", Logs.Entries.SelectMany(e => e.Fields.Select(f => f.Key + "=" + f.Value)));
}
