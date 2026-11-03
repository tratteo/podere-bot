# Runs PodereBot locally with seeded sample data for the panel.
# No Telegram messages are sent: the bot token is fake. Press Ctrl+C in this window
# or stop the printed PID when you are done.
#
#   pwsh scripts/demo.ps1
#   pwsh scripts/demo.ps1 -Stop
param(
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent $PSScriptRoot
$Demo = Join-Path $env:TEMP 'poderebot-demo'
$PidFile = Join-Path $Demo 'app.pid'

if ($Stop) {
    if (Test-Path $PidFile) {
        $old = Get-Content $PidFile
        Stop-Process -Id $old -Force -ErrorAction SilentlyContinue
        Remove-Item $PidFile -Force
        "Demo fermata (pid $old)."
    } else {
        'Nessuna demo in esecuzione.'
    }
    exit 0
}

$dll = Join-Path $Repo 'bin\Release\net10.0\PodereBot.dll'
if (-not (Test-Path $dll)) {
    throw "Build mancante: esegui 'dotnet build -c Release' prima della demo."
}
foreach ($port in 5050, 5051, 5052) {
    $busy = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    if ($busy) { throw "La porta $port è già in uso: ferma l'altra istanza." }
}

if (Test-Path $Demo) { Remove-Item -Recurse -Force $Demo }
New-Item -ItemType Directory -Path $Demo -Force | Out-Null
Copy-Item (Join-Path $Repo 'appsettings.json') $Demo
Copy-Item -Recurse (Join-Path $Repo 'wwwroot') (Join-Path $Demo 'wwwroot')
Copy-Item (Join-Path $PSScriptRoot 'seed-demo.py') $Demo
python (Join-Path $Demo 'seed-demo.py')

$sha = (git -C $Repo rev-parse HEAD 2>$null)
if (-not $sha) { $sha = 'locale' }
$now = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
@"
{
  "lastRunUtc": "$now",
  "result": "noop",
  "fromSha": "$sha",
  "toSha": "$sha",
  "failedSha": "",
  "reason": "already at $sha"
}
"@ | Set-Content (Join-Path $Demo 'update-state.json') -NoNewline
"{`"sha`":`"$sha`",`"builtAtUtc`":`"$now`",`"source`":`"demo`"}" |
    Set-Content (Join-Path $Demo 'version.json') -NoNewline

$token = '123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw'
@"
TELEGRAM_API_KEY=$token
ZEN_API_KEY=invalid
"@ | Set-Content (Join-Path $Demo '.env') -NoNewline

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:TELEGRAM_API_KEY = $token
$env:ZEN_API_KEY = 'invalid'
$p = Start-Process -FilePath 'dotnet' -ArgumentList @($dll) -WorkingDirectory $Demo `
    -RedirectStandardOutput (Join-Path $Demo 'app.log') -RedirectStandardError (Join-Path $Demo 'app.err.log') `
    -PassThru -NoNewWindow
$p.Id | Set-Content $PidFile

$ready = $false
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    try {
        if ((Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 'http://127.0.0.1:5051/panel/').StatusCode -eq 200) {
            $ready = $true
            break
        }
    } catch {}
}

if (-not $ready) {
    "La demo non è partita. Log: $Demo\app.log"
    Get-Content (Join-Path $Demo 'app.log') -ErrorAction SilentlyContinue | Select-Object -Last 15
    exit 1
}

"Demo pronta: http://127.0.0.1:5051/panel/#heating"
"PID: $($p.Id)  (ferma con: pwsh scripts/demo.ps1 -Stop)"
"Dati demo: $Demo"
