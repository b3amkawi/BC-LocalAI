using BC_LocalAI.Services.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Security;

/// <summary>
/// Registration of BE-006 on top of BE-005: BE-005 registers the cookie scheme, login, antiforgery and rate limits;
/// BE-006 replaces the cookie's session handling with server-side sessions checked against the account on every request,
/// and adds roles and policies. Call after <see cref="AuthenticationRegistration.AddLocalAuthentication"/>.
/// </summary>
public static class BcAuthentication
{
    /// <summary>The single authentication scheme of the application (registered by BE-005).</summary>
    public const string Scheme = AuthenticationRegistration.Scheme;

    /// <summary>
    /// __Host- prefix: the browser only accepts it with Secure, Path=/ and no Domain, so a sub-domain cannot set or
    /// overwrite it.
    /// </summary>
    public const string CookieName = "__Host-BC.LocalAI";

    public static IServiceCollection AddBcLocalAiSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<BcAuthenticationOptions>, BcAuthenticationOptionsValidator>();
        services.AddOptions<BcAuthenticationOptions>()
            .Bind(configuration.GetSection(BcAuthenticationOptions.SectionName))
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISessionStore, InMemorySessionStore>();
        services.TryAddScoped<IUserSecurityStateSource, MySqlUserSecurityStateSource>();
        services.AddScoped<SessionService>();
        services.AddScoped<BcCookieEvents>();

        // PostConfigure: these settings win over BE-005's cookie configuration, so there is one cookie and one session model.
        services.AddOptions<CookieAuthenticationOptions>(Scheme)
            .PostConfigure<IOptions<BcAuthenticationOptions>, TimeProvider>((cookie, settings, clock) =>
            {
                cookie.Cookie.Name = CookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.Path = "/";
                cookie.Cookie.Domain = null;
                cookie.Cookie.IsEssential = true;
                cookie.SlidingExpiration = false; // idle and absolute expiry are enforced by the server-side session
                cookie.ExpireTimeSpan = settings.Value.AbsoluteLifetime;
                cookie.TimeProvider = clock;
                cookie.EventsType = typeof(BcCookieEvents); // replaces any inline events: every request is checked here
            });

        services.AddSingleton<IAuthorizationHandler, ChatUserHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(BcPolicies.ChatUser, policy => policy.AddRequirements(new ChatUserRequirement()))
            .AddPolicy(BcPolicies.Admin, policy => policy
                .AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser()
                .RequireRole(BcRoles.Admin));

        return services;
    }

    /// <summary>Adds the same-site guard and authentication. Call after UseRouting and before UseAuthorization.</summary>
    public static WebApplication UseBcLocalAiSecurity(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<BcAuthenticationOptions>>().Value;
        var registered = app.Services.GetRequiredService<IServiceProviderIsService>();
        if (settings.Enabled && !registered.IsService(typeof(IUserSecurityStateSource)))
        {
            // Fail at startup instead of refusing every request later.
            throw new InvalidOperationException(
                "Authentication:Enabled is true but no IUserSecurityStateSource is registered.");
        }

        app.UseMiddleware<SameSiteRequestGuard>();
        app.UseAuthentication();
        return app;
    }
}

/// <summary>Requirement behind <see cref="BcPolicies.ChatUser"/>.</summary>
public sealed class ChatUserRequirement : IAuthorizationRequirement;

/// <summary>
/// Open to everyone while Authentication:Enabled is false (the current Chat page has no login yet);
/// otherwise a signed-in User or Admin only.
/// </summary>
public sealed class ChatUserHandler(IOptionsMonitor<BcAuthenticationOptions> options) : AuthorizationHandler<ChatUserRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ChatUserRequirement requirement)
    {
        if (!options.CurrentValue.Enabled
            || (context.User.Identity?.IsAuthenticated == true
                && (context.User.IsInRole(BcRoles.User) || context.User.IsInRole(BcRoles.Admin))))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
