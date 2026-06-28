$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$appSettingsPath = Resolve-Path '..\dev-client\Data\AppSettings.cs'
$appSettings = [System.IO.File]::ReadAllText($appSettingsPath, [System.Text.Encoding]::UTF8)
$appSettings = $appSettings.Replace("    public class AutoReactionRule`r`n    {`r`n        public ulong UserId { get; set; }`r`n        public string[] EmoteIds { get; set; } = [];`r`n    }", "    public class AutoReactionRule`r`n    {`r`n        public ulong UserId { get; set; }`r`n        public string[] EmoteIds { get; set; } = [];`r`n        public string[] ProfilePictureGifUrls { get; set; } = [];`r`n    }")
[System.IO.File]::WriteAllText($appSettingsPath, $appSettings, $utf8NoBom)

$autoReactionRepositoryPath = Resolve-Path '..\dev-api\Data\Discord\AutoReactionRepository.cs'
$autoReactionRepositoryContent = @'
using MySqlConnector;

namespace DevClient.Data.Discord
{
    public class AutoReactionRepository : IAutoReactionRepository
    {
        private readonly string _connectionString;

        public AutoReactionRepository(string connectionString) => _connectionString = connectionString;

        public void EnsureTable()
        {
            using var conn = new MySqlConnection(_connectionString);
            conn.Open();

            Exec(conn, """
                CREATE TABLE IF NOT EXISTS auto_reactions (
                    user_id    BIGINT UNSIGNED NOT NULL,
                    emote_id   VARCHAR(255)    NOT NULL,
                    sort_order INT             NOT NULL DEFAULT 0,
                    PRIMARY KEY (user_id, emote_id)
                )
                """);

            Exec(conn, """
                CREATE TABLE IF NOT EXISTS auto_reaction_profile_picture_gifs (
                    user_id    BIGINT UNSIGNED NOT NULL,
                    gif_url    VARCHAR(512)    NOT NULL,
                    sort_order INT             NOT NULL DEFAULT 0,
                    PRIMARY KEY (user_id, gif_url)
                )
                """);

            Exec(conn, """
                CREATE TABLE IF NOT EXISTS guild_auto_reactions (
                    guild_name VARCHAR(255)    NOT NULL,
                    user_id    BIGINT UNSIGNED NOT NULL,
                    emote_id   VARCHAR(255)    NOT NULL,
                    sort_order INT             NOT NULL DEFAULT 0,
                    PRIMARY KEY (guild_name, user_id, emote_id)
                )
                """);

            Exec(conn, """
                CREATE TABLE IF NOT EXISTS auto_reaction_migrations (
                    migration_name VARCHAR(255) NOT NULL PRIMARY KEY
                )
                """);

            using var migrated = conn.CreateCommand();
            migrated.CommandText = """
                SELECT COUNT(*)
                FROM auto_reaction_migrations
                WHERE migration_name = 'seeded_from_guild_auto_reactions'
                """;
            var hasSeededLegacyRules = Convert.ToInt32(migrated.ExecuteScalar()) > 0;

            if (!hasSeededLegacyRules)
            {
                using var tx = conn.BeginTransaction();

                using (var insertRules = conn.CreateCommand())
                {
                    insertRules.Transaction = tx;
                    insertRules.CommandText = """
                        INSERT IGNORE INTO auto_reactions (user_id, emote_id, sort_order)
                        SELECT user_id, emote_id, MIN(sort_order)
                        FROM guild_auto_reactions
                        GROUP BY user_id, emote_id
                        """;
                    insertRules.ExecuteNonQuery();
                }

                using (var markMigration = conn.CreateCommand())
                {
                    markMigration.Transaction = tx;
                    markMigration.CommandText = """
                        INSERT INTO auto_reaction_migrations (migration_name)
                        VALUES ('seeded_from_guild_auto_reactions')
                        """;
                    markMigration.ExecuteNonQuery();
                }

                tx.Commit();
            }
        }

