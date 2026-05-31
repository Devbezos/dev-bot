using dev_library;
using dev_library.Data;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

AppSettings.Initialize();

var logTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: logTemplate)
    .WriteTo.File("logs/bot-.log", rollingInterval: RollingInterval.Day, outputTemplate: logTemplate)
    .CreateLogger();

var host = Host.CreateDefaultBuilder(args)
    .UseSerilog()
    .ConfigureServices((_, services) =>
    {
        var connStr = AppSettings.MySql.ConnectionString;
        services.AddDevLibraryRepositories(connStr);
        services.AddDevLibraryClients();

        services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged
                | GatewayIntents.MessageContent
                | GatewayIntents.Guilds
                | GatewayIntents.GuildMembers,
            AlwaysDownloadUsers = true
        }));

        services.AddHostedService<BotService>();
    })
    .Build();

await host.RunAsync();

