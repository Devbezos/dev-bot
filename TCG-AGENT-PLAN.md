# TCG scraping via the home agent: plan

## Why

The store scrapers (Pokemon, Gundam, both pre-order jobs, Pokemon Center security) were switched off on Railway (`scheduled_jobs.enabled = 0`) because cloud IPs get blocked and several stores need a real browser. The home PC has a residential IP and a desktop, and the agent (long-poll to the API, already used for server restarts and RXPGuides) can run work there.

Same split as RXPGuides: **the home PC scrapes, the cloud bot does everything Discord**: price filters, blacklist words, hidden items, preorder split, "newly seen" detection, channel posts, pings, saved results and the dashboard pages stay in the bot and API as they are.

## What exists today (from the code)

- `Scheduler.cs` runs these jobs: Pokemon TCG, Gundam TCG, Pokemon pre-order, Gundam pre-order, Pokemon Center security. Each calls store clients in `dev-client/Clients/Pokemon/` (401Games, Atlas, Chimera, Dollys, EB Games, Hobbiesville, House of Cards, JJ, Walmart, Canada Computers, Shopify collections, Pokemon Center), which return `List<Search>` (keyword, store, products of name/price/url).
- The clients read store URLs from the `tcg_source_urls` table (`ITcgSourceUrlRepository`, edited on the TCG page).
- EB Games, JJ, Walmart and the Pokemon Center security check use Playwright (`PlaywrightBrowser.cs`). Traffic can go through an ExpressVPN proxy (`ScrapingNetwork.cs`).
- eBay resale values (`ResaleEndpoints`) are a plain API call and can stay in the cloud.

## Flow

1. A scheduled TCG job runs in the bot. Instead of scraping it queues one agent alias and waits (it must not block the scheduler tick; I'll run it off the tick, unlike the 6-minute RXP wait).
2. The agent claims the alias and runs the **tcg-scrape tool** on the home PC. The tool:
   1. Gets the store URLs from `GET /api/agent/tcg/state` (the TCG page stays the one place to edit them).
   2. Runs the same client classes for that job (it reuses `dev-client`, so no scraper logic is rewritten) with each store in its own try/catch.
   3. Uploads the combined result to `PUT /api/agent/tcg/results` as JSON: `{ job, scrapedAt, searches: [{keyword, store, products: [{name, price, url}]}], errors: ["store: reason"] }`.
   4. Prints one short status line (the agent keeps the last 1000 characters) and exits 0 when the upload succeeded, even if some stores failed (they are listed in `errors`); non-zero if nothing could be scraped or uploaded.
3. The API keeps the latest upload per job (new table `tcg_scrape_results`).
4. When the command finishes, the bot reads that row and runs the existing pipeline unchanged on it: filters, diff against the previous run, post to channels, pin/alert state, notify users, save results.
5. Failures show on the Server commands page (agent output) and in the bot log; a store that failed keeps its last known products rather than looking "sold out".

## Aliases to add to the agent config (one per job; aliases can't take arguments)

| Alias | Runs | Needs |
|---|---|---|
| `tcg-pokemon` | Pokemon TCG scrape | Playwright (EB Games, JJ, Walmart) |
| `tcg-gundam` | Gundam TCG scrape | plain HTTP |
| `tcg-pokemon-preorder` | Pokemon pre-order scrape | plain HTTP |
| `tcg-gundam-preorder` | Gundam pre-order scrape | plain HTTP |
| `tcg-pokecenter-security` | Pokemon Center queue/captcha snapshot | Playwright |

Each runs the same executable with the job as its argument in the agent's own config, for example `DevScrape.exe pokemon`. Please paste your Valheim and `rxp` entries (secrets removed) so I write these in the exact format.

## What I will change

- **New console project `dev-scrape`** (in this repo set, referencing `dev-client`; published self-contained for Windows): the tool above, plus an API-backed `ITcgSourceUrlRepository` so the existing clients work without a database.
- **dev-api**: migration for `tcg_scrape_results`; agent-only routes `GET /api/agent/tcg/state`, `PUT /api/agent/tcg/results` (same bearer `dga_` auth, rate limit and size cap as the RXP routes); table shows in the Database tab.
- **dev-bot**: the five job runners queue their alias via `RemoteTaskQueue`, read the uploaded results and continue into the current filter/post code; Playwright launch moves out of the bot for these jobs. The TCG jobs are re-enabled in `scheduled_jobs` only after the agent is confirmed working.
- **dev-ui**: TCG page shows the agent status, the last scrape time per job and per-store errors; copy says scraping runs on the home server.
- Tests for the routes (auth, size, bad payload) and the bot flow (agent offline, partial store failure, unchanged, new product).

## What needs adding on your server

1. **A logged-in desktop session** for the agent (or a scheduled task running as you) if any store needs a headed browser, as with the RXP forum. Tell me which stores fail headless once we try.
2. **Playwright's Chromium** installed for the published tool (`playwright install chromium` from the tool's output folder; I'll give the exact command).
3. **The `dev-scrape` folder** (I'll build it and give you a zip or publish command) at a path you choose, for example `C:\dev-bot-agent\dev-scrape\`.
4. **Config** next to the tool or as environment variables, never committed: `DEVBOT_API_BASE=https://devbot.gruulsgonewild.com` and `DEVBOT_AGENT_TOKEN=dga_...` (the existing agent's token or a new one).
5. **Five new actions** in the agent config (table above), then restart the agent so it says hello with them.
6. **Per-action timeout** of at least 5 minutes for `tcg-pokemon` (EB Games, JJ and Walmart are slow).
7. **ExpressVPN**: decide whether the home PC should use it. The home IP is residential, so I suggest leaving the proxy off at first (`expressVpn:enabled` stays false on the PC). If you want it, say how the proxy is exposed on the PC (the bot's `Rotate-ExpressVpnLocation.ps1` suggests a local proxy).
8. **Outbound HTTPS** from the PC to the stores and `devbot.gruulsgonewild.com`. Nothing inbound.

## Things to decide

- **Cadence**: how often should each job run (today's `scheduled_jobs` intervals)? Every run is an agent command; Server commands "Recent runs" would fill with scrape runs. I suggest tagging them as system runs and hiding them there by default.
- **Fallback**: if the home PC is off, should the bot skip the run quietly or alert you in Discord after N missed runs? I suggest an alert after 3 misses.
- **State files**: the bot keeps some TCG state in files under `data/` (`tcg-notified-products.json`, `tcg-channel-post-state.json`, `tcg-channel-alert-state.json`). On Railway those vanish at every deploy, which would repost products. I'll check how much of that already lives in the database and move the rest there as part of this work.
- **Pokemon Center security**: keep it in the same alias model, or drop it?
- **Which stores to keep**: any store you want removed instead of ported?

## Rollout

1. I build and test dev-api, dev-bot, dev-ui and dev-scrape locally against a fake agent.
2. You do the server steps above and tell me the agent said hello with the five aliases.
3. I push (api first, then bot and ui), then run one alias from the dashboard and check the stored result before enabling any schedule.
4. Enable one job at a time (Gundam first, plain HTTP and smaller), then Pokemon, then pre-orders and security. Re-enable scheduling in the Schedule page only after each is verified.