        public AutoReactionRule[] GetAll()
        {
            EnsureTable();

            using var conn = new MySqlConnection(_connectionString);
            conn.Open();

            var emoteMap = new Dictionary<ulong, List<string>>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT user_id, emote_id, sort_order FROM auto_reactions ORDER BY user_id, sort_order, emote_id";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var userId = reader.GetUInt64("user_id");
                    if (!emoteMap.TryGetValue(userId, out var emotes))
                        emoteMap[userId] = emotes = [];

                    emotes.Add(reader.GetString("emote_id"));
                }
            }

            var gifMap = new Dictionary<ulong, List<string>>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT user_id, gif_url, sort_order FROM auto_reaction_profile_picture_gifs ORDER BY user_id, sort_order, gif_url";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var userId = reader.GetUInt64("user_id");
                    if (!gifMap.TryGetValue(userId, out var gifUrls))
                        gifMap[userId] = gifUrls = [];

                    gifUrls.Add(reader.GetString("gif_url"));
                }
            }

            return emoteMap.Keys
                .Union(gifMap.Keys)
                .OrderBy(userId => userId)
                .Select(userId => new AutoReactionRule
                {
                    UserId = userId,
                    EmoteIds = emoteMap.GetValueOrDefault(userId)?.ToArray() ?? [],
                    ProfilePictureGifUrls = gifMap.GetValueOrDefault(userId)?.ToArray() ?? []
                })
                .ToArray();
        }

        public void ReplaceAll(IEnumerable<AutoReactionRule> rules)
        {
            EnsureTable();

            var normalizedRules = rules
                .Where(rule => rule.UserId != 0)
                .Select(rule => new AutoReactionRule
                {
                    UserId = rule.UserId,
                    EmoteIds = NormalizeValues(rule.EmoteIds),
                    ProfilePictureGifUrls = NormalizeValues(rule.ProfilePictureGifUrls)
                })
                .Where(rule => rule.EmoteIds.Length > 0 || rule.ProfilePictureGifUrls.Length > 0)
                .OrderBy(rule => rule.UserId)
                .ToArray();

            using var conn = new MySqlConnection(_connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();

            using (var delete = conn.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM auto_reactions";
                delete.ExecuteNonQuery();
            }

            using (var delete = conn.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM auto_reaction_profile_picture_gifs";
                delete.ExecuteNonQuery();
            }

            foreach (var rule in normalizedRules)
            {
                for (var i = 0; i < rule.EmoteIds.Length; i++)
                {
                    using var insert = conn.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText = """
                        INSERT INTO auto_reactions (user_id, emote_id, sort_order)
                        VALUES (@userId, @emoteId, @sortOrder)
                        """;
                    insert.Parameters.AddWithValue("@userId", rule.UserId);
                    insert.Parameters.AddWithValue("@emoteId", rule.EmoteIds[i]);
                    insert.Parameters.AddWithValue("@sortOrder", i);
                    insert.ExecuteNonQuery();
                }

                for (var i = 0; i < rule.ProfilePictureGifUrls.Length; i++)
                {
                    using var insert = conn.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText = """
                        INSERT INTO auto_reaction_profile_picture_gifs (user_id, gif_url, sort_order)
                        VALUES (@userId, @gifUrl, @sortOrder)
                        """;
                    insert.Parameters.AddWithValue("@userId", rule.UserId);
                    insert.Parameters.AddWithValue("@gifUrl", rule.ProfilePictureGifUrls[i]);
                    insert.Parameters.AddWithValue("@sortOrder", i);
                    insert.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }

        private static string[] NormalizeValues(IEnumerable<string>? values) => (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        private static void Exec(MySqlConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
'@
[System.IO.File]::WriteAllText($autoReactionRepositoryPath, $autoReactionRepositoryContent, $utf8NoBom)

$autoReactionEndpointsPath = Resolve-Path '..\dev-api\Endpoints\AutoReactionEndpoints.cs'
$autoReactionEndpointsContent = @'
using DevClient.Data;
using DevClient.Data.Discord;

namespace DevApi.Endpoints;

public static class AutoReactionEndpoints
{
    public static IEndpointRouteBuilder MapAutoReactionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/AutoReactions", () =>
        {
            var html = """
                <!DOCTYPE html>
                <html lang="en">
                <head>
                  <meta charset="UTF-8" />
                  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                  <title>Global Auto Reactions</title>
                  <style>
                    :root {
                      --bg: #f6efe7;
                      --ink: #1f2937;
                      --muted: #6b7280;
                      --panel: rgba(255,255,255,0.9);
                      --line: rgba(31,41,55,0.12);
                      --accent: #0f766e;
                      --accent-2: #c2410c;
                      --danger: #b91c1c;
                      --shadow: 0 22px 60px rgba(31, 41, 55, 0.12);
                    }
                    * { box-sizing: border-box; }
                    body {
                      margin: 0;
                      min-height: 100vh;
                      font-family: Georgia, "Times New Roman", serif;
                      color: var(--ink);
                      background:
                        radial-gradient(circle at top left, rgba(15, 118, 110, 0.2), transparent 28%),
                        radial-gradient(circle at bottom right, rgba(194, 65, 12, 0.16), transparent 26%),
                        linear-gradient(180deg, #fffaf3 0%, var(--bg) 100%);
                    }
                    main {
                      width: min(1200px, calc(100% - 28px));
                      margin: 0 auto;
                      padding: 32px 0 56px;
                    }
                    .hero, .panel {
                      background: var(--panel);
                      border: 1px solid var(--line);
                      border-radius: 28px;
                      box-shadow: var(--shadow);
                      backdrop-filter: blur(10px);
                    }
                    .hero {
                      padding: 28px;
                      margin-bottom: 20px;
                    }
                    .eyebrow {
                      display: inline-block;
                      padding: 6px 10px;
                      border-radius: 999px;
                      font-size: 0.78rem;
                      text-transform: uppercase;
                      letter-spacing: 0.08em;
                      font-weight: 700;
                      color: var(--accent);
                      background: rgba(15, 118, 110, 0.12);
                      margin-bottom: 10px;
                    }
                    h1 {
                      margin: 0 0 10px;
                      font-size: clamp(2rem, 5vw, 3.4rem);
                      line-height: 0.96;
                      letter-spacing: -0.04em;
                    }
                    .hero p {
                      margin: 0;
                      max-width: 760px;
                      color: var(--muted);
                      line-height: 1.6;
                    }
                    .panel {
                      padding: 24px;
                    }
                    .toolbar {
                      display: flex;
                      gap: 12px;
                      justify-content: space-between;
                      align-items: center;
                      margin-bottom: 18px;
                      flex-wrap: wrap;
                    }
                    .toolbar-copy {
                      color: var(--muted);
                      max-width: 720px;
                      line-height: 1.5;
                    }
                    .actions {
                      display: flex;
                      gap: 10px;
                      flex-wrap: wrap;
                    }
                    button {
                      border: none;
                      border-radius: 999px;
                      padding: 12px 18px;
                      font: inherit;
                      font-weight: 700;
                      cursor: pointer;
                    }
                    .primary {
                      color: white;
                      background: linear-gradient(135deg, var(--accent), var(--accent-2));
                      box-shadow: 0 12px 24px rgba(15, 118, 110, 0.18);
                    }
                    .secondary {
                      color: var(--ink);
                      background: rgba(255,255,255,0.86);
                      border: 1px solid var(--line);
                    }
                    .danger {
                      color: white;
                      background: var(--danger);
                    }
                    .status {
                      min-height: 1.4rem;
                      margin-bottom: 14px;
                      color: var(--muted);
                    }
                    .status.error { color: var(--danger); }
                    .status.success { color: var(--accent); }
                    .table-wrap {
                      overflow-x: auto;
                    }
                    table {
                      width: 100%;
                      border-collapse: collapse;
                      min-width: 900px;
                    }
                    th, td {
                      text-align: left;
                      vertical-align: top;
                      padding: 14px 12px;
                      border-top: 1px solid var(--line);
                    }
                    th {
                      font-size: 0.78rem;
                      letter-spacing: 0.06em;
                      text-transform: uppercase;
                      color: var(--muted);
                    }
                    td:first-child, th:first-child { padding-left: 0; }
                    td:last-child, th:last-child { padding-right: 0; }
                    input, textarea {
                      width: 100%;
                      border: 1px solid rgba(31,41,55,0.16);
                      border-radius: 16px;
                      padding: 12px 14px;
                      font: inherit;
                      color: var(--ink);
                      background: rgba(255,255,255,0.86);
                    }
                    textarea {
                      min-height: 122px;
                      resize: vertical;
                      line-height: 1.45;
                    }
                    input:focus, textarea:focus {
                      outline: 2px solid rgba(15, 118, 110, 0.25);
                      border-color: rgba(15, 118, 110, 0.35);
                    }
                    .hint {
                      margin-top: 8px;
                      font-size: 0.82rem;
                      color: var(--muted);
                      line-height: 1.45;
                    }
                    .empty {
                      text-align: center;
                      color: var(--muted);
                      padding: 30px 12px 12px;
                    }
                    @media (max-width: 720px) {
                      main { width: min(100%, calc(100% - 18px)); }
                      .hero, .panel { border-radius: 22px; }
                      .panel { padding: 18px; }
                    }
                  </style>
                </head>
                <body>
                  <main>
                    <section class="hero">
                      <div class="eyebrow">Discord Admin</div>
                      <h1>Global Auto Reactions</h1>
                      <p>Manage message reactions and optional profile-picture watch lists for specific users. If a user has one or more profile-picture GIF URLs, the bot will DM them one random GIF whenever their avatar changes.</p>
                    </section>
                    <section class="panel">
                      <div class="toolbar">
                        <div class="toolbar-copy">Use one row per Discord user. Reactions can be comma-separated or one-per-line. Profile-picture GIF URLs should be one URL per line.</div>
                        <div class="actions">
                          <button id="add-row" class="secondary" type="button">Add User</button>
                          <button id="save" class="primary" type="button">Save Changes</button>
                        </div>
                      </div>
                      <div id="status" class="status"></div>
                      <div class="table-wrap">
                        <table>
                          <thead>
                            <tr>
                              <th style="width: 18%;">Discord User ID</th>
                              <th style="width: 28%;">Auto Reactions</th>
                              <th style="width: 44%;">Profile Picture GIF URLs</th>
                              <th style="width: 10%;"></th>
                            </tr>
                          </thead>
                          <tbody id="rows"></tbody>
                        </table>
                      </div>
                      <div id="empty" class="empty" hidden>No rules yet. Add a user to get started.</div>
                    </section>
                  </main>
                  <template id="row-template">
                    <tr>
                      <td>
                        <input data-field="userId" type="text" inputmode="numeric" placeholder="178295063808311297" />
                        <div class="hint">Discord snowflake user ID.</div>
                      </td>
                      <td>
                        <textarea data-field="emoteIds" placeholder="😂&#10;🔥&#10;&lt;a:partyblob:123456789012345678&gt;"></textarea>
                        <div class="hint">Comma-separated also works if you prefer a single line.</div>
                      </td>
                      <td>
                        <textarea data-field="profilePictureGifUrls" placeholder="https://media.tenor.com/.../celebration.gif&#10;https://media.tenor.com/.../nice.gif"></textarea>
                        <div class="hint">Leave blank to disable avatar-change DMs for this user.</div>
                      </td>
                      <td>
                        <button class="danger" data-action="remove" type="button">Remove</button>
                      </td>
                    </tr>
                  </template>
                  <script>
                    const rowsEl = document.getElementById('rows');
                    const emptyEl = document.getElementById('empty');
                    const statusEl = document.getElementById('status');
                    const template = document.getElementById('row-template');

                    const splitValues = value => value
                      .split(/\r?\n|,/)
                      .map(part => part.trim())
                      .filter(Boolean);

                    const joinLines = values => (values || []).join('\n');

                    function setStatus(message, type = '') {
                      statusEl.textContent = message || '';
                      statusEl.className = `status${type ? ` ${type}` : ''}`;
                    }

                    function refreshEmptyState() {
                      emptyEl.hidden = rowsEl.children.length !== 0;
                    }

                    function addRow(rule = {}) {
                      const fragment = template.content.cloneNode(true);
                      const row = fragment.querySelector('tr');
                      row.querySelector('[data-field="userId"]').value = rule.userId || '';
                      row.querySelector('[data-field="emoteIds"]').value = joinLines(rule.emoteIds);
                      row.querySelector('[data-field="profilePictureGifUrls"]').value = joinLines(rule.profilePictureGifUrls);
                      row.querySelector('[data-action="remove"]').addEventListener('click', () => {
                        row.remove();
                        refreshEmptyState();
                      });
                      rowsEl.appendChild(fragment);
                      refreshEmptyState();
                    }

                    function collectRules() {
                      return Array.from(rowsEl.querySelectorAll('tr')).map(row => ({
                        userId: row.querySelector('[data-field="userId"]').value.trim(),
                        emoteIds: splitValues(row.querySelector('[data-field="emoteIds"]').value),
                        profilePictureGifUrls: splitValues(row.querySelector('[data-field="profilePictureGifUrls"]').value)
                      })).filter(rule => rule.userId || rule.emoteIds.length || rule.profilePictureGifUrls.length);
                    }

                    async function loadRules() {
                      setStatus('Loading rules...');
                      const response = await fetch('/api/auto-reactions');
                      if (!response.ok) throw new Error('Failed to load auto reactions.');
                      const rules = await response.json();
                      rowsEl.innerHTML = '';
                      rules.forEach(addRow);
                      refreshEmptyState();
                      setStatus(`Loaded ${rules.length} rule${rules.length === 1 ? '' : 's'}.`);
                    }

                    async function saveRules() {
                      const rules = collectRules();
                      setStatus('Saving changes...');
                      const response = await fetch('/api/auto-reactions', {
                        method: 'PUT',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify(rules)
                      });
                      if (!response.ok) {
                        const message = await response.text();
                        throw new Error(message || 'Failed to save auto reactions.');
                      }
                      setStatus('Saved successfully.', 'success');
                    }

                    document.getElementById('add-row').addEventListener('click', () => addRow());
                    document.getElementById('save').addEventListener('click', async () => {
                      try {
                        await saveRules();
                      } catch (error) {
                        setStatus(error.message || 'Something went wrong while saving.', 'error');
                      }
                    });

                    loadRules().catch(error => setStatus(error.message || 'Failed to load page data.', 'error'));
                  </script>
                </body>
                </html>
                """;
            return Results.Content(html, "text/html");
        });

        app.MapGet("/api/auto-reactions", (IAutoReactionRepository repo) =>
            repo.GetAll()
                .Select(rule => new
                {
                    userId = rule.UserId.ToString(),
                    emoteIds = rule.EmoteIds,
                    profilePictureGifUrls = rule.ProfilePictureGifUrls,
                }));

        app.MapPut("/api/auto-reactions", (AutoReactionRuleDto[] rules, IAutoReactionRepository repo) =>
        {
            var parsedRules = rules
                .Select(rule => new AutoReactionRule
                {
                    UserId = ulong.TryParse(rule.UserId, out var userId) ? userId : 0UL,
                    EmoteIds = rule.EmoteIds ?? [],
                    ProfilePictureGifUrls = rule.ProfilePictureGifUrls ?? [],
                })
                .Where(rule => rule.UserId != 0)
                .ToArray();

            repo.ReplaceAll(parsedRules);
            return Results.Ok();
        });

        return app;
    }
}

public class AutoReactionRuleDto
{
    public string UserId { get; set; } = string.Empty;
    public string[] EmoteIds { get; set; } = [];
    public string[] ProfilePictureGifUrls { get; set; } = [];
}
'@
[System.IO.File]::WriteAllText($autoReactionEndpointsPath, $autoReactionEndpointsContent, $utf8NoBom)

$botServicePath = Resolve-Path 'BotService.cs'
$botService = [System.IO.File]::ReadAllText($botServicePath, [System.Text.Encoding]::UTF8)
$botService = $botService.Replace("        _discordBotClient.MessageReceived += MonitorMessages;`r`n        _discordBotClient.ReactionAdded += OnReactionAdded;", "        _discordBotClient.MessageReceived += MonitorMessages;`r`n        _discordBotClient.UserUpdated += OnUserUpdated;`r`n        _discordBotClient.ReactionAdded += OnReactionAdded;")
[System.IO.File]::WriteAllText($botServicePath, $botService, $utf8NoBom)

$botHelpersPath = Resolve-Path 'BotHelpers.cs'
$botHelpers = [System.IO.File]::ReadAllText($botHelpersPath, [System.Text.Encoding]::UTF8)
$botHelpers = $botHelpers.Replace("    private IEnumerable<IEmote> ResolveAutoReactionEmotes(SocketMessage message)`r`n    {`r`n        if (_autoReactionRules.Length == 0) yield break;`r`n`r`n        var emoteIds = _autoReactionRules`r`n            .Where(rule => rule.UserId == message.Author.Id)`r`n            .SelectMany(rule => rule.EmoteIds ?? [])`r`n            .Where(emoteId => !string.IsNullOrWhiteSpace(emoteId))`r`n            .Select(emoteId => emoteId.Trim())`r`n            .Distinct(StringComparer.Ordinal);`r`n`r`n        foreach (var emoteId in emoteIds)`r`n        {`r`n            if (TryResolveAutoReactionEmote(message, emoteId, out var emote))`r`n                yield return emote;`r`n        }`r`n    }", "    private IEnumerable<IEmote> ResolveAutoReactionEmotes(SocketMessage message)`r`n    {`r`n        if (_autoReactionRules.Length == 0) yield break;`r`n`r`n        var emoteIds = _autoReactionRules`r`n            .Where(rule => rule.UserId == message.Author.Id)`r`n            .SelectMany(rule => rule.EmoteIds ?? [])`r`n            .Where(emoteId => !string.IsNullOrWhiteSpace(emoteId))`r`n            .Select(emoteId => emoteId.Trim())`r`n            .Distinct(StringComparer.Ordinal);`r`n`r`n        foreach (var emoteId in emoteIds)`r`n        {`r`n            if (TryResolveAutoReactionEmote(message, emoteId, out var emote))`r`n                yield return emote;`r`n        }`r`n    }`r`n`r`n    private string[] ResolveProfilePictureGifUrls(ulong userId) => _autoReactionRules`r`n        .Where(rule => rule.UserId == userId)`r`n        .SelectMany(rule => rule.ProfilePictureGifUrls ?? [])`r`n        .Where(gifUrl => !string.IsNullOrWhiteSpace(gifUrl))`r`n        .Select(gifUrl => gifUrl.Trim())`r`n        .Distinct(StringComparer.Ordinal)`r`n        .ToArray();")
[System.IO.File]::WriteAllText($botHelpersPath, $botHelpers, $utf8NoBom)

$avatarMonitorPath = Join-Path (Resolve-Path '.').Path 'AvatarMonitor.cs'
$avatarMonitorContent = @'
using Discord.WebSocket;

public partial class BotService
{
    private Task OnUserUpdated(Cacheable<SocketUser, ulong> before, SocketUser after)
    {
        if (after.IsBot)
            return Task.CompletedTask;

        ReloadAutoReactionsIfStale();

        var gifUrls = ResolveProfilePictureGifUrls(after.Id);
        if (gifUrls.Length == 0)
            return Task.CompletedTask;

        if (!before.HasValue)
            return Task.CompletedTask;

        var previousAvatarId = before.Value.AvatarId;
        var currentAvatarId = after.AvatarId;
        if (string.Equals(previousAvatarId, currentAvatarId, StringComparison.Ordinal))
            return Task.CompletedTask;

        return SendProfilePictureGifAsync(after, gifUrls, previousAvatarId, currentAvatarId);
    }

    private async Task SendProfilePictureGifAsync(SocketUser user, string[] gifUrls, string? previousAvatarId, string? currentAvatarId)
    {
        try
        {
            var gifUrl = gifUrls[Random.Shared.Next(gifUrls.Length)];
            LogInfo($"Detected avatar change for {user.Username} ({user.Id}). Sending GIF. Old avatar: {previousAvatarId ?? "<none>"}, new avatar: {currentAvatarId ?? "<none>"}");
            await SendDmAsync(user, gifUrl);
        }
        catch (Exception ex)
        {
            LogWarn($"Failed to send profile picture GIF DM to {user.Username} ({user.Id}): {ex.Message}");
        }
    }
}
'@
[System.IO.File]::WriteAllText($avatarMonitorPath, $avatarMonitorContent, $utf8NoBom)

$autoReactionTestsPath = Resolve-Path '..\dev-api\dev-api-tests\Tests\AutoReactionEndpointsTests.cs'
$autoReactionTestsContent = @'
namespace DevApi_tests.Tests;

public class AutoReactionEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public AutoReactionEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.ResetMocks();
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetAutoReactions_ReturnsRules()
    {
        _factory.AutoReactionRepo.Setup(r => r.GetAll()).Returns(
        [
            new AutoReactionRule
            {
                UserId = 178295063808311297ul,
                EmoteIds = ["123456789012345678", "😂"],
                ProfilePictureGifUrls = ["https://media.tenor.com/test-one.gif", "https://media.tenor.com/test-two.gif"]
            }
        ]);

        var response = await _client.GetAsync("/api/auto-reactions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("178295063808311297", body);
        Assert.Contains("123456789012345678", body);
        Assert.Contains("https://media.tenor.com/test-one.gif", body);
    }

    [Fact]
    public async Task PutAutoReactions_ReplacesRules()
    {
        var dto = """
            [
              {
                "userId": "178295063808311297",
                "emoteIds": ["123456789012345678", "😂"],
                "profilePictureGifUrls": [
                  "https://media.tenor.com/test-one.gif",
                  "https://media.tenor.com/test-two.gif"
                ]
              }
            ]
            """;

        var response = await _client.PutAsync(
            "/api/auto-reactions",
            new StringContent(dto, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _factory.AutoReactionRepo.Verify(r => r.ReplaceAll(It.Is<AutoReactionRule[]>(rules =>
            rules.Length == 1
            && rules[0].UserId == 178295063808311297ul
            && rules[0].EmoteIds.SequenceEqual(new[] { "123456789012345678", "😂" })
            && rules[0].ProfilePictureGifUrls.SequenceEqual(new[]
            {
                "https://media.tenor.com/test-one.gif",
                "https://media.tenor.com/test-two.gif"
            }))), Times.Once);
    }
}
'@
[System.IO.File]::WriteAllText($autoReactionTestsPath, $autoReactionTestsContent, $utf8NoBom)
