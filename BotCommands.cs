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
        var start = _botCommandRunStarter.TryStart(commandId, message.Author.Id, DateTime.UtcNow);
        if (start is null) return;
        var command = start.Command;
        if (start.Cooldown is { } wait)
        {
            var by = command.LastRunBy is { } userId ? $" by <@{userId}>" : "";
            await message.Channel.SendMessageAsync(
                $"**{command.Name}** was requested{by} <t:{new DateTimeOffset(command.LastRunUtc!.Value).ToUnixTimeSeconds()}:R>. " +
                $"Try again in {Math.Ceiling(wait.TotalMinutes)} min.",
                messageReference: reference, allowedMentions: AllowedMentions.None);
            return;
        }

        var runId = start.RunId!.Value;
        LogInfo($"Running bot command '{command.Name}' ({command.Action}) for {message.Author.Username} ({message.Author.Id})");
        var reply = await message.Channel.SendMessageAsync($"Running **{command.Name}**...", messageReference: reference,
            allowedMentions: AllowedMentions.None);
        _ = Task.Run(() => command.Action switch
        {
            "task" => RunTaskCommand(command, runId, reply),
            "remote-task" => RunRemoteTaskCommand(command, runId, reply),
            "script" => RunScriptCommand(command, runId, reply),
            _ => Task.CompletedTask,
        });
    }

    private async Task RunRemoteTaskCommand(BotCommand command, int runId, IUserMessage reply)
    {
        RemoteTaskAgentResult result;
        try
        {
            result = AppSettings.DryRun
                ? new(true, null, "[DRY RUN] Remote task not requested.")
                : await _remoteTaskAgentClient.RunAsync(command.Target);
        }
        catch (Exception ex)
        {
            // Exception messages can include URLs and credentials. Retain only the exception type.
            LogWarn($"Remote task command {command.Id} failed ({ex.GetType().Name}).");
            result = new(false, null, "Could not confirm the task request. Check the game server before retrying.");
        }

        _botCommandRepository.FinishRun(runId, result.Succeeded, null,
            result.StatusCode is { } status ? $"HTTP {status}: {result.Message}" : result.Message, DateTime.UtcNow);
        if (!result.Succeeded)
            LogWarn($"Remote task command {command.Id} failed (HTTP {result.StatusCode?.ToString() ?? "unavailable"}).");
        try
        {
            await reply.ModifyAsync(m =>
            {
                m.AllowedMentions = AllowedMentions.None;
                m.Content = result.Succeeded
                    ? AppSettings.DryRun ? $"**{command.Name}** finished (dry run; no request sent)."
                        : $"✅ {command.Name} was requested on the game server."
                    : $"Couldn't request **{command.Name}**. {result.Message}";
            });
        }
        catch (Exception ex)
        {
            // A Discord edit failure must not change an accepted request into a failed run.
            LogWarn($"Could not update remote task reply for command {command.Id} ({ex.GetType().Name}).");
        }
    }

    // schtasks only starts the task, so the bot then watches its status until it stops running and reports
    // the task's result: "still running" after a short wait, edited again when it finishes.
    private async Task RunTaskCommand(BotCommand command, int runId, IUserMessage reply)
    {
        try
        {
            if (AppSettings.DryRun)
            {
                _botCommandRepository.FinishRun(runId, true, 0, "[DRY RUN] task not run", DateTime.UtcNow);
                await reply.ModifyAsync(m => m.Content = $"✅ **{command.Name}** finished (dry run).");
                return;
            }

            var before = await ScheduledTaskRunner.GetStatusAsync(command.Target);
            var (ok, output) = await ScheduledTaskRunner.RunAsync(command.Target);
            if (!ok)
            {
                LogWarn($"Bot command '{command.Name}' failed to start: {output}");
                _botCommandRepository.FinishRun(runId, false, null, output, DateTime.UtcNow);
                await reply.ModifyAsync(m => m.Content = $"❌ Couldn't run **{command.Name}**: {Truncate(output, 300)}");
                return;
            }

            var (finished, status) = await WatchTask(command, reply, before?.LastRunTime);
            if (!finished || status is null)
            {
                _botCommandRepository.FinishRun(runId, true, null, output, DateTime.UtcNow);
                await reply.ModifyAsync(m => m.Content = status is null
                    ? $"✅ **{command.Name}** started, but I couldn't read the task's status to see when it finishes."
                    : $"⏳ **{command.Name}** is still running after {ScriptMaxWait.TotalHours:0} hours; I've stopped watching it.");
                return;
            }

            var result = status.LastResult;
            var succeeded = result is 0 or null;
            var code = result is { } r ? $"0x{r:X}" : "unknown";
            _botCommandRepository.FinishRun(runId, succeeded, result, $"Last result {code}", DateTime.UtcNow);
            if (!succeeded)
                LogWarn($"Bot command '{command.Name}' task finished with result {code}");
            await reply.ModifyAsync(m => m.Content = succeeded
                ? $"✅ **{command.Name}** finished."
                : $"❌ **{command.Name}** finished with an error (task result {code}).");
        }
        catch (Exception ex)
        {
            LogException(ex, $"botCommand={command.Name}");
            _botCommandRepository.FinishRun(runId, false, null, ex.Message, DateTime.UtcNow);
            try { await reply.ModifyAsync(m => m.Content = $"❌ Couldn't run **{command.Name}**: {Truncate(ex.Message, 300)}"); }
            catch (Exception replyEx) { LogWarn($"Could not report bot command failure: {replyEx.Message}"); }
        }
    }

    // Polls until the run that was just started has ended. A run counts as started once the task shows as running
    // or its last-run time moves on (a quick task can finish between two polls). Returns the final status, or
    // finished = false when it's still running at the deadline; a null status means it couldn't be read.
    private async Task<(bool Finished, ScheduledTaskStatus? Status)> WatchTask(BotCommand command, IUserMessage reply, string? lastRunBefore)
    {
        var started = DateTime.UtcNow;
        var toldStillRunning = false;
        ScheduledTaskStatus? status = null;
        var sawStart = false;
        while (DateTime.UtcNow - started < ScriptMaxWait)
        {
            var elapsed = DateTime.UtcNow - started;
            await Task.Delay(elapsed < TimeSpan.FromMinutes(2) ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(30));
            status = await ScheduledTaskRunner.GetStatusAsync(command.Target);
            if (status is null)
                return (false, null);

            sawStart |= status.IsRunning || status.LastRunTime != lastRunBefore;
            if (sawStart && !status.IsRunning)
                return (true, status);
            // Never seen running after a minute: it may have run and ended within the same second as last time.
            if (!sawStart && DateTime.UtcNow - started > TimeSpan.FromMinutes(1))
                return (true, status);

            if (!toldStillRunning && DateTime.UtcNow - started >= ScriptQuickWait)
            {
                toldStillRunning = true;
                await reply.ModifyAsync(m => m.Content = $"⏳ **{command.Name}** is still running. I'll update this message when it finishes.");
            }
        }
        return (false, status);
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
