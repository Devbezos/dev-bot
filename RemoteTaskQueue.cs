using DevApi.Data;
using Microsoft.Extensions.DependencyInjection;

// Messages are safe for Discord and persisted run history: the agent's output stays on the Game servers page.
public sealed record RemoteTaskResult(bool Succeeded, int? ExitCode, string Message);

/// <summary>
/// Runs a Remote TaskAgent command on a home server. The bot never connects to the home network: it queues the alias in
/// <c>game_agent_commands</c>, the home agent picks it up over its outbound long-poll to the API and reports back, and
/// this waits up to <see cref="DefaultWait"/> for that result.
/// </summary>
public sealed class RemoteTaskQueue(IGameAgentRepository agents, TimeSpan? wait = null, TimeSpan? pollInterval = null)
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(90);
    private readonly TimeSpan _wait = wait ?? DefaultWait;
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(2);

    public async Task<RemoteTaskResult> RunAsync(string alias, ulong requestedBy, int runId, CancellationToken cancellationToken = default)
    {
        if (!BotCommandRules.IsRemoteTaskAlias(alias))
            return new(false, null, "The remote task alias is invalid. Ask an administrator to check this command.");

        var now = DateTime.UtcNow;
        var (agent, error) = GameAgentRules.Pick(agents.GetAll(), alias, now);
        if (agent is null)
            return new(false, null, error!);

        var id = agents.Queue(agent.Id, alias, requestedBy.ToString(), runId, now);
        var deadline = now + _wait;
        while (true)
        {
            await Task.Delay(_poll, cancellationToken);
            // Nothing else expires a command when the agent is offline, so the bot does it while it waits.
            agents.ExpireStale(DateTime.UtcNow);
            var command = agents.GetCommand(id);
            var exit = command?.ExitCode is { } code ? $" (exit code {code})" : "";
            switch (command?.Status)
            {
                case null:
                    return new(false, null, "The request disappeared before the agent answered. Check the game server before retrying.");
                case GameAgentRules.Succeeded:
                    return new(true, command.ExitCode, $"Done on {agent.Name}{exit}.");
                case GameAgentRules.Failed:
                    return new(false, command.ExitCode, $"{agent.Name} reported a failure{exit}. Its output is on the Game servers page.");
                case GameAgentRules.Expired:
                    return new(false, null, $"The agent didn't respond: {agent.Name} didn't pick it up within a minute. Check that it's running.");
            }
            if (DateTime.UtcNow >= deadline)
                return new(false, null, command.Status == GameAgentRules.Claimed
                    ? $"The agent didn't respond in time: {agent.Name} started it but hasn't reported back. Check the game server before retrying."
                    : $"The agent didn't respond: {agent.Name} hasn't picked it up. Check that it's running.");
        }
    }
}

public static class RemoteTaskServices
{
    public static IServiceCollection AddRemoteTasks(this IServiceCollection services)
    {
        services.AddSingleton(sp => new RemoteTaskQueue(sp.GetRequiredService<IGameAgentRepository>()));
        services.AddSingleton<BotCommandRunStarter>();
        return services;
    }
}
