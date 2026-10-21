using BC_LocalAI.Configuration;
using Microsoft.Extensions.Options;

namespace BC_LocalAI.Services.ChatHistory;

/// <summary>
/// BE-007 wiring. Options are validated at startup in every case; while ChatHistory:Enabled is false (the default) the history
/// routes answer 404 before authentication (as if they did not exist) and a chat request naming a conversation is refused.
/// </summary>
public static class ChatHistoryRegistration
{
    public const string RoutePrefix = "/api/conversations";

    public static IServiceCollection AddChatHistory(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<ChatHistoryOptions>, ChatHistoryOptionsValidator>();
        services.AddOptions<ChatHistoryOptions>()
            .Bind(configuration.GetSection(ChatHistoryOptions.SectionName))
            .ValidateOnStart();
        services.AddScoped<IChatHistoryRepository, MySqlChatHistoryRepository>();
        services.AddSingleton<ConversationContextBuilder>();
        services.AddScoped<ChatTurnService>();
        return services;
    }

    /// <summary>Call before authentication: a disabled feature must not reveal itself through 401/403.</summary>
    public static IApplicationBuilder UseChatHistoryGate(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(RoutePrefix)
                && !context.RequestServices.GetRequiredService<IOptions<ChatHistoryOptions>>().Value.Enabled)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });
}
