# AGENTS.md — PodereBot

## Project

.NET 10 ASP.NET Core bot running on a Raspberry Pi (Linux ARM). Controls gates and heating and reports temperatures through Telegram, with an AI assistant and an MCP endpoint. Single-project with a small xUnit test project and a GitHub Actions workflow.

## Build & run

```powershell
dotnet build
dotnet run                    # uses mock GPIO drivers in Development
dotnet build /p:OutputPath=build   # production-style build (matches install.sh)
```

`TELEGRAM_API_KEY` must be set (loaded from `.env` file at startup via DotNetEnv).

The Telegram.Bot SDK comes from a custom Azure DevOps feed (see `PodereBot.csproj` `RestoreSources`).

## Hardware drivers

| Environment | Pin driver | Temperature driver |
|-------------|-----------|-------------------|
| Development (ASPNETCORE_ENVIRONMENT=Development) | `MockPinDriver` | `MockTemperatureDriver` |
| Production (`SerialPort` set) | `SerialPinDriver` (COM4 @ 115200 baud) | `OneWireEmbeddedTemperatureDriver` |
| Production (no `SerialPort`) | `EmbeddedPinDriver` (System.Device.Gpio) | `OneWireEmbeddedTemperatureDriver` |

GPIO pins configured in `appsettings.json` `Pins` section.

## Bot commands

Commands auto-register via `[CommandMetadata(Key, Description, Admin)]` attribute and reflection. Commands with `Admin = true` require the sender to hold the `admin` entitlement.

Admin commands are also hidden from non-admins through Telegram command scopes: the public list is set as the default scope, and the full list is set per admin private chat (`BotCommandScopeChat`) at startup and lazily the first time an admin messages the bot. Hiding is cosmetic only; `Command.HandleAuthorization` still enforces access.

`/adminpanel` is the admin panel: it lists every configured user as a clickable Telegram contact with their entitlements and topic subscriptions. It adds a user by typed id or the `KeyboardButtonRequestUsers` picker, toggles the `admin` and `gate_controller` entitlements, toggles the `technical`, `heating` and `gates` topics, and removes a user with confirmation. `UserDirectory` resolves the effective set as the `Users` seed in `appsettings.json` merged with the gitignored `users.json`, minus the ids listed under `Removed`, so runtime changes survive `patch.sh` and rebuilds. A legacy `admins.json` is migrated once at startup and then deleted. Removing or demoting the last admin is refused. `PendingInputRegistry` keeps the AI fallback from answering the typed id.

Interactive commands use `AttachEvents()`/`DetachEvents()` pattern for inline keyboard callbacks. Messages queued for deletion via `DeleteOnDetach()` are cleaned up on detach.

Bot language is **Italian**.

## REST API

All endpoints are restricted to local network (192.168.x.x, 127.0.0.1). Binds to `http://0.0.0.0:5050`.

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/status` | Process uptime + memory |
| GET | `/api/health` | Deep health check: Telegram connection and database writability. Returns 503 when unhealthy; the systemd watchdog calls this |
| GET | `/api/temperatures` | Local probe, external readings and the operational average |
| GET | `/api/heating` | Operative temperature, boiler state, program intervals, antifreeze threshold |
| GET | `/api/gates` | Access window, users who had access, rate limit settings |
| POST | `/api/temperature` | Accept external sensor readings |

API endpoints auto-register via `IEndpoint` reflection (same pattern as commands). `install.sh` generates `API_TOKEN` into `.env` when missing, so on a real deployment every `/api/*` and `/mcp` request must send it in the `X-Api-Token` header (compared in constant time); the watchdog reads the same value. Clearing the value disables the requirement and leaves the local-network restriction as the only guard.

## Heating

Heating program format: `hh:mm-hh:mm@temp/hh:mm-hh:mm@temp` (24h). `HeatingProgram.TryBuild()` validates intervals are non-overlapping, sorted, and that each interval starts before it ends (an interval crossing midnight is refused rather than silently never matching). `HeatingProgramDaemon` polls every `PollIntervalSeconds` and applies temperature hysteresis with `ToleranceSeconds` delay.

Antifreeze (`Heating:Antifreeze`) runs before the program: below `ThresholdCelsius` (default 5 °C) the boiler is forced on regardless of the program, of its suspension and of a manual switch-off, and it releases only above the threshold plus 0.5 °C of hysteresis.

`HeatingMonitorDaemon` only observes and notifies, never actuates: boiler state changes, a boiler running longer than `Heating:BoilerAlert:MaxContinuousHours`, the operative temperature leaving `Heating:TemperatureBand`, and stale external sensors all produce topic notifications with one message per episode.

## Notifications

Every notification is routed by topic through `UserDirectory.Subscribers(topic)`; no chat id is hardcoded. `technical` carries system and sensor events, `heating` the boiler and temperature events, `gates` the openings and the access window. Admins receive every topic. `BotHostedService.NotifyTopicAsync` and the `NotifyTopic` extension are the only senders.

## Gate access

`Gates:RateLimit` limits non-admin users to `MaxOpenings` per `WindowMinutes`; `admin` and `gate_controller` are never limited. Openings are consumed on confirmation, not on command invocation. While the shared window is open, `GateAccessService` records who used the gates in `db.json`; `GateAccessExpiryDaemon` notifies those users when the window closes.

## Database

Single-file JSON database (`db.json`) written to `AppContext.BaseDirectory` on every `Edit()` call, through a temp file plus atomic replace so a power cut cannot truncate it. Reads and clones on every `Data` access. Serialized with System.Text.Json; unknown fields from older files are ignored. No migrations.

## Crypto trading (removed)

The crypto alerts engine was removed entirely together with its NuGet dependencies: no `CryptoClients.Net`, no exchange SDKs, no trading AI tools, no `/trading` command. Do not reintroduce exchange packages unless the engine comes back as an explicit feature.

## AI usage

`UsageTracker` aggregates input, output and cached input tokens per day in `usage.json` (content root, gitignored), fed by every AI response. Each request's cost is computed when it is recorded, using prices and time multipliers fetched from `{AI:Endpoint}models` every 6 hours by `ModelPricing` (config prices act as fallback). `/status` sums the last 7 days and shows the cost as a percentage of `AI:WeeklyBudgetUsd`.

## Formatting

CSharpier (`.csharpierrc.json`): printWidth 140, tabWidth 4, no tabs.

## Deployment

`install.sh`: run from the repo with `sudo ./install.sh [user]` (defaults to `$SUDO_USER`). It derives all paths from the script location, requires a `.env` with `TELEGRAM_API_KEY` and `ZEN_API_KEY` (creates a template and stops if missing), chowns the repo to `user`, builds as `user`, and writes `poderebot.service` (`Restart=always`, `WorkingDirectory` set) plus the oneshot watchdog (`poderebot-wd.service` + timer).

`patch.sh` is generated by `install.sh` (gitignored). Run it as the repo user or with sudo: it derives its own directory, fetches and hard-resets to `origin/<current branch>`, stops the service, builds as the repo owner, then restarts it and prints the status. Untracked runtime files (`users.json`, `db.json`, `.env`) survive the reset.

Systemd watchdog polls `GET /api/health` every 10 minutes; restarts the service if it fails. It passes `X-Api-Token` when `API_TOKEN` is set in `.env`.

## What is NOT here

- CI runs `dotnet build -c Release` and `dotnet test` on every push (`.github/workflows/ci.yml`)
- No CD: deployment is manual through `install.sh` / `patch.sh`
- No code generation, no migrations, no build artifacts to commit
- No monitoring, no structured logging export
