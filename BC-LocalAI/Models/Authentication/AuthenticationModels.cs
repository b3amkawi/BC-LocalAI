using System.Text.Json.Serialization;

namespace BC_LocalAI.Models.Authentication;

/// <summary>Safe public projection only; never add hash/salt/database/security-stamp fields here.</summary>
public sealed record AuthenticatedUser(
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("displayName")] string DisplayName)
{
    public override string ToString() => "AuthenticatedUser [REDACTED]";
}

/// <summary>Draft success response for GET /api/auth/me. Anonymous behaviour still needs SEC-001 approval.</summary>
public sealed class CurrentUserResponse
{
    public CurrentUserResponse(AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(user.UserId)) throw new ArgumentException("A stable user id is required.", nameof(user));
        User = user;
    }

    [JsonPropertyName("authenticated")]
    public bool Authenticated => true;

    [JsonPropertyName("user")]
    public AuthenticatedUser User { get; }

    public override string ToString() => "CurrentUserResponse [REDACTED]";
}

// Default enum value is rejection, never success.
public enum AuthenticationOutcome { InvalidCredentials = 0, Success = 1, Unavailable = 2 }

/// <summary>Service boundary result, not a session, token or HTTP response. Private factories enforce result invariants.</summary>
public sealed class AuthenticationResult
{
    private AuthenticationResult(AuthenticationOutcome outcome, AuthenticatedUser? user)
    {
        Outcome = outcome;
        User = user;
    }

    [JsonIgnore]
    public AuthenticationOutcome Outcome { get; }

    [JsonIgnore]
    public AuthenticatedUser? User { get; }

    public static AuthenticationResult Succeeded(AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(user.UserId)) throw new ArgumentException("A stable user id is required.", nameof(user));
        return new(AuthenticationOutcome.Success, user);
    }

    // Wrong password, missing user, disabled and locked must share this public result.
    public static AuthenticationResult InvalidCredentials() => new(AuthenticationOutcome.InvalidCredentials, null);
    public static AuthenticationResult Unavailable() => new(AuthenticationOutcome.Unavailable, null);

    public override string ToString() => $"AuthenticationResult {{ Outcome = {Outcome} }}";
}
