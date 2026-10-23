using DevClient.Data;

namespace DevScrape;

/// <summary>
/// The store URLs the API handed over (GET /api/agent/tcg/state), served with the same filtering and order as the database
/// repository so the dev-client store clients run unchanged. Read-only: the TCG page is the one place URLs are edited.
/// </summary>
public sealed class ApiTcgSourceUrlRepository(IEnumerable<TcgSourceUrl> rows) : ITcgSourceUrlRepository
{
    private readonly List<TcgSourceUrl> _rows = rows.ToList();

    public List<TcgSourceUrl> GetAll(string? game = null, string? store = null, bool enabledOnly = false) =>
        _rows
            .Where(u => game == null || string.Equals(u.Game, game, StringComparison.OrdinalIgnoreCase))
            .Where(u => store == null || string.Equals(u.Store, store, StringComparison.OrdinalIgnoreCase))
            .Where(u => !enabledOnly || u.Enabled)
            .OrderBy(u => u.Store, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.Game, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.Id)
            .ToList();

    public int Add(TcgSourceUrl sourceUrl) => throw new NotSupportedException("Source URLs are edited on the TCG page.");
    public void UpdateUrl(int id, string url) => throw new NotSupportedException("Source URLs are edited on the TCG page.");
    public void UpdateEnabled(int id, bool enabled) => throw new NotSupportedException("Source URLs are edited on the TCG page.");
    public void Delete(int id) => throw new NotSupportedException("Source URLs are edited on the TCG page.");
}
