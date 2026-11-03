# AGENTS.md — PodereBot

## Project

.NET 10 ASP.NET Core bot running on a Raspberry Pi (Linux ARM). Controls gates and heating and reports temperatures through Telegram, with an AI assistant, an MCP endpoint and a local touch panel. Single-project with a small xUnit test project and a GitHub Actions workflow.

## Build & run

```powershell
dotnet build
dotnet run                    # uses mock GPIO drivers in Development
dotnet build /p:OutputPath=build   # production-style build (install.sh publishes into build.next)
```

`TELEGRAM_API_KEY` must be set (loaded from `.env` file at startup via DotNetEnv).

The Telegram.Bot SDK comes from a custom Azure DevOps feed (see `PodereBot.csproj` `RestoreSources`).

`pwsh scripts/demo.ps1` runs the panel on 5051 with a temporary content root, a seeded `panel.db` (local probe plus three sample sensors), a fake Telegram token and sample version metadata. `-Stop` stops it. Nothing is sent to Telegram.

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

`/adminpanel` is the admin panel: it lists every configured user as a clickable Telegram contact with their entitlements and topic subscriptions. It adds a user by typed id or the `KeyboardButtonRequestUsers` picker, toggles the `admin` and `gate_controller` entitlements, toggles the `technical`, `heating` and `gates` topics, and removes a user with confirmation. Buttons are action-oriented (`➕` grants, `➖` revokes); topic subscriptions are explicit for every user, admins included; a new admin starts with all three and can remove them. `UserDirectory` resolves the effective set as the `Users` seed in `appsettings.json` merged with the gitignored `users.json`, minus the ids listed under `Removed`, so runtime changes survive `install.sh` and rebuilds. A legacy `admins.json` is migrated once at startup and then deleted. Removing or demoting the last admin is refused. `PendingInputRegistry` keeps the AI fallback from answering the typed id.

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
| POST | `/api/temperature` | Accept external sensor readings, or an error report with `temperature: null`, `status` and `error` (missing `timestamp` falls back to the server UTC clock) |

API endpoints auto-register via `IEndpoint` reflection (same pattern as commands). `install.sh` generates `API_TOKEN` into `.env` when missing, so on a real deployment every `/api/*` and `/mcp` request must send it in the `X-Api-Token` header (compared in constant time); the watchdog reads the same value. Clearing the value disables the requirement and leaves the local-network restriction as the only guard.

## Touch panel

Three Kestrel ports: `0.0.0.0:5050` for the REST API and MCP, `127.0.0.1:5051` for the kiosk panel and `0.0.0.0:5052` for the LAN panel (`Panel:LanPort`). `/panel` answers 404 on 5050 and is unreachable from the LAN on 5051. `wwwroot/panel` is served by the bot itself; `config.js` carries the machine token only on 5051.

Devices are authorised per device, never by client IP, MAC or hostname. The kiosk (or `panel-pair.sh`, which uses the token on 5051) creates a device name and a single-use link, returns it once with a server-generated QR SVG (`QRCoder`), and stores only the SHA-256 hash. Redeeming the link on 5052 sets the `poderebot_panel` cookie (HttpOnly, SameSite=Strict, `Panel:SessionDays`); every request is checked against the stored key hash. Revoking a device deletes it from the list, frees its name and invalidates the cookie. Writes on 5052 also require `X-Panel: 1` and a same-origin `Origin`. The QR and the cookie travel in clear text over HTTP: use HTTPS or Tailscale on a real network. `wwwroot/panel/manifest.json` with `icon-192.png`, `icon-512.png` and `icon-maskable-512.png` makes the panel installable as an app, but Chrome only offers a real install on `localhost` or HTTPS: on a plain HTTP LAN address it creates a shortcut and takes the icon from the favicon, so the PNG fallbacks matter.

`Panel:LanHost` must be a stable Pi address or name (`poderebot.local`), because the cookie is bound to that origin. Without it the first non-loopback IPv4 is used, preferring `192.168.x`.

User names in the panel come from `users.json` when set, otherwise from Telegram (`GetChat`, cached for a day). The pencil on a user card edits the stored name; clearing it falls back to Telegram.

