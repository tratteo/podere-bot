#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
was_running=0

error_handler() {
    echo "error occurred at line $1"
    if [ "${was_running:-0}" -eq 1 ]; then
        systemctl start poderebot.service || true
    fi
    exit 1
}
trap 'error_handler $LINENO' ERR

confirm() {
    read -p "$1 (y/n) [n]: " answer
    case $answer in
        [Yy]* ) return 0;;
        * ) return 1;;
    esac
}

user="${1:-${SUDO_USER:-}}"
if [ -z "$user" ]; then
    echo "usage: sudo ./install.sh [user]"
    exit 1
fi

if [ "$(id -u)" -ne 0 ]; then
    echo "run this script with sudo"
    exit 1
fi

if ! id -u "$user" &> /dev/null; then
    echo "user [$user] does not exist in the current system"
    exit 1
fi

for bin in dotnet systemctl curl git; do
    if ! command -v "$bin" &> /dev/null; then
        echo "missing required command: $bin"
        exit 1
    fi
done

if [ ! -f "$ROOT/PodereBot.csproj" ]; then
    echo "[$ROOT] does not look like the PodereBot repository"
    exit 1
fi

as_user() {
    if [ "$user" = "root" ]; then
        "$@"
    elif command -v sudo &> /dev/null; then
        sudo -u "$user" -H -- "$@"
    else
        runuser -u "$user" -- "$@"
    fi
}

echo - checking secrets
if [ ! -f "$ROOT/.env" ]; then
    printf 'TELEGRAM_API_KEY=\nZEN_API_KEY=\n' > "$ROOT/.env"
    chown "$user":"$user" "$ROOT/.env"
    echo "created $ROOT/.env"
    echo "fill TELEGRAM_API_KEY and ZEN_API_KEY, then run install again"
    exit 1
fi

for key in TELEGRAM_API_KEY ZEN_API_KEY; do
    if ! grep -Eq "^[[:space:]]*(export[[:space:]]+)?${key}[[:space:]]*=[[:space:]]*[^[:space:]]" "$ROOT/.env"; then
        echo "missing or empty $key in $ROOT/.env"
        exit 1
    fi
done

echo - fixing ownership of $ROOT
chown -R "$user":"$user" "$ROOT"

echo - generating scripts
cat > "$ROOT/run.sh" <<'EOF'
#!/bin/bash
cd "$(dirname "$0")/build"
exec ./PodereBot
EOF

cat > "$ROOT/patch.sh" <<'EOF'
#!/bin/bash
set -euo pipefail

REPO_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_USER="$(stat -c '%U' "$REPO_DIR")"

if [ ! -f "$REPO_DIR/.env" ]; then
    echo "missing $REPO_DIR/.env"
    exit 1
fi

if ! command -v systemctl &> /dev/null; then
    echo "systemctl is not available"
    exit 1
fi

if [ "$(id -u)" -eq 0 ]; then
    if command -v sudo &> /dev/null; then
        as_user() { sudo -u "$REPO_USER" -H -- "$@"; }
    else
        as_user() { runuser -u "$REPO_USER" -- "$@"; }
    fi
    sys() { systemctl "$@"; }
else
    sudo -v
    as_user() { "$@"; }
    sys() { sudo systemctl "$@"; }
fi

if ! sys cat poderebot.service &> /dev/null; then
    echo "poderebot.service is not installed, run install.sh first"
    exit 1
fi

cd "$REPO_DIR"

echo - fetching changes
as_user git fetch --all --prune
branch="$(as_user git rev-parse --abbrev-ref HEAD)"
if [ "$branch" = "HEAD" ]; then
    echo "repository is in detached HEAD, checkout a branch first"
    exit 1
fi
as_user git reset --hard "origin/$branch"
echo - now on "$(as_user git log -1 --oneline)"

echo - stopping service
sys stop poderebot.service || true
trap 'sys start poderebot.service || true' EXIT

echo - cleaning old build output
rm -rf "$REPO_DIR/build"

echo - building distribution
as_user dotnet publish -c Release -r linux-arm64 --self-contained false -o "$REPO_DIR/build"

echo - starting service
sys start poderebot.service
trap - EXIT

sys status --no-pager poderebot.service
EOF

chmod +x "$ROOT/run.sh" "$ROOT/patch.sh"
chown "$user":"$user" "$ROOT/run.sh" "$ROOT/patch.sh"

echo - building distribution
if systemctl is-active --quiet poderebot.service 2>/dev/null; then
    was_running=1
    systemctl stop poderebot.service
fi
echo - cleaning old build output
rm -rf "$ROOT/build"
as_user dotnet publish -c Release -r linux-arm64 --self-contained false -o "$ROOT/build"
if [ "$was_running" -eq 1 ]; then
    systemctl start poderebot.service
    was_running=0
fi

# ===== SYSTEMD SERVICE SETUP
if confirm "setup systemd services?"; then
    echo - writing .service file
    cat > /etc/systemd/system/poderebot.service <<EOF
[Unit]
Description=Telegram Podere bot
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
WorkingDirectory=$ROOT/build
ExecStart="$ROOT/run.sh"
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

    echo - writing watchdog .service files
    cat > /etc/systemd/system/poderebot-wd.service <<'EOF'
[Unit]
Description=Telegram Podere bot watchdog service
After=poderebot.service

[Service]
Type=oneshot
ExecStart=/bin/sh -c 'curl -m 5 -f http://localhost:5050/api/status || systemctl restart poderebot.service'

[Install]
WantedBy=multi-user.target
EOF

    cat > /etc/systemd/system/poderebot-wd.timer <<'EOF'
[Unit]
Description=Telegram Podere bot watchdog timer

[Timer]
OnBootSec=1m
OnUnitActiveSec=10m
AccuracySec=1s

[Install]
WantedBy=timers.target
EOF

    echo - reloading daemon
    systemctl daemon-reload
    echo - enabling services
    systemctl enable poderebot.service
    systemctl enable poderebot-wd.service
    systemctl enable poderebot-wd.timer

    if confirm "start systemd services?"; then
        echo - restarting services
        systemctl restart poderebot.service
        systemctl restart poderebot-wd.service
        systemctl restart poderebot-wd.timer
    fi
fi

echo - installation completed
echo "  directory: $ROOT"
echo "  update:    $ROOT/patch.sh"
echo "  logs:      journalctl -u poderebot.service -f"
