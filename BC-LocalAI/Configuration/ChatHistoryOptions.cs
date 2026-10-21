using System.Net;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace BC_LocalAI.Configuration;

/// <summary>
/// BE-007 persistent chat history (section "ChatHistory"). Off by default. When on, it needs Authentication:Enabled (the owner of
/// every conversation is the signed-in user) and its own runtime account with SELECT/INSERT/UPDATE on the two history tables only.
/// </summary>
public sealed class ChatHistoryOptions
{
    public const string SectionName = "ChatHistory";

    public bool Enabled { get; set; }

    // Secret-provider value, never in checked-in appsettings or logs. A separate account from BE-005's reader and writer.
    public string ConnectionString { get; set; } = "";

    public int CommandTimeoutSeconds { get; set; } = 5;

    /// <summary>Estimated tokens of earlier turns sent to the AI with a new question (newest turns first).</summary>
    public int ContextTokenBudget { get; set; } = 1500;

    /// <summary>
    /// Estimated tokens a new question may use. The model context is 4096 tokens and 1024 are kept for the answer, so history
    /// (<see cref="ContextTokenBudget"/>) + question + template (about 100) must stay under 3072; a longer prompt is silently truncated by Ollama.
    /// </summary>
    public int MaxQuestionTokens { get; set; } = 1200;

    /// <summary>Partial text of a streaming answer is saved at most this often (plus once at the end).</summary>
    public int StreamCheckpointSeconds { get; set; } = 2;

    public override string ToString() => "ChatHistoryOptions [REDACTED]";
}

public sealed class ChatHistoryOptionsValidator(IHostEnvironment environment, IOptions<AuthenticationOptions> authentication)
    : IValidateOptions<ChatHistoryOptions>
{
    public ValidateOptionsResult Validate(string? name, ChatHistoryOptions options)
    {
        if (options.CommandTimeoutSeconds is < 1 or > 30 || options.ContextTokenBudget is < 0 or > 8000
            || options.StreamCheckpointSeconds is < 1 or > 60 || options.MaxQuestionTokens is < 1 or > 8000
            || options.ContextTokenBudget + options.MaxQuestionTokens > 2900)
            return ValidateOptionsResult.Fail("ChatHistory: CommandTimeoutSeconds 1-30, ContextTokenBudget 0-8000, StreamCheckpointSeconds 1-60, MaxQuestionTokens 1-8000, and ContextTokenBudget + MaxQuestionTokens at most 2900 (4096 context - 1024 answer - template).");
        if (!options.Enabled) return ValidateOptionsResult.Success;

        var auth = authentication.Value;
        if (!auth.Enabled)
            return ValidateOptionsResult.Fail("ChatHistory:Enabled needs Authentication:Enabled: every conversation belongs to a signed-in user.");

        // Deliberately generic: parser errors may contain connection-string values.
        try
        {
            var history = new MySqlConnectionStringBuilder(options.ConnectionString);
            var others = new[] { auth.ReaderConnectionString, auth.WriterConnectionString }
                .Where(value => !string.IsNullOrEmpty(value)).Select(value => new MySqlConnectionStringBuilder(value)).ToArray();
            var local = auth.AllowLocalDevelopmentDatabase && environment.IsDevelopment() &&
                IPAddress.TryParse(history.Server, out var ip) && IPAddress.IsLoopback(ip);
            if (string.IsNullOrWhiteSpace(history.UserID) || string.IsNullOrWhiteSpace(history.Password) || string.IsNullOrWhiteSpace(history.Database)
                || history.UserID.Contains("admin", StringComparison.OrdinalIgnoreCase) || history.UserID.Equals("root", StringComparison.OrdinalIgnoreCase)
                || others.Any(other => other.UserID.Equals(history.UserID, StringComparison.OrdinalIgnoreCase) || other.Password == history.Password)
                || history.ConnectionTimeout is < 1 or > 15 || history.PersistSecurityInfo || history.AllowLoadLocalInfile
                || history.AllowPublicKeyRetrieval || history.MaximumPoolSize is < 1 or > 32 || history.UseAffectedRows
                || (!local && history.SslMode != MySqlSslMode.VerifyFull)
                || (local && history.SslMode is not (MySqlSslMode.Required or MySqlSslMode.VerifyFull))
                || (auth.AllowLocalDevelopmentDatabase && !local))
                return Invalid();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            return Invalid();
        }

        return ValidateOptionsResult.Success;
    }

    private static ValidateOptionsResult Invalid() =>
        ValidateOptionsResult.Fail("ChatHistory configuration is invalid; inspect private settings without logging credentials.");
}
