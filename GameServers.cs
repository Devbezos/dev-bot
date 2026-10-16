using System.Text.RegularExpressions;
using DevClient.Data;
using DevApi.Data;
using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private static readonly TimeSpan ScheduledTaskSnapshotInterval = TimeSpan.FromMinutes(5);
    private DateTime _scheduledTaskSnapshotUtc = DateTime.MinValue;
    // One restart at a time, so two people asking at once can't both pass the cooldown check.
    private readonly SemaphoreSlim _gameServerLock = new(1, 1);

    // "restart valheim" in a DM, or "@DevBot restart valheim" in a server channel.
    private async Task<bool> TryHandleGameServerCommand(SocketMessage message)
    {
        var mentionsBot = message.MentionedUsers.Any(u => u.Id == _discordBotClient.CurrentUser?.Id);
        if (message.Channel is not IDMChannel && !mentionsBot)
            return false;

        var text = Regex.Replace(message.Content, @"<@!?\d+>", " ");
        if (!GameServerRules.TryParseRestartCommand(text, out var requested))
            return false;

        var reference = new MessageReference(message.Id);
        var allowed = _gameServerRepository.GetAll().Where(s => GameServerRules.CanRestart(s, message.Author.Id)).ToList();
        if (allowed.Count == 0)
        {
            LogInfo($"Game server restart '{requested}' refused for {message.Author.Username} ({message.Author.Id}): not on any restart list");
            await message.Channel.SendMessageAsync("You're not on the restart list for any game server.", messageReference: reference);
            return true;
        }

        if (GameServerRules.Find(allowed, requested) is not { } server)
        {
            var names = string.Join(", ", allowed.Select(s => $"**{s.Name}**"));
            await message.Channel.SendMessageAsync($"I don't know a server called \"{requested}\" that you can restart. You can restart: {names}.", messageReference: reference);
            return true;
        }

        await RestartGameServer(server.Id, message, reference);
        return true;
    }

    private async Task RestartGameServer(int serverId, SocketMessage message, MessageReference reference)
    {
        await _gameServerLock.WaitAsync();
        try
        {
            var nowUtc = DateTime.UtcNow;
            var server = _gameServerRepository.Get(serverId);
            if (server is null || !GameServerRules.CanRestart(server, message.Author.Id))
                return;

            if (GameServerRules.CooldownLeft(server, nowUtc) is { } wait)
            {
                var by = server.LastRestartBy is { } userId ? $" by <@{userId}>" : "";
                await message.Channel.SendMessageAsync(
                    $"**{server.Name}** was restarted{by} <t:{new DateTimeOffset(server.LastRestartUtc!.Value).ToUnixTimeSeconds()}:R>. " +
                    $"Try again in {Math.Ceiling(wait.TotalMinutes)} min.",
                    messageReference: reference, allowedMentions: AllowedMentions.None);
                return;
            }

            LogInfo($"Restarting game server '{server.Name}' (task {server.TaskName}) for {message.Author.Username} ({message.Author.Id})");
            var reply = await message.Channel.SendMessageAsync($"🔄 Restarting **{server.Name}**…", messageReference: reference);
            var (ok, output) = AppSettings.DryRun
                ? (true, "[DRY RUN] task not run")
                : await ScheduledTaskRunner.RunAsync(server.TaskName);
            _gameServerRepository.LogRestart(server, message.Author.Id, nowUtc, ok, output);
            if (!ok)
                LogWarn($"Game server '{server.Name}' restart failed: {output}");

            await reply.ModifyAsync(m => m.Content = ok
                ? $"✅ **{server.Name}** restart started. Give it a few minutes to come back up."
                : $"❌ Couldn't restart **{server.Name}**: {Truncate(output, 300)}");
        }
        finally
        {
            _gameServerLock.Release();
        }
    }

    // Publishes this machine's Task Scheduler tasks so the Game Servers tab can offer them in a dropdown,
    // even when the API runs on a different machine.
    private async Task RefreshScheduledTaskSnapshot()
    {
        var nowUtc = DateTime.UtcNow;
        if (!OperatingSystem.IsWindows() || nowUtc - _scheduledTaskSnapshotUtc < ScheduledTaskSnapshotInterval)
            return;
        _scheduledTaskSnapshotUtc = nowUtc;
        _gameServerRepository.ReplaceTaskSnapshot(await ScheduledTaskRunner.ListAsync(), nowUtc);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
