using BC_LocalAI.Configuration;
using BC_LocalAI.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Services.Authentication;

public static class AuthenticationRegistration
{
    public const string Scheme = "BC.LocalAI";

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
            // Expiry, renewal and per-request session checks are configured by BE-006 (BcAuthentication.AddBcLocalAiSecurity).
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => Error(context.Response, 401, "AUTH_REQUIRED", "กรุณาเข้าสู่ระบบ");
            options.Events.OnRedirectToAccessDenied = context => Error(context.Response, 403, "AUTH_FORBIDDEN", "ไม่มีสิทธิ์เข้าถึง");
        });
        // Sessions: BE-006 (BC_LocalAI.Security) replaced BE-005's basic in-memory ticket store with server-side sessions that
        // re-check the account (active, lockout, role, security stamp) on every request.
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
