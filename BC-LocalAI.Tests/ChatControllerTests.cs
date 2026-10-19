using BC_LocalAI.Controllers;
using BC_LocalAI.Models;
using BC_LocalAI.Services;
using Microsoft.AspNetCore.Mvc;

namespace BC_LocalAI.Tests;

public sealed class ChatControllerTests
{
    [Fact]
    public async Task PostAsync_WithValidMessage_ReturnsContractResponseAndCallsService()
    {
        var aiService = new RecordingAIService();
        var controller = new ChatController(aiService);
        var request = new ChatRequest { Message = "สวัสดี AI" };

        var result = await controller.PostAsync(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ChatResponse>(okResult.Value);
        Assert.Equal("Mock AI response", response.Response);
        Assert.Equal("mock", response.Model);
        Assert.Equal("สวัสดี AI", aiService.ReceivedMessage);
        Assert.Equal(1, aiService.CallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PostAsync_WithInvalidMessage_ReturnsContractError(string? message)
    {
        var aiService = new RecordingAIService();
        var controller = new ChatController(aiService);

        var result = await controller.PostAsync(
            new ChatRequest { Message = message },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var response = Assert.IsType<ApiErrorResponse>(badRequest.Value);
        Assert.Equal("INVALID_MESSAGE", response.Error.Code);
        Assert.Equal("Message is required", response.Error.Message);
        Assert.Equal(0, aiService.CallCount);
    }

    [Fact]
    public async Task PostAsync_WithNullRequest_ReturnsContractError()
    {
        var aiService = new RecordingAIService();
        var controller = new ChatController(aiService);

        var result = await controller.PostAsync(null, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var response = Assert.IsType<ApiErrorResponse>(badRequest.Value);
        Assert.Equal("INVALID_MESSAGE", response.Error.Code);
        Assert.Equal("Message is required", response.Error.Message);
        Assert.Equal(0, aiService.CallCount);
    }

    private sealed class RecordingAIService : IAIService
    {
        public int CallCount { get; private set; }
        public string? ReceivedMessage { get; private set; }

        public Task<ChatResponse> GenerateResponseAsync(
            string message,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ReceivedMessage = message;
            return Task.FromResult(new ChatResponse("Mock AI response", "mock"));
        }
    }
}
