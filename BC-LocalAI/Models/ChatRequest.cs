namespace BC_LocalAI.Models;

public sealed class ChatRequest
{
    public string? Message { get; init; }

    /// <summary>
    /// BE-007, optional: a conversation of the signed-in user to save this turn in and to take earlier turns from as context.
    /// Without it the chat works exactly as before (nothing is saved).
    /// </summary>
    public string? ConversationId { get; init; }
}
