using System.Net.Http.Headers;
using System.Text.Json;
using DevApi.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public sealed class RemoteTaskAgentOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 15;
}

// Messages are safe for Discord and persisted run history; never return response bodies or exception details.
public sealed record RemoteTaskAgentResult(bool Succeeded, int? StatusCode, string Message);

public sealed class RemoteTaskAgentClient(HttpClient http, IOptions<RemoteTaskAgentOptions> options)
{
    public async Task<RemoteTaskAgentResult> RunAsync(string alias, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!BotCommandRules.IsRemoteTaskAlias(alias))
            return new(false, null, "The remote task alias is invalid. Ask an administrator to check this command.");
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https") || baseUri.UserInfo.Length > 0
            || baseUri.Query.Length > 0 || baseUri.Fragment.Length > 0
            || string.IsNullOrWhiteSpace(settings.ApiKey) || settings.ApiKey.Any(char.IsWhiteSpace)
            || settings.TimeoutSeconds is < 1 or > 60)
            return new(false, null, "TaskAgent is not configured correctly. Ask an administrator to check the bot configuration.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/api/tasks/" + Uri.EscapeDataString(alias) + "/run"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                return new(false, status, status switch
                {
                    401 or 403 => "TaskAgent rejected the bot's credentials. Ask an administrator to check the configuration.",
                    404 => "TaskAgent does not recognize this task alias. Ask an administrator to check the command.",
                    >= 300 and < 400 => "TaskAgent returned a redirect, which the bot will not follow.",
                    _ => "TaskAgent could not confirm the task start. Check the game server before retrying.",
                });

            var message = "TaskAgent accepted the task start request. Task completion is not confirmed.";
            // Acknowledgement JSON is optional. Read a bounded body, and only retain the expected protocol message.
            // Unknown messages could contain secrets or machine paths and must not reach history or Discord.
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var bytes = new byte[4097];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
                    if (read == 0) break;
                    count += read;
                }
                if (count is > 0 and <= 4096)
                {
                    using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
                    if (json.RootElement.ValueKind == JsonValueKind.Object
                        && json.RootElement.TryGetProperty("message", out var property)
                        && property.ValueKind == JsonValueKind.String
                        && property.GetString() == $"Task '{alias}' started.")
                        message = $"Task '{alias}' started. Task completion is not confirmed.";
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException or OperationCanceledException)
            {
                // A received 2xx already confirms acceptance, even if its optional body is unavailable.
            }
            return new(true, status, message);
        }
        catch (OperationCanceledException)
        {
            return new(false, null, "TaskAgent did not confirm the request before the timeout. Check the game server before retrying.");
        }
        catch (HttpRequestException)
        {
            return new(false, null, "Could not confirm the request with TaskAgent. Check the game server before retrying.");
        }
    }
}

public static class RemoteTaskAgentServices
{
    public static IServiceCollection AddRemoteTaskAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RemoteTaskAgentOptions>(configuration.GetSection("remoteTaskAgent"));
        services.AddHttpClient<RemoteTaskAgentClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RedactLoggedHeaders(["Authorization"]);
        services.AddSingleton<BotCommandRunStarter>();
        return services;
    }
}
