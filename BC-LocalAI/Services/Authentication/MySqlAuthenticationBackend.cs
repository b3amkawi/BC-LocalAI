using BC_LocalAI.Models.Authentication;

namespace BC_LocalAI.Services.Authentication;

public sealed class MySqlAuthenticationBackend(
    IAuthenticationRepository repository, IAuthenticationPasswordVerifier verifier, TimeProvider clock,
    ILogger<MySqlAuthenticationBackend> logger, IAuthenticationAttemptStore attempts) : IAuthenticationBackend
{
    public async Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AuthenticationRequestValidator.Validate(request) is not null) return AuthenticationResult.InvalidCredentials();
        try
        {
            var account = await repository.FindAsync(request.Username!.ToUpperInvariant(), cancellationToken);
            // Always perform one bounded KDF, including unknown/disabled/locked accounts. Never log username/hash/password.
            var valid = verifier.Verify(account?.PasswordHash, request.Password!);
            cancellationToken.ThrowIfCancellationRequested();
            var eligible = account is not null && account.IsActive && !(account.LockoutEndUtc > clock.GetUtcNow()) &&
                Guid.TryParseExact(account.Id, "D", out _) && account.Role is ("User" or "Admin") &&
                account.SecurityStamp.Length == 64 && account.SecurityStamp.All(Uri.IsHexDigit);
            // Known/unknown/wrong/disabled/locked all perform one writer roundtrip. Null predicates cannot match any row.
            var recorded = await attempts.RecordAsync(eligible ? account!.Id : null, eligible ? account!.SecurityStamp : null, eligible && valid, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!valid || !eligible || !recorded)
                return AuthenticationResult.InvalidCredentials();
            return AuthenticationResult.Succeeded(new(account!.Id, account.DisplayName, account.Username, [account.Role]));
        }
        catch (AuthenticationDependencyException)
        {
            logger.LogWarning(new EventId(5101, "AuthenticationDependencyUnavailable"), "Authentication dependency unavailable.");
            return AuthenticationResult.Unavailable();
        }
    }
}
