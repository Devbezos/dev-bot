using System.Diagnostics;
using DevApi.Data;
using DevClient.Data;
using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private static readonly TimeSpan BotCommandSnapshotInterval = TimeSpan.FromMinutes(5);
    // How long to wait for a script before saying it's still running; the reply is edited again when it finishes.
    private static readonly TimeSpan ScriptQuickWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScriptMaxWait = TimeSpan.FromHours(12);
    private DateTime _botCommandSnapshotUtc = DateTime.MinValue;
    private string? _snapshotScriptsFolder;
    // Serializes the cooldown check and run start, so two people sending a keyword at once can't both pass.
    private readonly SemaphoreSlim _botCommandLock = new(1, 1);

    // A DM, or a message mentioning the bot, whose whole text is a command's keyword.
    private async Task<bool> TryHandleBotCommand(SocketMessage message)
    {
        var mentionsBot = message.MentionedUsers.Any(u => u.Id == _discordBotClient.CurrentUser?.Id);
        if (message.Channel is not IDMChannel && !mentionsBot)
            return false;

        var commands = _botCommandRepository.GetAll();
        if (BotCommandRules.Find(commands, message.Content) is not { } command)
            return false;

        var reference = new MessageReference(message.Id);
        if (!BotCommandRules.CanRun(command, message.Author.Id))
        {
            LogInfo($"Bot command '{command.Name}' refused for {message.Author.Username} ({message.Author.Id})");
            await message.Channel.SendMessageAsync($"You're not allowed to run **{command.Name}**.", messageReference: reference);
            return true;
        }

        await RunBotCommand(command.Id, message, reference);
        return true;
    }

    private async Task RunBotCommand(int commandId, SocketMessage message, MessageReference reference)
    {
        BotCommand? command;
        int runId;
        IUserMessage reply;
        await _botCommandLock.WaitAsync();
        try
        {
            var nowUtc = DateTime.UtcNow;
            command = _botCommandRepository.Get(commandId);
            if (command is null || !BotCommandRules.CanRun(command, message.Author.Id))
                return;

            if (BotCommandRules.CooldownLeft(command, nowUtc) is { } wait)
            {
                var by = command.LastRunBy is { } userId ? $" by <@{userId}>" : "";
                await message.Channel.SendMessageAsync(
                    $"**{command.Name}** was run{by} <t:{new DateTimeOffset(command.LastRunUtc!.Value).ToUnixTimeSeconds()}:R>. " +
                    $"Try again in {Math.Ceiling(wait.TotalMinutes)} min.",
                    messageReference: reference, allowedMentions: AllowedMentions.None);
                return;
            }

            LogInfo($"Running bot command '{command.Name}' ({command.Action} {command.Target}) for {message.Author.Username} ({message.Author.Id})");
            runId = _botCommandRepository.StartRun(command, message.Author.Id, nowUtc);
            reply = await message.Channel.SendMessageAsync($"▶️ Running **{command.Name}**…", messageReference: reference);
        }
        finally
        {
            _botCommandLock.Release();
        }

        if (command.Action == "task")
            await RunTaskCommand(command, runId, reply);
        else
            _ = Task.Run(() => RunScriptCommand(command, runId, reply));
    }

    private async Task RunTaskCommand(BotCommand command, int runId, IUserMessage reply)
    {
        var (ok, output) = AppSettings.DryRun ? (true, "[DRY RUN] task not run") : await ScheduledTaskRunner.RunAsync(command.Target);
        _botCommandRepository.FinishRun(runId, ok, null, output, DateTime.UtcNow);
        if (!ok)
            LogWarn($"Bot command '{command.Name}' failed: {output}");
        await reply.ModifyAsync(m => m.Content = ok
            ? $"✅ **{command.Name}** started."
            : $"❌ Couldn't run **{command.Name}**: {Truncate(output, 300)}");
    }

    // Scripts can take minutes: the reply says "still running" after a short wait and is edited again when it exits.
    private async Task RunScriptCommand(BotCommand command, int runId, IUserMessage reply)
    {
        var logPath = Path.Combine(ScriptRunner.LogFolder, $"run-{runId}.log");
        try
        {
            if (AppSettings.DryRun)
            {
                LogInfo($"[DRY RUN] Would run {command.Target} {command.Arguments}");
                _botCommandRepository.FinishRun(runId, true, 0, "[DRY RUN] script not run", DateTime.UtcNow);
                await reply.ModifyAsync(m => m.Content = $"✅ **{command.Name}** finished (dry run).");
                return;
            }

            using var process = ScriptRunner.Start(command.Target, command.Arguments, logPath);
            if (!await WaitForExit(process, ScriptQuickWait))
            {
                await reply.ModifyAsync(m => m.Content = $"⏳ **{command.Name}** is still running. I'll update this message when it finishes.");
                if (!await WaitForExit(process, ScriptMaxWait))
                {
                    _botCommandRepository.FinishRun(runId, true, null, ScriptRunner.ReadLogTail(logPath), DateTime.UtcNow);
                    await reply.ModifyAsync(m => m.Content = $"⏳ **{command.Name}** is still running after {ScriptMaxWait.TotalHours:0} hours; I've stopped watching it.");
                    return;
                }
            }

            var output = ScriptRunner.ReadLogTail(logPath);
            var ok = process.ExitCode == 0;
            _botCommandRepository.FinishRun(runId, ok, process.ExitCode, output, DateTime.UtcNow);
            if (!ok)
                LogWarn($"Bot command '{command.Name}' exited with {process.ExitCode}");
            var heading = ok ? $"✅ **{command.Name}** finished." : $"❌ **{command.Name}** failed (exit code {process.ExitCode}).";
            await reply.ModifyAsync(m => m.Content = output.Length == 0 ? heading : $"{heading}\n```\n{Truncate(output, 1500).Replace("```", "'''")}\n```");
        }
        catch (Exception ex)
        {
            LogException(ex, $"botCommand={command.Name}");
            _botCommandRepository.FinishRun(runId, false, null, ex.Message, DateTime.UtcNow);
            try { await reply.ModifyAsync(m => m.Content = $"❌ Couldn't run **{command.Name}**: {Truncate(ex.Message, 300)}"); }
            catch (Exception replyEx) { LogWarn($"Could not report bot command failure: {replyEx.Message}"); }
        }
    }

    private static async Task<bool> WaitForExit(Process process, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    /// <summary>The keyword commands this user may run, for the "commands" DM.</summary>
    private List<BotCommand> BotCommandsFor(ulong userId) =>
        _botCommandRepository.GetAll().Where(c => BotCommandRules.CanRun(c, userId)).ToList();

    // Publishes this machine's Task Scheduler tasks and the scripts in the configured folder, so the Commands tab
    // can offer them in dropdowns even when the API runs on a different machine. Rescans right away when the
    // folder setting changes.
    private async Task RefreshBotCommandSnapshots()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var nowUtc = DateTime.UtcNow;
        var folder = _botCommandRepository.GetSettings().ScriptsFolder;
        if (nowUtc - _botCommandSnapshotUtc < BotCommandSnapshotInterval && folder == _snapshotScriptsFolder)
            return;
        _botCommandSnapshotUtc = nowUtc;
        _snapshotScriptsFolder = folder;

        _botCommandRepository.ReplaceTaskSnapshot(await ScheduledTaskRunner.ListAsync(), nowUtc);
        _botCommandRepository.ReplaceScriptSnapshot(string.IsNullOrWhiteSpace(folder) ? [] : ScriptRunner.ListScripts(folder), nowUtc);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
