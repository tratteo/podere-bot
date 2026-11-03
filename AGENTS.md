# AGENTS.md — PodereBot

## Project

.NET 10 ASP.NET Core bot running on a Raspberry Pi (Linux ARM). Controls gates and heating and reports temperatures through Telegram, with an AI assistant, an MCP endpoint and a local touch panel. Single-project with a small xUnit test project and a GitHub Actions workflow.

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

`/adminpanel` is the admin panel: it lists every configured user as a clickable Telegram contact with their entitlements and topic subscriptions. It adds a user by typed id or the `KeyboardButtonRequestUsers` picker, toggles the `admin` and `gate_controller` entitlements, toggles the `technical`, `heating` and `gates` topics, and removes a user with confirmation. Buttons are action-oriented (`➕` grants, `➖` revokes); topic subscriptions are explicit for every user, admins included; a new admin starts with all three and can remove them. `UserDirectory` resolves the effective set as the `Users` seed in `appsettings.json` merged with the gitignored `users.json`, minus the ids listed under `Removed`, so runtime changes survive `patch.sh` and rebuilds. A legacy `admins.json` is migrated once at startup and then deleted. Removing or demoting the last admin is refused. `PendingInputRegistry` keeps the AI fallback from answering the typed id.

Interactive commands use `AttachEvents()`/`DetachEvents()` pattern for inline keyboard callbacks. Messages queued for deletion via `DeleteOnDetach()` are cleaned up on detach.

`/myinfo` shows the sender's Telegram profile and forwards it to `Telegram:OwnerId` (fallback: the first admin), never to an id derived from the sender.

Bot language is **Italian**.

## REST API

