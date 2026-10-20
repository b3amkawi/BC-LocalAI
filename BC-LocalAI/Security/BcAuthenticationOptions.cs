using Microsoft.Extensions.Options;

namespace BC_LocalAI.Security;

/// <summary>
/// BE-006 session and authorization settings (section "Authentication" in appsettings.json).
/// Values decided in SEC-001: idle 30 minutes, absolute 8 hours.
/// </summary>
public sealed class BcAuthenticationOptions
{
    public const string SectionName = "Authentication";

    /// <summary>
    /// When false (default until BE-005 login and FE-003 login page exist) the chat APIs stay open as before.
    /// When true they require a signed-in User or Admin, and unsafe API requests must come from this site.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>A session unused for longer than this is refused.</summary>
    public int IdleTimeoutMinutes { get; set; } = 30;

    /// <summary>A session is refused this long after sign-in, however active it is. Renewal never extends it.</summary>
    public int AbsoluteLifetimeMinutes { get; set; } = 480;

    public TimeSpan IdleTimeout => TimeSpan.FromMinutes(IdleTimeoutMinutes);

    public TimeSpan AbsoluteLifetime => TimeSpan.FromMinutes(AbsoluteLifetimeMinutes);
}

public sealed class BcAuthenticationOptionsValidator : IValidateOptions<BcAuthenticationOptions>
{
    public const int MaxIdleTimeoutMinutes = 240;
    public const int MinAbsoluteLifetimeMinutes = 5;
    public const int MaxAbsoluteLifetimeMinutes = 1440;

    public ValidateOptionsResult Validate(string? name, BcAuthenticationOptions options)
    {
        var failures = new List<string>();

        if (options.IdleTimeoutMinutes is < 1 or > MaxIdleTimeoutMinutes)
        {
            failures.Add($"Authentication:IdleTimeoutMinutes must be between 1 and {MaxIdleTimeoutMinutes}.");
        }

        if (options.AbsoluteLifetimeMinutes is < MinAbsoluteLifetimeMinutes or > MaxAbsoluteLifetimeMinutes)
        {
            failures.Add($"Authentication:AbsoluteLifetimeMinutes must be between {MinAbsoluteLifetimeMinutes} and {MaxAbsoluteLifetimeMinutes}.");
        }

        if (options.IdleTimeoutMinutes > options.AbsoluteLifetimeMinutes)
        {
            failures.Add("Authentication:IdleTimeoutMinutes must not be longer than Authentication:AbsoluteLifetimeMinutes.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