`panel.db` (SQLite, `Microsoft.Data.Sqlite`, WAL) holds `temperature_hourly`, `sensor_hourly`, `events` and `panel_devices`. `TemperatureHistoryRecorder` samples the operational temperature every `Panel:History:SampleMinutes` (5), folds it into the UTC hour with average/min/max/samples, and also stores one bucket per sensor (local probe and fresh external readings) in `sensor_hourly`; it prunes history to `RetentionDays` (365) daily. The heating chart draws the operational average plus one colored line per sensor, selected with the series picker, smoothed with monotone cubic interpolation, 200 px tall and with a dot on every real sample; hovering or dragging on the plot shows a vertical cursor that follows the pointer with easing and a tooltip with the time and the value of every visible series. `NotificationLog` records every notification before the Telegram send and keeps `Panel:Events:MaxEntries` (500) entries for `RetentionDays` (30). Panel actions are audit events with topic `panel`.

The panel UI is monochrome (white on black or black on white); colour is reserved for icons, states and badges. Theme (`Automatico`/`Scuro`/`Chiaro`), animations (`Complete`/`Ridotte`) and the idle screen delay are per-device preferences in `localStorage` (`pb.theme`, `pb.motion`, `pb.idle`), set in Sistema › Aspetto. `app.js` morphs the view in place on every poll instead of replacing it, so transitions and running animations survive; elements marked `data-keep` belong to script (the animated temperature). The hero shows the operational average with a `N sorgenti` badge counting the local probe and the fresh non-informational external sensors. Every write goes through `optimistic()`: it snapshots the overview, applies the change locally, shows a loading toast, then reconciles with the server overview or, on failure, restores the snapshot and refreshes. The `Sistema › Sensori` badge counts the local probe status and the stale or failed external sensors, so a local error never reads `Tutti ok`. The blob behind the temperature is two radial gradients animated only with `transform` (no `filter: blur`), coloured by the operative temperature: flat blue up to 10 °C, then 15, 18, 20, 24, 28 °C stops.

Outdoor weather comes from Open-Meteo (`WeatherService`, no key) for the `Home` coordinates, refreshed every `Weather:RefreshMinutes` (15) and exposed as `weather` in the panel overview; a failed refresh keeps the last reading. It is display only: `/temperatures` reports it as `Esterno (Open-Meteo)` with temperature and humidity, and the panel lists it under Sistema › Sensori, but it never enters the operational temperature or the heating logic. `/sendpos` reads the same `Home` section. A future outdoor sensor should take precedence over it.

`db.json` stores `GatesOpenAccessStartDate` next to the expiration, set by every writer of the window (panel, `/gates`, AI tool) and cleared with it, so the panel can draw the remaining-time bar.

Panel actions act as `PanelUserId = 0` ("Pannello"): admin-like, never rate limited, able to open while the gates are locked, and excluded from the gate expiry notifications.

`poderebot-kiosk.service` runs `panel.sh` (generated by `install.sh`), which waits for 5051 and then starts `cage -- cog` on the HDMI screen. It is `BindsTo` and `WantedBy` `poderebot.service`, so it stops and restarts with the bot. `install.sh` requires `cage` and `cog`.

## Heating

Heating program format: `hh:mm-hh:mm@temp/hh:mm-hh:mm@temp` (24h). `HeatingProgram.TryBuild()` validates intervals are non-overlapping, sorted, and that each interval starts before it ends (an interval crossing midnight is refused rather than silently never matching). `HeatingProgramDaemon` polls every `PollIntervalSeconds` and applies temperature hysteresis with `ToleranceSeconds` delay (600 s in production, `MonitorIntervalSeconds` 10). While an interval is active and the program is not suspended the program owns the boiler: the panel disables the manual buttons and both the API and the Telegram callback refuse the switch. Outside it, the panel and the command announce the change immediately through `BoilerNotifier`, and the monitor skips a state announced in the last 90 seconds.

Antifreeze (`Heating:Antifreeze`) runs before the program: below `ThresholdCelsius` (default 5 °C) the boiler is forced on regardless of the program, of its suspension and of a manual switch-off, and it releases only above the threshold plus 0.5 °C of hysteresis. Antifreeze notifications are rate-limited by `Heating:Antifreeze:NotifyCooldownMinutes` (default 15), and boiler on/off notifications are suppressed when the change is antifreeze-driven. The monitor treats its first observation of boiler, temperature, local sensor and external sensors as a baseline, so the boot state is not re-announced.

