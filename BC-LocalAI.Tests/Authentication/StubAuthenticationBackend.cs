using BC_LocalAI.Models.Authentication;
using BC_LocalAI.Services.Authentication;

namespace BC_LocalAI.Tests.Authentication;

// Test-only seam. Never add this to the application assembly or use it as a fallback when MySQL is unavailable.
internal sealed class StubAuthenticationBackend(AuthenticationResult result) : IAuthenticationBackend
{
    public int Calls { get; private set; }

    public Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(result);
    }
}
