using System.Text.Json;
using BC_LocalAI.Models.Authentication;

namespace BC_LocalAI.Tests.Authentication;

public sealed class AuthenticationContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void InboundCredentialsDeserializeButNeverSerializeBack()
    {
        const string body = """{"username":"synthetic.user","password":"synthetic-password"}""";
        var input = JsonSerializer.Deserialize<LoginRequest>(body, WebJson)!;
        Assert.Equal("synthetic.user", input.Username);
        Assert.Equal("synthetic-password", input.Password);
        Assert.Equal("{}", JsonSerializer.Serialize(input, WebJson));
        Assert.Equal("LoginRequest [REDACTED]", input.ToString());
    }

    [Theory]
    [InlineData("userId")]
    [InlineData("role")]
    [InlineData("passwordHash")]
    public void UndeclaredSensitiveOrAuthorizationFieldsAreRejected(string field)
    {
        var json = "{\"username\":\"synthetic\",\"password\":\"synthetic\",\"" + field + "\":\"forged\"}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LoginRequest>(json, WebJson));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("{\"username\":123}")]
    [InlineData("{\"password\":true}")]
    public void MalformedOrWrongTypeBodyCannotBecomeALoginRequest(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LoginRequest>(json, WebJson));

    [Fact]
    public void SuccessfulPublicDtosContainOnlyTheAgreedDraftProjection()
    {
        var user = new AuthenticatedUser("synthetic-id", "ผู้ใช้สมมติ", "synthetic", ["User"]);
        using var login = JsonDocument.Parse(JsonSerializer.Serialize(user, WebJson));
        Assert.Equal(["displayName", "id", "roles", "username"], login.RootElement.EnumerateObject().Select(p => p.Name).Order());
        using var me = JsonDocument.Parse(JsonSerializer.Serialize(new CurrentUserResponse(user), WebJson));
        Assert.Single(me.RootElement.EnumerateObject());
        Assert.Equal("synthetic-id", me.RootElement.GetProperty("user").GetProperty("id").GetString());
        Assert.DoesNotContain("synthetic-id", user.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void NoSuccessfulResultOrCurrentUserCanHaveAMissingStableId(string? id)
    {
        var user = new AuthenticatedUser(id!, "synthetic");
        Assert.Throws<ArgumentException>(() => AuthenticationResult.Succeeded(user));
        Assert.Throws<ArgumentException>(() => new CurrentUserResponse(user));
    }

    [Fact]
    public void MissingUserObjectIsRejectedBySuccessFactories()
    {
        Assert.Throws<ArgumentNullException>(() => AuthenticationResult.Succeeded(null!));
        Assert.Throws<ArgumentNullException>(() => new CurrentUserResponse(null!));
    }

    [Fact]
    public void NegativeOutcomesCannotCarryAUserAndDoNotExposeAccountReason()
    {
        Assert.Equal(AuthenticationOutcome.InvalidCredentials, default(AuthenticationOutcome));
        foreach (var result in new[] { AuthenticationResult.InvalidCredentials(), AuthenticationResult.Unavailable() })
        {
            Assert.Null(result.User);
            Assert.NotEqual(AuthenticationOutcome.Success, result.Outcome);
            Assert.Equal("{}", JsonSerializer.Serialize(result, WebJson));
        }
    }

    [Fact]
    public void SuccessResultRemainsAnInternalBoundaryRatherThanASessionOrHttpResponse()
    {
        var result = AuthenticationResult.Succeeded(new("synthetic-id", "synthetic-name"));
        Assert.Equal(AuthenticationOutcome.Success, result.Outcome);
        Assert.Equal("synthetic-id", result.User!.UserId);
        Assert.DoesNotContain("synthetic-id", result.ToString());
        Assert.Equal("{}", JsonSerializer.Serialize(result, WebJson));
    }
}
