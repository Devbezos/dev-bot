using DevApi.Data;
using Discord;
using Discord.WebSocket;

public partial class BotService
{
    private static readonly TimeSpan DiscordDirectoryInterval = TimeSpan.FromMinutes(10);
    private DateTime _nextDiscordDirectorySyncUtc = DateTime.UtcNow;

    // Every 10 minutes: saves the servers and channels the bot is in, and which channels each signed-in person's Discord
    // account can view, so the dashboard can offer them as dropdowns (and only the ones each person can see).
    private async Task ProcessDiscordDirectory()
    {
        var nowUtc = DateTime.UtcNow;
        if (nowUtc < _nextDiscordDirectorySyncUtc || _discordBotClient.Guilds.Count == 0)
            return;
        _nextDiscordDirectorySyncUtc = nowUtc + DiscordDirectoryInterval;

        var guilds = _discordBotClient.Guilds.ToList();
        var servers = guilds.Select(g => new DiscordServer(g.Id.ToString(), g.Name, g.IconUrl)).ToList();
        var channels = guilds.SelectMany(g => g.Channels.Where(c => DirectoryKind(c) is not null).Select(c => new DiscordChannel(
            c.Id.ToString(), g.Id.ToString(), c.Name, DirectoryKind(c)!,
            (c as INestedChannel)?.CategoryId?.ToString(), c.Position))).ToList();
        _discordDirectoryRepository.ReplaceServers(servers, channels);

        foreach (var userId in _accountRepository.GetAll().Select(a => a.DiscordUserId).OfType<string>().Distinct())
        {
            if (!ulong.TryParse(userId, out var id))
                continue;
            var visible = new List<string>();
            foreach (var guild in guilds)
            {
                IGuildUser? member = guild.GetUser(id);
                try
                {
                    // Without the members intent most members aren't cached; fetching one doesn't need the intent.
                    member ??= await _discordBotClient.Rest.GetGuildUserAsync(guild.Id, id);
                }
                catch (Exception ex)
                {
                    LogWarn($"Discord directory: couldn't look up {userId} in {guild.Name}: {ex.Message}");
                }
                if (member is null)
                    continue;
                visible.AddRange(guild.Channels
                    .Where(c => DirectoryKind(c) is not null && member.GetPermissions(c).ViewChannel)
                    .Select(c => c.Id.ToString()));
            }
            _discordDirectoryRepository.ReplaceAccess(userId, visible);
        }
    }

    // Threads come and go too fast to pick from a list, so they're left out.
    private static string? DirectoryKind(SocketGuildChannel channel) => channel switch
    {
        SocketThreadChannel => null,
        SocketCategoryChannel => "category",
        SocketForumChannel => "forum",
        SocketNewsChannel => "announcement",
        SocketVoiceChannel => "voice",
        SocketTextChannel => "text",
        _ => null,
    };
}
