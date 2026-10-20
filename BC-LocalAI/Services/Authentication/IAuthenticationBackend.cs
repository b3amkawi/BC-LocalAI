using BC_LocalAI.Models.Authentication;

namespace BC_LocalAI.Services.Authentication;

/// <summary>
/// Boundary for the SEC-001 local MySQL account architecture. Enabled explicitly by configuration.
/// Credentials and persistence fields never leave the verifier/service boundary.
/// </summary>
public interface IAuthenticationBackend
{
    /// <summary>
    /// Future implementation must validate inputs, preserve the exact password, perform approved read-only
    /// parameterized lookup/verification, check account status and return only the safe user projection.
    /// Unknown/wrong/disabled/locked share InvalidCredentials; dependency failure returns safe Unavailable.
    /// Never log the request or raw exception/DB/hash; cancellation propagates, with no automatic credential retry.
    /// Success is not a session: the future controller must also establish the approved ASP.NET authentication mechanism.
    /// </summary>
    Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default);
}
