using BC_LocalAI.Models.Authentication;

namespace BC_LocalAI.Services.Authentication;

public sealed class MySqlAuthenticationBackend(
    IAuthenticationRepository repository, IAuthenticationPasswordVerifier verifier, TimeProvider clock,
    ILogger<MySqlAuthenticationBackend> logger) : IAuthenticationBackend
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
            if (!valid || account is null || !account.IsActive || account.LockoutEndUtc > clock.GetUtcNow() ||
                !Guid.TryParseExact(account.Id, "D", out _) || account.Role is not ("User" or "Admin"))
                return AuthenticationResult.InvalidCredentials();
            return AuthenticationResult.Succeeded(new(account.Id, account.DisplayName, account.Username, [account.Role]));
        }
        catch (AuthenticationDependencyException)
        {
            logger.LogWarning(new EventId(5101, "AuthenticationDependencyUnavailable"), "Authentication dependency unavailable.");
            return AuthenticationResult.Unavailable();
        }
    }
}
