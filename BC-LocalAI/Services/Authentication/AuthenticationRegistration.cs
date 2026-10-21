using BC_LocalAI.Configuration;
using BC_LocalAI.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Services.Authentication;

public static class AuthenticationRegistration
{
    public const string Scheme = "BC.LocalAI";
    public const string SessionClaim = "bc_session_key";

    public static IServiceCollection AddLocalAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<AuthenticationOptions>, AuthenticationOptionsValidator>();
        services.AddOptions<AuthenticationOptions>().Bind(configuration.GetSection("Authentication")).ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "__Host-BC.Csrf";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
            options.SuppressXFrameOptionsHeader = false;
        });
        services.AddAuthentication(Scheme).AddCookie(Scheme, options =>
        {
            options.Cookie.Name = "__Host-BC.LocalAI";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = BasicAuthenticationTicketStore.IdleLifetime;
            // The server store controls renewal and clamps it to absolute expiry; never rely on cookie-only sliding.
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => Error(context.Response, 401, "AUTH_REQUIRED", "กรุณาเข้าสู่ระบบ");
            options.Events.OnRedirectToAccessDenied = context => Error(context.Response, 403, "AUTH_FORBIDDEN", "ไม่มีสิทธิ์เข้าถึง");
        });
        services.AddSingleton<BasicAuthenticationTicketStore>();
        services.AddOptions<CookieAuthenticationOptions>(Scheme).Configure<BasicAuthenticationTicketStore, TimeProvider>((cookie, store, clock) =>
        {
            cookie.TimeProvider = clock;
            cookie.Events.OnValidatePrincipal = async context =>
            {
                var key = context.Principal?.FindFirst(SessionClaim)?.Value;
                var ticket = key is null ? null : await store.RetrieveAsync(key);
                if (ticket is null || ticket.Principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value !=
                    context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value)
                {
                    context.RejectPrincipal();
                    return;
                }
                if (clock.GetUtcNow() - ticket.Properties.IssuedUtc >= TimeSpan.FromMinutes(15))
                {
                    await store.RenewAsync(key!, ticket);
                    var renewed = await store.RetrieveAsync(key!);
                    if (renewed is null) { context.RejectPrincipal(); return; }
                    context.Properties.IssuedUtc = renewed.Properties.IssuedUtc;
                    context.Properties.ExpiresUtc = renewed.Properties.ExpiresUtc;
                    context.ShouldRenew = true;
                }
            };
        });
        services.AddSingleton<LoginAttemptLimiter>();
        services.AddSingleton<IAuthenticationPasswordVerifier, IdentityPasswordVerifier>();
        services.AddScoped<IAuthenticationRepository, MySqlAuthenticationRepository>();
        services.AddScoped<IAuthenticationAttemptStore, MySqlAuthenticationAttemptStore>();
        services.AddScoped<IAuthenticationBackend, MySqlAuthenticationBackend>();
        // Disabled-by-default gates all Auth routes before these services are used. Key directory must already have restricted ACL.
        services.AddDataProtection().SetApplicationName("BC-LocalAI.Authentication");
        services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
            .Configure<IOptions<AuthenticationOptions>>((keys, auth) =>
            {
                if (!auth.Value.Enabled) return;
                if (!OperatingSystem.IsWindows())
                    throw new InvalidOperationException("Configure an approved OS-protected key encryptor before enabling Authentication on this platform.");
                keys.XmlRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(
                    new DirectoryInfo(auth.Value.KeyDirectory), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
                if (OperatingSystem.IsWindows()) keys.XmlEncryptor = new Microsoft.AspNetCore.DataProtection.XmlEncryption.DpapiXmlEncryptor(
                    protectToLocalMachine: false, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
            });
        return services;
    }

    public static Task Error(HttpResponse response, int status, string code, string message)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(new ApiErrorResponse(new ApiError(code, message)));
    }
}
