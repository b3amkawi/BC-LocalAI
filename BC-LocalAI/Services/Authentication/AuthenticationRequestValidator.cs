using System.Text;
using BC_LocalAI.Models;
using BC_LocalAI.Models.Authentication;

namespace BC_LocalAI.Services.Authentication;

/// <summary>Draft input resource ceilings, not company password/account policy. Review against SEC-001 before wiring in.</summary>
public sealed record AuthenticationInputLimits(
    int MaxUsernameCharacters = 128,
    int MaxPasswordCharacters = 1024,
    int MaxPasswordUtf8Bytes = 4096);

/// <summary>Pure, offline preparation. Does not authenticate, normalize credentials, query a database or create sessions.</summary>
public static class AuthenticationRequestValidator
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static ApiError? Validate(LoginRequest? request, AuthenticationInputLimits? limits = null)
    {
        limits ??= new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxUsernameCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxPasswordCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxPasswordUtf8Bytes);

        if (request is null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password) ||
            request.Username.Length > limits.MaxUsernameCharacters || request.Password.Length > limits.MaxPasswordCharacters ||
            request.Username.Any(char.IsControl))
            return Invalid();

        try
        {
            _ = StrictUtf8.GetByteCount(request.Username);
            if (StrictUtf8.GetByteCount(request.Password) > limits.MaxPasswordUtf8Bytes) return Invalid();
        }
        catch (EncoderFallbackException) { return Invalid(); }

        // Password whitespace/normalization rules must come from the reviewed legacy verifier, not invented here.
        return null;
    }

    private static ApiError Invalid() => new("AUTH_INVALID_REQUEST", "ข้อมูลเข้าสู่ระบบไม่ถูกต้อง");
}