`HeatingMonitorDaemon` only observes and notifies, never actuates: boiler state changes, a boiler running longer than `Heating:BoilerAlert:MaxContinuousHours`, the operative temperature leaving `Heating:TemperatureBand`, and stale external sensors all produce topic notifications with one message per episode. The stale and recovery window is `Heating:SensorAlertMinutes` (10 minutes), independent from the 60-second freshness (`TempSensorsToleranceSeconds`) used by the operational average. External sensors can also post an error report (`temperature: null`, `status`, `error`); it is informational, excluded from the average, shown as an error in the panel sensors and announced once per episode.

## Notifications

Every notification is routed by topic through `UserDirectory.Subscribers(topic)`; no chat id is hardcoded. `technical` carries system and sensor events, `heating` the boiler and temperature events, `gates` the openings and the access window. Subscriptions are explicit for every user, admins included. `BotHostedService.NotifyTopicAsync` and the `NotifyTopic` extension are the only senders.

On boot `BotHostedService` sends `Ce sto` immediately, before the slower Telegram setup. It sends it again after a Telegram outage of at least `heartbeatTimeout` (three heartbeats); `me so scocciato, me dò` is sent on shutdown when the process ran longer than 10 minutes. `StartupGate` only keeps the monitor quiet until the setup finishes.

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

`install.sh` is the single deployment command: `sudo ./install.sh [--auto|--check|--dry-run] [--branch NAME|--ref SHA] [--no-pull] [--force] [--user NAME]`. It defaults to `release`, fetches and resets, re-executes itself when the release changed `install.sh`, builds fresh into `build.next` as the repository user, installs units, scripts and sudoers, writes `.install-state`, stops the service once, swaps `build.next` into `build`, starts once, waits for `/api/health` and rolls back to `build.old` on failure. Runtime files are preserved. `--no-pull` builds the current checkout; `--force` discards dirty tracked changes; a dirty worktree is refused otherwise. `patch.sh` no longer exists.

Systemd watchdog polls `GET /api/health` every 10 minutes; restarts the service if it fails. It passes `X-Api-Token` when `API_TOKEN` is set in `.env`.

`uninstall.sh` removes the six systemd units with their drop-ins, the updater sudoers rule, the generated scripts and `build/`. `--purge` also deletes `.env` and the runtime files, `--delete-repo` removes the checkout, `--dry-run` prints the actions. It never removes `cage`, `cog` or the system user.

## Runtime state location

`db.json`, `users.json`, `usage.json` and `panel.db` live at `IHostEnvironment.ContentRootPath`, which is the repository root because `run.sh` starts the app from there. The first start migrates a copy found inside the publish folder (`build/`), so replacing the publish folder on update never destroys runtime state.

## Auto update

`release` is a fast-forward-only branch. The CI job `promote-release` moves it to the verified `main` commit only after the Release build and the tests pass, and refuses when `release` contains commits that are not in `main`. The workflow runs on pushes to `main` and on pull requests, so pushing `release` does not re-trigger it.

`install.sh` installs `poderebot-update.service` + `poderebot-update.timer`, which runs `install.sh --auto` every `UPDATE_INTERVAL` (default 2 minutes) as root, plus `/etc/sudoers.d/020-poderebot-update` and `.install-state` with the fingerprints of `install.sh` and the installed units.

`--auto` fetches `release`, re-executes the new `install.sh` when it changed, installs changed units and sudoers, and builds only when the running build SHA (`version.json`) or the installation differs from the target. A no-op run still writes `lastRunUtc` in `update-state.json`, so `Ultima verifica` in the panel always moves. `version.json` carries the built SHA and UTC build time; `UpdateStateStore` reads it first from `AppContext.BaseDirectory` and then from the content root, falling back to the camelCase `update-state.json`. The panel shows the SHA as `Versione`, the build time as `Aggiornato` and the last run as `Ultima verifica`.

The deploy core is the generated `update.sh` (called with `--no-lock` while `install.sh` holds the lock). It builds beside the running service, stops the watchdog timer, swaps `build` with `build.old`, restarts, checks `/api/health` and rolls back the build and SHA on failure. A failed SHA is recorded and not retried automatically; the manual `install.sh` forces a retry. `install.sh --check` reports running and target SHAs and writes the check date.

## What is NOT here

- CI runs `dotnet build -c Release` and `dotnet test` on every push (`.github/workflows/ci.yml`)
- No CD: deployment is through `install.sh` (manual or timer `--auto`)
- No code generation, no migrations, no build artifacts to commit
- No monitoring, no structured logging export
