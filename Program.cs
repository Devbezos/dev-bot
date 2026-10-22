using DevClient;
using DevClient.Data;
using DevClient.Logging;
using DevApi.Infrastructure;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

AppSettings.Initialize();

var logTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";
var logConfig = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .MinimumLevel.Information()
    .MinimumLevel.Override("BotService", LogEventLevel.Information)
    .MinimumLevel.Override("DiscordClient", LogEventLevel.Information)
    .MinimumLevel.Override("Discord", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.Extensions.Hosting", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate: logTemplate);
// Railway keeps the console; log files are for local runs.
if (DevLogging.FileLoggingEnabled(AppSettings.LogFiles))
    logConfig.WriteTo.File("logs/bot-.log", rollingInterval: RollingInterval.Day, outputTemplate: logTemplate);
var alerts = DiscordAlertSink.Create(DiscordAlertSink.WebhookUrl(AppSettings.AlertsDiscordWebhookUrl), "dev-bot");
if (alerts is not null)
    logConfig.WriteTo.Sink(alerts, LogEventLevel.Error);
Log.Logger = logConfig.CreateLogger();

var host = Host.CreateDefaultBuilder(args)
    .UseSerilog()
    .ConfigureServices((context, services) =>
    {
        var connStr = AppSettings.MySql.ConnectionString;
        services.AddApiRepositories(connStr);
        services.AddDevClientClients();
        services.AddRemoteTasks();

        services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged
                | GatewayIntents.MessageContent
                | GatewayIntents.Guilds
                | GatewayIntents.GuildMembers,
            AlwaysDownloadUsers = false
        }));

        services.AddSingleton<BotService>();
        services.AddHostedService(sp => sp.GetRequiredService<BotService>());
        services.AddSingleton<SchedulerTickJob>();
        services.AddHostedService<QuartzSchedulerHostedService>();
    })
    .Build();

// A crash, a failed sign-in or a gateway close Discord won't let us reconnect from ends the process with exit code 1,
// so Railway's ON_FAILURE restart policy restarts it (a clean stop exits 0 and stays stopped).
var fatal = false;
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
host.Services.GetRequiredService<DiscordSocketClient>().Disconnected += ex =>
{
    if (FatalGateway.IsFatal(ex))
    {
        fatal = true;
        Log.Fatal("Discord refused the bot ({Reason}); stopping so it restarts",
            ex is WebSocketClosedException closed ? $"gateway close code {closed.CloseCode}" : "401 Unauthorized, check discord:token");
        lifetime.StopApplication();
    }
    return Task.CompletedTask;
};

try
{
    await host.StartAsync();
    alerts?.Notify("started");
    await host.WaitForShutdownAsync();
    if (host.Services.GetRequiredService<BotService>().ExecuteTask is { IsFaulted: true } execute)
    {
        fatal = true;
        Log.Fatal(execute.Exception?.GetBaseException(), "The bot stopped after a fatal error");
    }
}
catch (Exception ex)
{
    fatal = true;
    Log.Fatal(ex, "The bot failed to start");
}
finally
{
    alerts?.Notify(fatal ? "stopped after a fatal error; Railway will restart it" : "stopped");
    host.Dispose();
    // Sends any queued Discord alerts before the process ends.
    await Log.CloseAndFlushAsync();
}

return fatal ? 1 : 0;

/// <summary>
/// Disconnects that reconnecting can't fix: a rejected token (Discord.Net otherwise retries a 401 forever) or a gateway
/// close for bad intents, shard or API version.
/// </summary>
public static class FatalGateway
{
    public static readonly int[] CloseCodes = [4004, 4010, 4011, 4012, 4013, 4014];

    public static bool IsFatal(Exception? ex) =>
        ex is WebSocketClosedException closed && CloseCodes.Contains(closed.CloseCode)
        || ex is HttpException { HttpCode: System.Net.HttpStatusCode.Unauthorized };
}
