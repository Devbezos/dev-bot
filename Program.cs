using DevClient;
using DevClient.Data;
using DevApi.Infrastructure;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

AppSettings.Initialize();

var logTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";
Log.Logger = new LoggerConfiguration()
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
    .WriteTo.Console(outputTemplate: logTemplate)
    .WriteTo.File("logs/bot-.log", rollingInterval: RollingInterval.Day, outputTemplate: logTemplate)
    .CreateLogger();

var host = Host.CreateDefaultBuilder(args)
    .UseSerilog()
    .ConfigureServices((_, services) =>
    {
        var connStr = AppSettings.MySql.ConnectionString;
        services.AddApiRepositories(connStr);
        services.AddDevClientClients();

        services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged
                | GatewayIntents.MessageContent
                | GatewayIntents.Guilds
                | GatewayIntents.GuildMembers,
            AlwaysDownloadUsers = true
        }));

        services.AddSingleton<BotService>();
        services.AddHostedService(sp => sp.GetRequiredService<BotService>());
        services.AddSingleton<SchedulerTickJob>();
        services.AddHostedService<QuartzSchedulerHostedService>();
    })
    .Build();

await host.RunAsync();







