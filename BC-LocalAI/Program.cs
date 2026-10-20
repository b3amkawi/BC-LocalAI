using BC_LocalAI.Configuration;
using BC_LocalAI.Middleware;
using BC_LocalAI.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using BC_LocalAI.Services;

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
        options.InvalidModelStateResponseFactory = _ => new BadRequestObjectResult(
            new ApiErrorResponse(new ApiError("INVALID_MESSAGE", "Message is required")));
    });
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

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllers();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

public partial class Program;
