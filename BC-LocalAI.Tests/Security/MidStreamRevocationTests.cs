using System.Net;
using System.Text;
using System.Text.Json;
using BC_LocalAI.Models;
using BC_LocalAI.Security;
using BC_LocalAI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BC_LocalAI.Tests.Security;

/// <summary>
/// Session ended while an answer is being produced (Issue #22 §4.4–4.5, review note on #22): the stream stops with
/// AUTH_SESSION_ENDED and sends nothing more; /api/chat does not hand over an answer that finished after the session ended.
/// Sign-out / revoke-all / 8 h stop at the next piece; an account change in the database within 5 s.
/// </summary>
public sealed class MidStreamRevocationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>Produces the pieces in order and runs the test's action after each one (server side, deterministic).</summary>
    private sealed class ScriptedStream : IStreamingAIService, IAIService
    {
        public string[] Pieces { get; init; } = ["A", "B", "C", "D"];
        public Func<int, Task> After { get; set; } = _ => Task.CompletedTask;
        public int Produced;

        public async Task<AiStreamCompletion> StreamResponseAsync(string message, Func<string, CancellationToken, Task> onDelta, CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < Pieces.Length; i++)
            {
                Produced++;
                await onDelta(Pieces[i], cancellationToken);
                await After(i);
            }

            return new AiStreamCompletion("stop", Pieces.Length);
        }

        public async Task<ChatResponse> GenerateResponseAsync(string message, CancellationToken cancellationToken = default)
        {
            await After(0);
            return new ChatResponse("SECRET-ANSWER-SENTINEL", "qwen3:4b");
        }
    }

    private (SecurityHost Host, ScriptedStream Ai) Create()
    {
        var ai = new ScriptedStream();
        var host = SecurityHost.Create(factory);
        host = host with
        {
            Factory = host.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStreamingAIService>(ai);
                services.AddSingleton<IAIService>(ai);
            }))
        };
        host.Users.Put("u-1");
        return (host, ai);
    }

    private static async Task<string> SignIn(SecurityHost host)
    {
        var response = await host.Raw().PostAsync("/api/__test/signin/u-1", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(BcAuthentication.CookieName + "=", StringComparison.Ordinal)).Split(';', 2)[0];
    }

    private static async Task<HttpResponseMessage> Post(SecurityHost host, string cookie, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"message":"hello"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Cookie", cookie);
        return await host.Raw().SendAsync(request);
    }

    private static List<(string Event, JsonElement Data)> Events(string sse) => sse
        .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
        .Select(block => block.Split('\n'))
        .Select(lines => (lines[0]["event: ".Length..], JsonDocument.Parse(lines[1]["data: ".Length..]).RootElement.Clone()))
        .ToList();

    private static void AssertStoppedAfter(string sse, params string[] delivered)
    {
        var events = Events(sse);
        Assert.Equal(["started", .. delivered.Select(_ => "delta"), "error"], events.Select(e => e.Event));
        Assert.Equal(delivered, events.Where(e => e.Event == "delta").Select(e => e.Data.GetProperty("text").GetString()));
        var error = events[^1].Data;
        Assert.Equal("AUTH_SESSION_ENDED", error.GetProperty("code").GetString());
        Assert.Equal(delivered.Length > 0, error.GetProperty("partial").GetBoolean());
    }

    [Theory]
    [InlineData("signout")]
    [InlineData("revoke-all")]
    public async Task SessionEndedElsewhere_StreamStopsAtTheNextPiece(string how)
    {
        var (host, ai) = Create();
        var cookie = await SignIn(host);
        ai.After = async i =>
        {
            if (i != 0) return;
            if (how == "signout")
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/__test/signout");
                request.Headers.Add("Cookie", cookie);   // the same session, signed out from another tab
                Assert.Equal(HttpStatusCode.NoContent, (await host.Raw().SendAsync(request)).StatusCode);
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, (await host.Raw().PostAsync("/api/__test/revoke-all/u-1", null)).StatusCode);
            }
        };

        var response = await Post(host, cookie, "/api/chat/stream");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertStoppedAfter(await response.Content.ReadAsStringAsync(), "A");
        Assert.Equal(2, ai.Produced);   // the piece being offered when it stopped; the model is not read any further
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(host, cookie, "/api/chat")).StatusCode);
    }

    [Fact]
    public async Task AccountDisabledInTheDatabase_StopsWithinTheCheckInterval_AndTheSessionIsRevoked()
    {
        var (host, ai) = Create();
        var cookie = await SignIn(host);
        ai.After = i =>
        {
            if (i == 0) host.Users.Put("u-1", active: false);                     // disabled now ...
            if (i == 1) host.Clock.Advance(StreamSessionGuard.AccountCheckInterval); // ... noticed at the next account check
            return Task.CompletedTask;
        };

        var response = await Post(host, cookie, "/api/chat/stream");

        AssertStoppedAfter(await response.Content.ReadAsStringAsync(), "A", "B");
        host.Users.Put("u-1");   // account restored: the ended session stays ended
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(host, cookie, "/api/chat")).StatusCode);
        Assert.Contains(host.Logs.Entries, e => e.EventId.Id == 5003 && e.Message.Contains("UserDisabled", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("stamp")]
    [InlineData("role")]
    [InlineData("deleted")]
    public async Task OtherAccountChanges_AlsoStopTheStream(string change)
    {
        var (host, ai) = Create();
        var cookie = await SignIn(host);
        ai.After = i =>
        {
            if (i != 0) return Task.CompletedTask;
            switch (change)
            {
                case "stamp": host.Users.Put("u-1", stamp: "S2"); break;
                case "role": host.Users.Put("u-1", role: BcRoles.Admin); break;
                case "deleted": host.Users.Users.TryRemove("u-1", out _); break;
            }

            host.Clock.Advance(StreamSessionGuard.AccountCheckInterval);
            return Task.CompletedTask;
        };

        var response = await Post(host, cookie, "/api/chat/stream");

        AssertStoppedAfter(await response.Content.ReadAsStringAsync(), "A");
    }

    [Fact]
    public async Task FailedPasswordLockoutDuringTheAnswer_DoesNotStopIt()
    {
        var (host, ai) = Create();
        var cookie = await SignIn(host);
        ai.After = i =>
        {
            if (i != 0) return Task.CompletedTask;
            host.Users.Put("u-1", lockout: host.Clock.Now.AddMinutes(15));   // someone typed 5 wrong passwords for this user
            host.Clock.Advance(StreamSessionGuard.AccountCheckInterval);
            return Task.CompletedTask;
        };

        var events = Events(await (await Post(host, cookie, "/api/chat/stream")).Content.ReadAsStringAsync());

        Assert.Equal("completed", events[^1].Event);
        Assert.Equal(4, events.Count(e => e.Event == "delta"));
    }

    [Fact]
    public async Task EightHoursReachedDuringTheAnswer_Stops()
    {
        var (host, ai) = Create();
        var cookie = await SignIn(host);
        for (var minutes = 0; minutes < 479; minutes += 20)   // active all day (a request every 20 minutes) ...
        {
            host.Clock.Advance(TimeSpan.FromMinutes(Math.Min(20, 479 - minutes)));
            Assert.Equal(HttpStatusCode.OK, (await Post(host, cookie, "/api/chat")).StatusCode);
        }
        // ... at 7 h 59 min the stream starts; the 8 h limit is reached during it.
        ai.After = i =>
        {
            if (i == 0) host.Clock.Advance(TimeSpan.FromMinutes(1));
            return Task.CompletedTask;
        };

        var response = await Post(host, cookie, "/api/chat/stream");

        AssertStoppedAfter(await response.Content.ReadAsStringAsync(), "A");
    }

    [Fact]
    public async Task UserStoreDownDuringTheAnswer_StopsFailClosed_ButDoesNotRevoke()
    {
        var (host, ai) = Create();
        var cookie = await SignIn(host);
        ai.After = i =>
        {
            if (i != 0) return Task.CompletedTask;
            host.Users.Unavailable = true;
            host.Clock.Advance(StreamSessionGuard.AccountCheckInterval);
            return Task.CompletedTask;
        };

        var response = await Post(host, cookie, "/api/chat/stream");

        AssertStoppedAfter(await response.Content.ReadAsStringAsync(), "A");
        host.Users.Unavailable = false;
        ai.After = _ => Task.CompletedTask;
        Assert.Equal(HttpStatusCode.OK, (await Post(host, cookie, "/api/chat")).StatusCode);   // the database is back: same session works
    }

    [Fact]
    public async Task LongActiveAnswer_IsNotEndedByTheIdleTimeout_AndQueriesTheAccountOnlyEveryInterval()
    {
        var (host, ai) = Create();
        var cookie = await SignIn(host);
        ai.After = i =>
        {
            host.Clock.Advance(TimeSpan.FromSeconds(1));   // 4 pieces, 1 s apart: within one check interval
            return Task.CompletedTask;
        };
        var readsBefore = host.Users.Reads;

        var response = await Post(host, cookie, "/api/chat/stream");
        var events = Events(await response.Content.ReadAsStringAsync());

        Assert.Equal("completed", events[^1].Event);
        Assert.Equal(["A", "B", "C", "D"], events.Where(e => e.Event == "delta").Select(e => e.Data.GetProperty("text").GetString()));
        Assert.Equal(1, host.Users.Reads - readsBefore);   // the cookie check only; no query per piece

        // A 31-minute answer (one piece every 10 minutes) is not cut by the 30-minute idle timeout: the user is active.
        ai.After = _ =>
        {
            host.Clock.Advance(TimeSpan.FromMinutes(10));
            return Task.CompletedTask;
        };
        var longOne = Events(await (await Post(host, cookie, "/api/chat/stream")).Content.ReadAsStringAsync());
        Assert.Equal("completed", longOne[^1].Event);
    }

    [Fact]
    public async Task ChatAnswer_FinishedAfterTheAccountWasDisabled_IsNotHandedOver()
    {
        var (host, ai) = Create();
        var cookie = await SignIn(host);
        ai.After = _ =>
        {
            host.Users.Put("u-1", active: false);
            host.Clock.Advance(StreamSessionGuard.AccountCheckInterval);
            return Task.CompletedTask;
        };

        var response = await Post(host, cookie, "/api/chat");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("AUTH_SESSION_ENDED", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-ANSWER-SENTINEL", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticationOff_NothingIsChecked_AnswerCompletes()
    {
        var ai = new ScriptedStream();
        var host = SecurityHost.Create(factory, enabled: false);
        host = host with { Factory = host.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IStreamingAIService>(ai))) };

        var response = await host.Raw().PostAsync("/api/chat/stream", new StringContent("""{"message":"hello"}""", Encoding.UTF8, "application/json"));

        Assert.Equal("completed", Events(await response.Content.ReadAsStringAsync())[^1].Event);
        Assert.Equal(0, host.Users.Reads);
    }
}
