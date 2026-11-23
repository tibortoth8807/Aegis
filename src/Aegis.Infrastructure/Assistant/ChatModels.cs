using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Aegis.Application.Common;

namespace Aegis.Infrastructure.Assistant;

public sealed class AssistantOptions
{
    public const string Section = "Assistant";

    /// <summary>"builtin" (default), "ollama" or "openai" (any OpenAI-compatible endpoint, e.g. Azure OpenAI, LM Studio).</summary>
    public string Provider { get; set; } = "builtin";
    /// <summary>Ollama: http://localhost:11434 · OpenAI: https://api.openai.com</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "llama3.2";
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Ollama (https://ollama.com): free, runs on your own machine, no registration or key.
/// <c>ollama pull llama3.2</c>, then set Assistant:Provider=ollama.
/// </summary>
public sealed class OllamaChatModel(HttpClient http, AssistantOptions options) : IChatModel
{
    public string Name => $"ollama:{options.Model}";

    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatTurn> conversation, CancellationToken ct)
    {
        var body = new OllamaRequest(options.Model, Messages(systemPrompt, conversation), Stream: false);
        using var response = await http.PostAsJsonAsync("/api/chat", body, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<OllamaResponse>(ct);
        return result?.Message?.Content ?? throw new InvalidOperationException("Empty answer from Ollama.");
    }

    internal static List<Message> Messages(string system, IReadOnlyList<ChatTurn> conversation) =>
        [new("system", system), .. conversation.Select(t => new Message(t.Role == "assistant" ? "assistant" : "user", t.Text))];

    internal sealed record Message([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content);

    private sealed record OllamaRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] List<Message> Messages,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record OllamaResponse([property: JsonPropertyName("message")] Message? Message);
}

/// <summary>Any OpenAI-compatible chat completions endpoint (OpenAI, Azure OpenAI, LM Studio, vLLM...).</summary>
public sealed class OpenAiChatModel(HttpClient http, AssistantOptions options) : IChatModel
{
    public string Name => $"openai:{options.Model}";

    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatTurn> conversation, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new ChatRequest(options.Model, OllamaChatModel.Messages(systemPrompt, conversation), 0.2)),
        };
        if (!string.IsNullOrEmpty(options.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ChatResponse>(ct);
        return result?.Choices?.FirstOrDefault()?.Message?.Content ?? throw new InvalidOperationException("Empty answer from the model.");
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] List<OllamaChatModel.Message> Messages,
        [property: JsonPropertyName("temperature")] double Temperature);

    private sealed record Choice([property: JsonPropertyName("message")] OllamaChatModel.Message? Message);

    private sealed record ChatResponse([property: JsonPropertyName("choices")] List<Choice>? Choices);
}
