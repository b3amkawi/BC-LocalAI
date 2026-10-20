using BC_LocalAI.Models.Authentication;

namespace BC_LocalAI.Services.Authentication;

/// <summary>
/// Draft boundary for the SEC-001-reviewed authentication adapter, not an implementation.
/// No production implementation, DI registration, MySQL query or password verifier is supplied here.
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
