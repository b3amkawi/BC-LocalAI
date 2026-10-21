using BC_LocalAI.Configuration;
using BC_LocalAI.Middleware;
using BC_LocalAI.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using BC_LocalAI.Services;
using BC_LocalAI.Security;
using BC_LocalAI.Services.Authentication;
using BC_LocalAI.Services.ChatAdmission;
using BC_LocalAI.Services.ChatHistory;

var builder = WebApplication.CreateBuilder(args);

// MapStaticAssets serves pre-compressed copies (chat.css.gz) to browsers. When the app runs from build output (dotnet run)
// those copies live under obj/ and are found only through the static web assets manifest, which ASP.NET Core loads in
// Development only; in any other environment the CSS and JavaScript then arrived empty. Loading the manifest in every
// environment fixes that; a published app has no such manifest and serves its own pre-compressed files, so it is unaffected.
builder.WebHost.UseStaticWebAssets();

// Add services to the container.
builder.Services.AddControllersWithViews()
    .ConfigureApiBehaviorOptions(options =>
    {
        // Malformed JSON / wrong types must follow the same error contract.
        options.InvalidModelStateResponseFactory = context => new BadRequestObjectResult(
            context.HttpContext.Request.Path.StartsWithSegments(ChatHistoryRegistration.RoutePrefix)
                // BE-007: history requests have their own error code; the chat contract below is unchanged.
                ? new ApiErrorResponse(new ApiError("INVALID_REQUEST", "คำขอไม่ถูกต้อง"))
                : new ApiErrorResponse(new ApiError("INVALID_MESSAGE", "Message is required")));
    });
// BE-005: login against MySQL, antiforgery, rate limits, the authentication cookie scheme.
builder.Services.AddLocalAuthentication(builder.Configuration);
// BE-006: server-side sessions checked on every request, roles and policies, on BE-005's cookie scheme
// (chat APIs open while Authentication:Enabled is false).
builder.Services.AddBcLocalAiSecurity(builder.Configuration);
// BE-007: persistent chat history (off by default; needs authentication and its own database account).
builder.Services.AddChatHistory(builder.Configuration);
// BE-008: bounded queue and per-user quota in front of Ollama, shared by /api/chat and /api/chat/stream.
builder.Services.AddChatAdmission(builder.Configuration);
builder.Services.AddSingleton<IValidateOptions<OllamaOptions>, OllamaOptionsValidator>();
builder.Services.AddOptions<OllamaOptions>()
    .Bind(builder.Configuration.GetSection(OllamaOptions.SectionName))
    .ValidateOnStart();

// Typed HttpClient: /api/chat is served by Ollama. There is intentionally no silent
// fallback to MockAIService; automated tests replace IAIService explicitly.
builder.Services.AddHttpClient<IAIService, OllamaAIService>((serviceProvider, client) =>
{
    var ollama = serviceProvider.GetRequiredService<IOptions<OllamaOptions>>().Value;
    // Built without any user info, query or fragment (the validator already refuses them; this keeps them out of the client).
    client.BaseAddress = OllamaOptions.BuildBaseAddress(ollama.BaseUrl);
    // The deadline is enforced by OllamaAIService (Ollama:TimeoutSeconds, validated 1-600) so that a timeout and a client
    // cancellation can be told apart and the body read is covered too; HttpClient's own 100 s default must not cut in first.
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddHttpClient<IStreamingAIService, OllamaStreamingService>((serviceProvider, client) =>
{
    var ollama = serviceProvider.GetRequiredService<IOptions<OllamaOptions>>().Value;
    client.BaseAddress = OllamaOptions.BuildBaseAddress(ollama.BaseUrl);
    client.Timeout = Timeout.InfiniteTimeSpan;
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // Never drain a cancelled stream for the default two seconds; close it so Ollama stops generating immediately.
    MaxResponseDrainSize = 0,
    AllowAutoRedirect = false,
    UseProxy = false
});

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    // The HTML error page is for the MVC site only. /api/* errors must stay JSON,
    // so they are left to ExceptionHandlingMiddleware (INTERNAL_ERROR contract).
    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/api"),
        branch => branch.UseExceptionHandler("/Home/Error"));
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// Auth rejects HTTP itself instead of redirecting a submitted password. Other routes retain existing behaviour.
app.UseFrontendApiHttpsGuard();
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api/auth"), branch => branch.UseHttpsRedirection());
app.UseRouting();

// Same-site guard for /api and authentication (BE-006).
// BE-007: history routes do not exist (404) while ChatHistory:Enabled is false, before authentication can answer 401.
app.UseChatHistoryGate();
app.UseBcLocalAiSecurity();
// Antiforgery must see the authenticated principal, not an anonymous pre-authentication context.
app.UseMiddleware<AuthenticationBoundaryMiddleware>();
app.UseFrontendApiIdentityBoundary();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllers();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

public partial class Program;
