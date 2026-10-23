# dev-scrape

Console tool for the home PC. It scrapes one TCG job with the same store clients the bot used to run on Railway (`dev-client`),
then uploads the result to the API. The cloud bot reads the upload and does everything Discord (filters, diff, posts, pings).
It is not part of the bot: `DevBot.csproj` excludes `tools\**`, and the Railway images never build it.

```
DevScrape.exe <job>
```

| Job | Agent alias | Needs |
|---|---|---|
| `pokemon` | `tcg-pokemon` | Playwright Chromium (EB Games, JJ, Walmart); give it a timeout of 8 minutes or more |
| `gundam` | `tcg-gundam` | plain HTTP |
| `pokemon-preorder` | `tcg-pokemon-preorder` | plain HTTP |
| `gundam-preorder` | `tcg-gundam-preorder` | plain HTTP |
| `pokecenter-security` | `tcg-pokecenter-security` | plain HTTP (captures the queue/captcha snapshot; the bot compares it with the stored state) |

Flow: `GET /api/agent/tcg/state` (store URLs from the TCG page) -> run the job's scrapers, each in its own try/catch ->
`PUT /api/agent/tcg/results`. One status line is printed (the agent keeps the last 1000 characters).

Exit codes: 0 uploaded (stores that failed are listed in the upload's `errors` and `failedStores`); 1 nothing could be
scraped or uploaded (nothing is uploaded when every scraper failed, so the last good scrape stays); 2 missing settings;
64 bad argument. The token is never printed.

## Build and publish

From `D:\Code\dev-bot\tools\dev-scrape`:

```powershell
dotnet publish -c Release -r win-x64 --self-contained -o C:\dev-bot-agent\dev-scrape
```

Install Playwright's Chromium once (and after upgrading `Microsoft.Playwright`), from the output folder:

```powershell
cd C:\dev-bot-agent\dev-scrape
powershell -ExecutionPolicy Bypass -File .\playwright.ps1 install chromium
```

(`pwsh .\playwright.ps1 install chromium` works too if PowerShell 7 is installed.)

## Settings (never commit)

Environment variables, or a `config.json` next to `DevScrape.exe` with the same names:

```json
{
  "DEVBOT_API_BASE": "https://devbot.gruulsgonewild.com",
  "DEVBOT_AGENT_TOKEN": "dga_...",
  "DEVSCRAPE_HEADED": false
}
```

- `DEVBOT_AGENT_TOKEN` is the home agent's token (or a new one made on the Server commands page).
- `DEVSCRAPE_HEADED: true` opens a visible Chromium window for the Playwright stores. That needs a logged-in desktop
  session for the agent; leave it off unless a store fails headless.
- The ExpressVPN proxy stays off. To use it, add an `"expressVpn": { "enabled": true, "proxyServer": "..." }` section to
  `config.json` (same keys as the bot's `expressVpn` settings).

## Agent aliases

Add five actions to the home agent's config, one per job, then restart the agent so its hello lists them. The alias names
must match exactly:

`tcg-pokemon`, `tcg-gundam`, `tcg-pokemon-preorder`, `tcg-gundam-preorder`, `tcg-pokecenter-security`

Each runs the same executable with the job as its argument. The agent's config format is not known to this repo, so this is
only the shape of one entry; adapt it to the agent's real format:

```
tcg-pokemon = C:\dev-bot-agent\dev-scrape\DevScrape.exe pokemon
```

Give `tcg-pokemon` a per-action timeout of at least 8 minutes and the others at least 5.

## Check it by hand

```powershell
$env:DEVBOT_API_BASE = "https://devbot.gruulsgonewild.com"; $env:DEVBOT_AGENT_TOKEN = "dga_..."
.\DevScrape.exe gundam
```

Then open the Database tab's `tcg_scrape_results` table (or the TCG page's scrape status) to see the upload.
