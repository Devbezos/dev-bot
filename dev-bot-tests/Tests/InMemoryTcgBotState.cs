using DevClient.Data;

namespace dev_bot_tests.Tests;

/// <summary>A stand-in for the tcg_bot_state table; installs itself as the bot's TCG state store.</summary>
public sealed class InMemoryTcgBotState : ITcgBotStateRepository
{
    public Dictionary<string, string> Values { get; } = new();

    public InMemoryTcgBotState() => BotService.UseTcgBotState(this);

    public string? Get(string key) => Values.GetValueOrDefault(key);

    public void Set(string key, string value) => Values[key] = value;
}
