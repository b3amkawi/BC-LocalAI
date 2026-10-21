using BC_LocalAI.Services.ChatHistory;

namespace BC_LocalAI.Tests.ChatHistory;

/// <summary>Runs the contract against the in-memory reference implementation.</summary>
public sealed class InMemoryChatHistoryRepositoryTests : ChatHistoryRepositoryContractTests
{
    protected override IChatHistoryRepository CreateRepository(Func<DateTimeOffset> clock) => new InMemoryChatHistoryRepository(clock);
}
