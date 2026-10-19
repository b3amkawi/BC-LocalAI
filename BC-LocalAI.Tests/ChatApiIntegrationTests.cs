using System.Net;
using System.Net.Http.Json;
using System.Text;
using BC_LocalAI.Models;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BC_LocalAI.Tests;

public sealed class ChatApiIntegrationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Post_ValidMessage_Returns200WithContract()
    {
        var response = await _client.PostAsJsonAsync("/api/chat", new { message = "สวัสดี AI" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.Equal(new ChatResponse("Mock AI response", "mock"), body);
    }

    [Theory]
    [InlineData("""{"message":""}""")]
    [InlineData("""{"message":"   "}""")]
    [InlineData("{}")]
    [InlineData("{bad json")]
    [InlineData("""{"message":123}""")]
    public async Task Post_InvalidPayload_Returns400WithContract(string json)
    {
        var response = await _client.PostAsync(
            "/api/chat",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Equal("INVALID_MESSAGE", body!.Error.Code);
        Assert.Equal("Message is required", body.Error.Message);
    }
}
