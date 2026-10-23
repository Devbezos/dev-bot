using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DevClient.Data;

namespace DevScrape;

/// <summary>The two /api/agent/tcg routes, signed in with the agent's bearer token (which is never printed).</summary>
public sealed class AgentApi : IDisposable
{
    private readonly HttpClient _http;

    public AgentApi(string apiBase, string token, HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<List<TcgSourceUrl>> GetSourceUrlsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/agent/tcg/state", cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        var state = await response.Content.ReadFromJsonAsync<State>(TcgScrapePayload.JsonOptions, cancellationToken);
        return state?.SourceUrls ?? [];
    }

    public async Task UploadAsync(TcgScrapePayload payload, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync("api/agent/tcg/results", payload, TcgScrapePayload.JsonOptions, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
    }

    // The API answers errors with { error = "..." }, a sentence written for people, so it is safe to show.
    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string? detail = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            detail = JsonDocument.Parse(body).RootElement.GetProperty("error").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        throw new InvalidOperationException($"The API answered HTTP {(int)response.StatusCode}" + (string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}"));
    }

    public void Dispose() => _http.Dispose();

    private sealed class State
    {
        public List<TcgSourceUrl> SourceUrls { get; set; } = [];
    }
}
