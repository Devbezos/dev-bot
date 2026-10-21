using DevApi.Data;

public sealed record BotCommandStart(BotCommand Command, int? RunId, TimeSpan? Cooldown);

/// <summary>Rechecks authorization and serializes cooldown checks with persisted run starts.</summary>
public sealed class BotCommandRunStarter(IBotCommandRepository repository)
{
    private readonly object _gate = new();

    public BotCommandStart? TryStart(int commandId, ulong userId, DateTime nowUtc)
    {
        lock (_gate)
        {
            var command = repository.Get(commandId);
            if (command is null || !BotCommandRules.CanRun(command, userId)) return null;
            var cooldown = BotCommandRules.CooldownLeft(command, nowUtc);
            return cooldown is { } wait ? new(command, null, wait)
                : new(command, repository.StartRun(command, userId, nowUtc), null);
        }
    }
}