All endpoints are restricted to local network (192.168.x.x, 127.0.0.1). Binds to `http://0.0.0.0:5050`.

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/status` | Process uptime + memory |
| GET | `/api/health` | Core health check: HTTP and database writability. Returns 200 while the core is healthy and reports Telegram as `telegramOk`; the systemd watchdog calls this |
| GET | `/api/temperatures` | Local probe, external readings and the operational average |
| GET | `/api/heating` | Operative temperature, boiler state, program intervals, antifreeze threshold |
| GET | `/api/gates` | Access window, users who had access, rate limit settings |
| POST | `/api/temperature` | Accept external sensor readings (missing `timestamp` falls back to the server UTC clock) |

API endpoints auto-register via `IEndpoint` reflection (same pattern as commands). `install.sh` generates `API_TOKEN` into `.env` when missing, so on a real deployment every `/api/*` and `/mcp` request must send it in the `X-Api-Token` header (compared in constant time); the watchdog reads the same value. Clearing the value disables the requirement and leaves the local-network restriction as the only guard.

## Touch panel

Three Kestrel ports: `0.0.0.0:5050` for the REST API and MCP, `127.0.0.1:5051` for the kiosk panel and `0.0.0.0:5052` for the LAN panel (`Panel:LanPort`). `/panel` answers 404 on 5050 and is unreachable from the LAN on 5051. `wwwroot/panel` is served by the bot itself; `config.js` carries the machine token only on 5051.

Devices are authorised per device, never by client IP, MAC or hostname. The kiosk (or `panel-pair.sh`, which uses the token on 5051) creates a device name and a single-use link, returns it once with a server-generated QR SVG (`QRCoder`), and stores only the SHA-256 hash. Redeeming the link on 5052 sets the `poderebot_panel` cookie (HttpOnly, SameSite=Strict, `Panel:SessionDays`); every request is checked against the stored key hash. Revoking a device deletes it from the list, frees its name and invalidates the cookie. Writes on 5052 also require `X-Panel: 1` and a same-origin `Origin`. The QR and the cookie travel in clear text: LAN HTTP is a testing choice, production needs HTTPS or Tailscale.

`Panel:LanHost` must be a stable Pi address or name (`poderebot.local`), because the cookie is bound to that origin. Without it the first non-loopback IPv4 is used, preferring `192.168.x`.

User names in the panel come from `users.json` when set, otherwise from Telegram (`GetChat`, cached for a day). The pencil on a user card edits the stored name; clearing it falls back to Telegram.

`panel.db` (SQLite, `Microsoft.Data.Sqlite`, WAL) holds `temperature_hourly`, `events` and `panel_devices`. `TemperatureHistoryRecorder` samples the operational temperature every `Panel:History:SampleMinutes` (5), folds it into the UTC hour with average/min/max/samples, and prunes history to `RetentionDays` (365) daily. `NotificationLog` records every notification before the Telegram send and keeps `Panel:Events:MaxEntries` (500) entries for `RetentionDays` (30). Panel actions are audit events with topic `panel`.

The panel UI is monochrome (white on black or black on white); colour is reserved for icons, states and badges. Theme (`Automatico`/`Scuro`/`Chiaro`), animations (`Complete`/`Ridotte`) and the idle screen delay are per-device preferences in `localStorage` (`pb.theme`, `pb.motion`, `pb.idle`), set in Sistema › Aspetto. `app.js` morphs the view in place on every poll instead of replacing it, so transitions and running animations survive; elements marked `data-keep` belong to script (the animated temperature). Every write goes through `optimistic()`: it snapshots the overview, applies the change locally, shows a loading toast, then reconciles with the server overview or, on failure, restores the snapshot and refreshes. The blob behind the temperature is two radial gradients animated only with `transform` (no `filter: blur`), coloured by the operative temperature: flat blue up to 10 °C, then 15, 18, 20, 24, 28 °C stops.

Outdoor weather comes from Open-Meteo (`WeatherService`, no key) for the `Home` coordinates, refreshed every `Weather:RefreshMinutes` (15) and exposed as `weather` in the panel overview; a failed refresh keeps the last reading. It is display only: the `Presente` and `/temperatures` report it as `Esterno (Open-Meteo)` and the panel lists it under Sistema › Sensori, but it never enters the operational temperature or the heating logic. `/sendpos` reads the same `Home` section. A future outdoor sensor should take precedence over it.

`db.json` stores `GatesOpenAccessStartDate` next to the expiration, set by every writer of the window (panel, `/gates`, AI tool) and cleared with it, so the panel can draw the remaining-time bar.

Panel actions act as `PanelUserId = 0` ("Pannello"): admin-like, never rate limited, able to open while the gates are locked, and excluded from the gate expiry notifications.

`poderebot-kiosk.service` runs `panel.sh` (generated by `install.sh`), which waits for 5051 and then starts `cage -- cog` on the HDMI screen. It is `BindsTo` and `WantedBy` `poderebot.service`, so it stops and restarts with the bot. `install.sh` requires `cage` and `cog`.

## Heating

Heating program format: `hh:mm-hh:mm@temp/hh:mm-hh:mm@temp` (24h). `HeatingProgram.TryBuild()` validates intervals are non-overlapping, sorted, and that each interval starts before it ends (an interval crossing midnight is refused rather than silently never matching). `HeatingProgramDaemon` polls every `PollIntervalSeconds` and applies temperature hysteresis with `ToleranceSeconds` delay (600 s in production, `MonitorIntervalSeconds` 10). While an interval is active and the program is not suspended the program owns the boiler: the panel disables the manual buttons and both the API and the Telegram callback refuse the switch. Outside it, the panel and the command announce the change immediately through `BoilerNotifier`, and the monitor skips a state announced in the last 90 seconds.

Antifreeze (`Heating:Antifreeze`) runs before the program: below `ThresholdCelsius` (default 5 °C) the boiler is forced on regardless of the program, of its suspension and of a manual switch-off, and it releases only above the threshold plus 0.5 °C of hysteresis. Antifreeze notifications are rate-limited by `Heating:Antifreeze:NotifyCooldownMinutes` (default 15), and boiler on/off notifications are suppressed when the change is antifreeze-driven. The monitor treats its first observation of boiler, temperature, local sensor and external sensors as a baseline, so the boot state is reported only by the `Presente` summary.

`HeatingMonitorDaemon` only observes and notifies, never actuates: boiler state changes, a boiler running longer than `Heating:BoilerAlert:MaxContinuousHours`, the operative temperature leaving `Heating:TemperatureBand`, and stale external sensors all produce topic notifications with one message per episode.

## Notifications

Every notification is routed by topic through `UserDirectory.Subscribers(topic)`; no chat id is hardcoded. `technical` carries system and sensor events, `heating` the boiler and temperature events, `gates` the openings and the access window. Subscriptions are explicit for every user, admins included. `BotHostedService.NotifyTopicAsync` and the `NotifyTopic` extension are the only senders.

On boot `BotHostedService` sends a single `Presente` summary with boiler, operative/local temperatures, external sensors, the Open-Meteo outdoor temperature, program, gate window and warnings, instead of the per-daemon startup messages. `StartupGate` silences the monitor until the summary is sent and suppresses a second summary when the process restarts within 10 minutes; `offline` is sent only when the process ran longer than 10 minutes.

## Gate access

`Gates:RateLimit` limits non-admin users to `MaxOpenings` per `WindowMinutes`; `admin` and `gate_controller` are never limited. Openings are consumed on confirmation, not on command invocation. While the shared window is open, `GateAccessService` records who used the gates in `db.json`; `GateAccessExpiryDaemon` notifies those users when the window closes.

Every opening made with `/openauto` or `/openped` is recorded as a `gates` event, whoever did it. The `gates` topic is notified for non-admin openings, `gate_controller` included; admin openings stay silent on Telegram because the admin knows, but they still land in the panel history.

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

`patch.sh` is generated by `install.sh` (gitignored). Run it as the repo user or with sudo: it derives its own directory, fetches and hard-resets to `origin/<current branch>`, stops the service, builds as the repo owner, then restarts it and prints the status. Untracked runtime files (`users.json`, `db.json`, `.env`) survive the reset. It follows the current branch, so it can deploy unmerged work; `update.sh` follows `release` only.

Systemd watchdog polls `GET /api/health` every 10 minutes; restarts the service if it fails. It passes `X-Api-Token` when `API_TOKEN` is set in `.env`.

`uninstall.sh` removes the six systemd units with their drop-ins, the updater sudoers rule, the generated scripts and `build/`. `--purge` also deletes `.env` and the runtime files, `--delete-repo` removes the checkout, `--dry-run` prints the actions. It never removes `cage`, `cog` or the system user.

## Runtime state location

`db.json`, `users.json`, `usage.json`, `panel.db` and `startup.json` live at `IHostEnvironment.ContentRootPath`, which is the repository root because `run.sh` starts the app from there. The first start migrates a copy found inside the publish folder (`build/`), so replacing the publish folder on update never destroys runtime state.

## Auto update

`release` is a fast-forward-only branch. The CI job `promote-release` moves it to the verified `main` commit only after the Release build and the tests pass, and refuses when `release` contains commits that are not in `main`. The workflow runs on pushes to `main` and on pull requests, so pushing `release` does not re-trigger it.

`install.sh` generates `update.sh` (gitignored, like `patch.sh`), the `poderebot-update.service` + `poderebot-update.timer` pair (default every 10 minutes, `UPDATE_INTERVAL` in `.env`, e.g. `15min`), `/etc/sudoers.d/020-poderebot-update` restricted to the `systemctl stop|start|restart` calls the updater needs, and `.install-state` with the fingerprints of `install.sh` and of the installed units, kiosk included.

The updater takes a `flock` and is a no-op when `HEAD` equals `origin/release`. It refuses when the worktree is dirty, when `install.sh` or the unit files differ from `.install-state` (a manual `install.sh` run is required), or when free space is below `UPDATE_MIN_FREE_MB` (default 1024). It builds into `build.next` while the service keeps running, stops the watchdog timer, swaps `build` with `build.old`, restarts the service and waits for `/api/health` with the token from `.env`; on timeout it restores `build.old`, resets the repository to the previous SHA and restarts. A failed SHA is recorded in `update-state.json` and never retried. `update.sh --check` only reports the SHAs. Nothing reboots the Pi.

## What is NOT here

- CI runs `dotnet build -c Release` and `dotnet test` on every push (`.github/workflows/ci.yml`)
- No CD: deployment is manual through `install.sh` / `patch.sh`
- No code generation, no migrations, no build artifacts to commit
- No monitoring, no structured logging export
