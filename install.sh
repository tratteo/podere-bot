#!/bin/bash
set -euo pipefail
export GIT_TERMINAL_PROMPT=0
trap 'rc=$?; echo "install.sh: [$BASH_COMMAND] failed at line $LINENO (exit $rc)" >&2' ERR

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ORIG_ARGS=("$@")
MODE=manual
BRANCH=release
REF=
NO_PULL=0
FORCE=0
USER_ARG=

while [ $# -gt 0 ]; do
    case "$1" in
        --auto) MODE=auto ;;
        --check) MODE=check ;;
        --dry-run) MODE=dry ;;
        --branch) BRANCH="${2:?}"; shift ;;
        --ref) REF="${2:?}"; shift ;;
        --user) USER_ARG="${2:?}"; shift ;;
        --no-pull) NO_PULL=1 ;;
        --force) FORCE=1 ;;
        --yes) : ;;
        --resumed) : ;;
        -h|--help)
            echo "usage: sudo ./install.sh [--auto|--check|--dry-run] [--branch NAME|--ref SHA] [--no-pull] [--force] [--user NAME]"
            exit 0
            ;;
        --*) echo "unknown option: $1"; exit 1 ;;
        *) USER_ARG="$1" ;;
    esac
    shift
done

user="${USER_ARG:-${SUDO_USER:-}}"
if [ -z "$user" ] && [ "$(id -u)" -eq 0 ]; then
    user="$(stat -c '%U' "$ROOT" 2>/dev/null || true)"
fi
if [ -z "$user" ]; then
    echo "usage: sudo ./install.sh [user]"
    exit 1
fi
if [ "$(id -u)" -ne 0 ]; then
    echo "run this script with sudo"
    exit 1
fi
if ! id -u "$user" &>/dev/null; then
    echo "user [$user] does not exist in the current system"
    exit 1
fi
for bin in dotnet systemctl curl git; do
    if ! command -v "$bin" &>/dev/null; then
        echo "missing required command: $bin"
        exit 1
    fi
done
for bin in cage cog; do
    if ! command -v "$bin" &>/dev/null; then
        echo "missing the kiosk browser: sudo apt install cage cog"
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
    elif [ "$(id -u)" -eq 0 ]; then
        if command -v runuser >/dev/null 2>&1; then
            runuser -u "$user" -- "$@"
        else
            /usr/sbin/runuser -u "$user" -- "$@"
        fi
    elif command -v sudo &>/dev/null; then
        sudo -n -u "$user" -H -- "$@"
    else
        runuser -u "$user" -- "$@"
    fi
}

fail() {
    echo "error: $*" >&2
    exit 1
}

sanitize() {
    printf '%s' "${1//\"/\'}" | tr '\n' ' '
}

write_state() {
    local result="$1" from="$2" to="$3" reason="$4" failed="${5:-}"
    {
        printf '{\n'
        printf '  "lastRunUtc": "%s",\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        printf '  "result": "%s",\n' "$result"
        printf '  "fromSha": "%s",\n' "$from"
        printf '  "toSha": "%s",\n' "$to"
        printf '  "failedSha": "%s",\n' "$failed"
        printf '  "reason": "%s"\n' "$(sanitize "$reason")"
        printf '}\n'
    } > "$ROOT/update-state.json.tmp" 2>/dev/null && mv "$ROOT/update-state.json.tmp" "$ROOT/update-state.json"
    chown "$user":"$user" "$ROOT/update-state.json" 2>/dev/null || true
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
echo - checking API token
if ! grep -Eq "^[[:space:]]*(export[[:space:]]+)?API_TOKEN[[:space:]]*=[[:space:]]*[^[:space:]]" "$ROOT/.env"; then
    api_token=$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')
    printf 'API_TOKEN=%s\n' "$api_token" >> "$ROOT/.env"
    chown "$user":"$user" "$ROOT/.env"
    echo "generated API_TOKEN in $ROOT/.env (required by /api and /mcp)"
fi

self_hash="$(sha256sum "$ROOT/install.sh" | cut -d' ' -f1)"
echo - checking the deploy lock
exec 9>"$ROOT/.update.lock" || fail "cannot open the deploy lock"
if ! flock -n 9; then
    echo "another deploy is running, exiting"
    exit 0
fi

cd "$ROOT"
local_sha="$(as_user git rev-parse HEAD 2>/dev/null || true)"
[ -n "$local_sha" ] || fail "not a git repository"

if [ "$NO_PULL" -eq 0 ]; then
    echo - fetching origin
    as_user git fetch --quiet origin || fail "git fetch origin failed"
    if [ -n "$REF" ]; then
        target_ref="$REF"
    else
        target_ref="origin/$BRANCH"
    fi
    target_sha="$(as_user git rev-parse "$target_ref^{commit}" 2>/dev/null || true)"
    [ -n "$target_sha" ] || fail "target $target_ref not found"
    if [ "$MODE" != check ] && [ "$MODE" != dry ]; then
        if [ "$FORCE" -eq 0 ]; then
            if ! as_user git diff --quiet || ! as_user git diff --cached --quiet; then
                fail "working tree is dirty; commit or stash, or use --no-pull"
            fi
        fi
        echo - resetting to ${target_sha:0:12}
        as_user git reset --hard "$target_sha" >/dev/null 2>&1 || fail "git reset to $target_sha failed"
    fi
else
    target_sha="$local_sha"
    target_ref="HEAD"
fi

if [ "$(sha256sum "$ROOT/install.sh" | cut -d' ' -f1)" != "$self_hash" ]; then
    flock -u 9 || true
    exec 9>&-
    exec "$ROOT/install.sh" --resumed "${ORIG_ARGS[@]}"
fi

build_sha=""
if [ -f "$ROOT/build/version.json" ]; then
    build_sha="$(sed -n 's/.*"sha"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$ROOT/build/version.json" | head -n1)"
fi
install_hash="$(sha256sum "$ROOT/install.sh" | cut -d' ' -f1)"
recorded_install=""
if [ -f "$ROOT/.install-state" ]; then
    recorded_install="$(sed -n 's/^install_sha=//p' "$ROOT/.install-state" | head -n1)"
fi
units_changed=0
if [ -z "$recorded_install" ] || [ "$recorded_install" != "$install_hash" ]; then
    units_changed=1
fi

if [ "$MODE" = check ]; then
    echo "running: ${build_sha:-unknown}"
    echo "target:  $target_sha ($target_ref)"
    if [ "$build_sha" = "$target_sha" ] && [ "$units_changed" -eq 0 ]; then
        write_state "noop" "$build_sha" "$target_sha" "already at $target_sha"
        echo "no update"
    else
        write_state "available" "$build_sha" "$target_sha" "update available"
        echo "update available"
    fi
    exit 0
fi

if [ "$MODE" = dry ]; then
    echo "working tree: $local_sha"
    echo "target:       $target_sha ($target_ref)"
    echo "running:      ${build_sha:-unknown}"
    echo "units:        $([ "$units_changed" -eq 1 ] && echo changed || echo unchanged)"
    echo "would build into build.next, install units, stop, swap and start once"
    exit 0
fi

if [ "$MODE" = auto ] && [ "$build_sha" = "$target_sha" ] && [ "$units_changed" -eq 0 ]; then
    write_state "noop" "$build_sha" "$target_sha" "already at $target_sha"
    exit 0
fi

if [ "$MODE" != auto ]; then
    echo - fixing ownership of $ROOT
    chown -R "$user":"$user" "$ROOT"
fi

echo - generating scripts
cat > "$ROOT/run.sh" <<'EOF'
#!/bin/bash
# Run from the repository root: the content root holds .env, db.json, users.json, usage.json
# and appsettings.json, which must not be replaced together with the publish folder.
cd "$(dirname "$0")"
exec ./build/PodereBot
EOF

cat > "$ROOT/panel.sh" <<'EOF'
#!/bin/bash
# Kiosk launcher: waits for the panel on loopback, then hands the screen to cage + cog.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
URL="http://127.0.0.1:5051/panel/"

for _ in $(seq 1 90); do
    if curl -fsS -o /dev/null --max-time 2 "$URL"; then
        break
    fi
    sleep 1
done

exec cage -- cog -P wl --scale=1 --device-scale=1 --webprocess-failure=restart -b '#0a0e0d' "$URL"
EOF

cat > "$ROOT/panel-pair.sh" <<'EOF'
#!/bin/bash
# Creates the first panel device without the HDMI screen and prints its single-use link.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
NAME="${1:-}"

if [ -z "$NAME" ]; then
    echo "usage: ./panel-pair.sh \"nome dispositivo\""
    exit 1
fi

TOKEN=$(sed -n 's/^API_TOKEN=//p' "$ROOT/.env" 2>/dev/null | head -n1)
ESCAPED=$(printf '%s' "$NAME" | sed 's/\\/\\\\/g; s/"/\\"/g')
BODY=$(printf '{"name":"%s"}' "$ESCAPED")

if [ -n "$TOKEN" ]; then
    RESPONSE=$(curl -fsS -X POST -H "Content-Type: application/json" -H "X-Api-Token: $TOKEN" -d "$BODY" http://127.0.0.1:5051/panel/api/devices/pair)
else
    RESPONSE=$(curl -fsS -X POST -H "Content-Type: application/json" -d "$BODY" http://127.0.0.1:5051/panel/api/devices/pair)
fi

URL=$(printf '%s' "$RESPONSE" | sed -n 's/.*"url":"\([^"]*\)".*/\1/p')
EXPIRES=$(printf '%s' "$RESPONSE" | sed -n 's/.*"expiresAt":"\([^"]*\)".*/\1/p')

if [ -z "$URL" ]; then
    echo "pairing failed: $RESPONSE"
    exit 1
fi

echo "dispositivo: $NAME"
echo "link monouso: $URL"
if [ -n "$EXPIRES" ]; then
    echo "scade: $EXPIRES"
fi
echo "apri il link sul dispositivo: il QR non viene mostrato da questo script."
EOF

cat > "$ROOT/update.sh" <<'EOF'
#!/bin/bash
# Deploy core for PodereBot. Generated by install.sh, do not edit by hand.
# Builds beside the running service, swaps, restarts and rolls back on failure.
set -uo pipefail
export GIT_TERMINAL_PROMPT=0

ROOT="$(cd "$(dirname "$0")" && pwd)"
STATE="$ROOT/update-state.json"
LOCK_FILE="$ROOT/.update.lock"
BRANCH="${UPDATE_BRANCH:-release}"
REMOTE="origin/$BRANCH"
HEALTH_URL="${UPDATE_HEALTH_URL:-http://127.0.0.1:5050/api/health}"
HEALTH_TIMEOUT="${UPDATE_HEALTH_TIMEOUT:-120}"
MIN_FREE_MB="${UPDATE_MIN_FREE_MB:-1024}"
REPO_USER="${UPDATE_USER:-$(stat -c '%U' "$ROOT")}"
FORCE=0
NO_LOCK=0
NO_FETCH=0
DRY_RUN=0

while [ $# -gt 0 ]; do
    case "$1" in
        --force) FORCE=1 ;;
        --no-lock) NO_LOCK=1 ;;
        --no-fetch) NO_FETCH=1 ;;
        --check) DRY_RUN=1 ;;
    esac
    shift
done

log() { echo "$(date -Is) [update] $*"; }
journal() { logger -t poderebot-update -- "$*" 2>/dev/null || true; }
sanitize() { printf '%s' "${1//\"/\'}" | tr '\n' ' '; }
as_user() {
    if [ "$(id -u)" -eq 0 ]; then
        runuser -u "$REPO_USER" -- "$@"
    else
        "$@"
    fi
}
sysctl_() {
    if [ "$(id -u)" -eq 0 ]; then
        systemctl "$@"
    else
        sudo -n /usr/bin/systemctl "$@"
    fi
}

write_state() {
    local result="$1" from="$2" to="$3" reason="$4" failed="${5:-}"
    {
        printf '{\n'
        printf '  "lastRunUtc": "%s",\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        printf '  "result": "%s",\n' "$result"
        printf '  "fromSha": "%s",\n' "$from"
        printf '  "toSha": "%s",\n' "$to"
        printf '  "failedSha": "%s",\n' "$failed"
        printf '  "reason": "%s"\n' "$(sanitize "$reason")"
        printf '}\n'
    } > "$STATE.tmp" 2>/dev/null && mv "$STATE.tmp" "$STATE"
    chown "$REPO_USER":"$REPO_USER" "$STATE" 2>/dev/null || true
}

finish() {
    local result="$1" from="$2" to="$3" reason="$4" failed="${5:-}" code="${6:-0}"
    write_state "$result" "$from" "$to" "$reason" "$failed"
    log "$result: $reason"
    journal "$result from=$from to=$to reason=$reason"
    exit "$code"
}

state_field() {
    [ -f "$STATE" ] || return 0
    sed -n "s/.*\"$1\": *\"\\([^\"]*\\)\".*/\\1/p" "$STATE" | head -n1
}

health_ok() {
    local token
    token=$(sed -n 's/^API_TOKEN=//p' "$ROOT/.env" 2>/dev/null | head -n1)
    if [ -n "$token" ]; then
        curl -fsS -m 5 -H "X-Api-Token: $token" "$HEALTH_URL" >/dev/null 2>&1
    else
        curl -fsS -m 5 "$HEALTH_URL" >/dev/null 2>&1
    fi
}

wait_healthy() {
    local deadline=$(( $(date +%s) + HEALTH_TIMEOUT ))
    while [ "$(date +%s)" -lt "$deadline" ]; do
        health_ok && return 0
        sleep 2
    done
    return 1
}

if [ "$NO_LOCK" -eq 0 ]; then
    exec 9>"$LOCK_FILE" || finish "failed" "" "" "cannot open the lock file" "" 1
    if ! flock -n 9; then
        log "another update is running, skipping"
        exit 0
    fi
fi

cd "$ROOT" || finish "failed" "" "" "cannot enter $ROOT" "" 1
local_sha=$(as_user git rev-parse HEAD 2>/dev/null)
[ -n "$local_sha" ] || finish "failed" "" "" "not a git repository" "" 1

build_sha=""
if [ -f "$ROOT/build/version.json" ]; then
    build_sha=$(sed -n 's/.*"sha"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$ROOT/build/version.json" | head -n1)
fi

if [ "$NO_FETCH" -eq 0 ]; then
    if ! as_user git fetch --quiet origin "$BRANCH" 2>/dev/null; then
        finish "failed" "$local_sha" "" "git fetch origin $BRANCH failed" "" 1
    fi
    remote_sha=$(as_user git rev-parse "$REMOTE" 2>/dev/null)
    [ -n "$remote_sha" ] || finish "failed" "$local_sha" "" "$REMOTE not found" "" 1
else
    remote_sha="$local_sha"
fi

if [ "$DRY_RUN" -eq 1 ]; then
    if [ -n "$build_sha" ] && [ "$build_sha" = "$remote_sha" ]; then
        log "check: already running $remote_sha"
    else
        log "check: running=${build_sha:-unknown} remote=$remote_sha, an update would apply"
    fi
    exit 0
fi

if [ "$FORCE" -eq 0 ] && [ -n "$build_sha" ] && [ "$build_sha" = "$remote_sha" ]; then
    finish "noop" "$build_sha" "$remote_sha" "already at $remote_sha" "" 0
fi
if [ "$FORCE" -eq 0 ] && [ "$(state_field failedSha)" = "$remote_sha" ]; then
    finish "skipped" "$local_sha" "$remote_sha" "sha already failed, not retrying" "$remote_sha" 0
fi

if [ "$NO_FETCH" -eq 0 ]; then
    if ! as_user git diff --quiet || ! as_user git diff --cached --quiet; then
        if [ "$FORCE" -eq 0 ]; then
            finish "refused" "$local_sha" "$remote_sha" "working tree is dirty" "" 0
        fi
    fi
    free_mb=$(df -Pm "$ROOT" | awk 'NR==2 {print $4}')
    if [ -z "$free_mb" ] || [ "$free_mb" -lt "$MIN_FREE_MB" ]; then
        finish "refused" "$local_sha" "$remote_sha" "not enough free space (${free_mb:-?} MB < $MIN_FREE_MB MB)" "" 0
    fi
    if ! as_user git reset --hard "$remote_sha" >/dev/null 2>&1; then
        finish "failed" "$local_sha" "$remote_sha" "git reset to $remote_sha failed" "" 1
    fi
fi

log "building $remote_sha into build.next"
rm -rf "$ROOT/build.next"
build_ok=0
( exec 9>&- 2>/dev/null || true; as_user dotnet publish "$ROOT/PodereBot.csproj" -c Release -r linux-arm64 --self-contained false -o "$ROOT/build.next" >/dev/null 2>&1 ) && build_ok=1
if [ "$build_ok" -ne 1 ]; then
    rm -rf "$ROOT/build.next"
    if [ "$NO_FETCH" -eq 0 ]; then
        as_user git reset --hard "$local_sha" >/dev/null 2>&1 || true
    fi
    finish "failed" "$local_sha" "$remote_sha" "release build failed" "" 1
fi

# The panel reads this from the publish folder: the exact commit that was built and when.
printf '{\n  "sha": "%s",\n  "builtAtUtc": "%s",\n  "source": "github"\n}\n' \
    "$remote_sha" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$ROOT/build.next/version.json"
chown "$REPO_USER":"$REPO_USER" "$ROOT/build.next/version.json" 2>/dev/null || true

log "stopping the watchdog timer for the swap"
sysctl_ stop poderebot-wd.timer || true

old_pid=$(systemctl show -p MainPID --value poderebot.service 2>/dev/null || echo 0)
log "stopping the service (pid ${old_pid:-0})"
sysctl_ stop poderebot.service || true
stopped=0
for _ in $(seq 1 20); do
    if ! systemctl is-active --quiet poderebot.service; then stopped=1; break; fi
    sleep 1
done
if [ "$stopped" -ne 1 ]; then
    rm -rf "$ROOT/build.next"
    sysctl_ start poderebot-wd.timer || true
    finish "failed" "$local_sha" "$remote_sha" "the service did not stop, nothing was swapped" "" 1
fi

log "swapping the publish folder"
rm -rf "$ROOT/build.old"
if [ -d "$ROOT/build" ]; then
    mv "$ROOT/build" "$ROOT/build.old"
fi
mv "$ROOT/build.next" "$ROOT/build"
for runtime_file in db.json users.json usage.json panel.db; do
    if [ -f "$ROOT/build.old/$runtime_file" ] && [ ! -f "$ROOT/$runtime_file" ]; then
        mv "$ROOT/build.old/$runtime_file" "$ROOT/$runtime_file"
        log "preserved $runtime_file from the previous build"
    fi
done

sysctl_ start poderebot.service || true
started=0
for _ in $(seq 1 30); do
    if systemctl is-active --quiet poderebot.service; then started=1; break; fi
    sleep 1
done
new_pid=$(systemctl show -p MainPID --value poderebot.service 2>/dev/null || echo 0)

if [ "$started" -eq 1 ] && [ "${new_pid:-0}" != "${old_pid:-0}" ] && wait_healthy; then
    rm -rf "$ROOT/build.old"
    sysctl_ start poderebot-wd.timer || true
    finish "updated" "$local_sha" "$remote_sha" "applied $remote_sha" "" 0
fi

log "the update did not become healthy, rolling back to $local_sha"
sysctl_ stop poderebot.service || true
for _ in $(seq 1 20); do
    systemctl is-active --quiet poderebot.service || break
    sleep 1
done
rm -rf "$ROOT/build.failed.${remote_sha:0:12}"
mv "$ROOT/build" "$ROOT/build.failed.${remote_sha:0:12}"
mv "$ROOT/build.old" "$ROOT/build"
if [ "$NO_FETCH" -eq 0 ]; then
    as_user git reset --hard "$local_sha" >/dev/null 2>&1 || true
fi
sysctl_ start poderebot.service || true
sysctl_ start poderebot-wd.timer || true
wait_healthy || log "the service is still unhealthy after the rollback"
finish "rolled-back" "$local_sha" "$remote_sha" "the new build did not become healthy" "$remote_sha" 1
EOF

chmod +x "$ROOT/run.sh" "$ROOT/update.sh" "$ROOT/panel.sh" "$ROOT/panel-pair.sh"
chown "$user":"$user" "$ROOT/run.sh" "$ROOT/update.sh" "$ROOT/panel.sh" "$ROOT/panel-pair.sh"

# ===== SYSTEMD SERVICE SETUP
setup_units() {
    echo - writing .service file
    cat > /etc/systemd/system/poderebot.service <<EOF
[Unit]
Description=Telegram Podere bot
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=$user
Group=$user
WorkingDirectory=$ROOT
ExecStart="$ROOT/run.sh"
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

    echo - writing watchdog .service files
    cat > /etc/systemd/system/poderebot-wd.service <<EOF
[Unit]
Description=Telegram Podere bot watchdog service
After=poderebot.service

[Service]
Type=oneshot
ExecStart=/bin/sh -c 'R="$ROOT"; T=\$(sed -n "s/^API_TOKEN=//p" "\$R/.env" 2>/dev/null | head -n1); if [ -n "\$T" ]; then curl -m 5 -f -H "X-Api-Token: \$T" http://localhost:5050/api/health || systemctl restart poderebot.service; else curl -m 5 -f http://localhost:5050/api/health || systemctl restart poderebot.service; fi'

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

    update_interval=$(sed -n 's/^UPDATE_INTERVAL=//p' "$ROOT/.env" 2>/dev/null | head -n1)
    [ -n "$update_interval" ] || update_interval=2min

    echo - writing update units
    cat > /etc/systemd/system/poderebot-update.service <<EOF
[Unit]
Description=PodereBot update from the release branch
Wants=network-online.target
After=network-online.target

[Service]
Type=oneshot
WorkingDirectory=$ROOT
ExecStart=$ROOT/install.sh --auto
Nice=10
IOSchedulingClass=best-effort
IOSchedulingPriority=7
TimeoutStartSec=1800
EOF

    cat > /etc/systemd/system/poderebot-update.timer <<EOF
[Unit]
Description=PodereBot update timer

[Timer]
OnBootSec=5min
OnUnitActiveSec=$update_interval
AccuracySec=1min
Persistent=false

[Install]
WantedBy=timers.target
EOF

    echo - writing the touch panel kiosk unit
    cat > /etc/systemd/system/poderebot-kiosk.service <<EOF
[Unit]
Description=PodereBot touch panel
BindsTo=poderebot.service
After=poderebot.service

[Service]
Type=simple
User=$user
WorkingDirectory=$ROOT
Environment=XDG_RUNTIME_DIR=/run/poderebot-kiosk
RuntimeDirectory=poderebot-kiosk
RuntimeDirectoryMode=0700
ExecStart=$ROOT/panel.sh
Restart=always
RestartSec=3
Nice=5

[Install]
WantedBy=poderebot.service
EOF

    echo - writing the restricted sudoers rule
    cat > /etc/sudoers.d/020-poderebot-update <<EOF
$user ALL=(root) NOPASSWD: /usr/bin/systemctl stop poderebot.service, /usr/bin/systemctl start poderebot.service, /usr/bin/systemctl restart poderebot.service, /usr/bin/systemctl stop poderebot-wd.timer, /usr/bin/systemctl start poderebot-wd.timer, /usr/bin/systemctl restart poderebot-wd.timer
EOF
    chmod 440 /etc/sudoers.d/020-poderebot-update
    if ! visudo -cf /etc/sudoers.d/020-poderebot-update; then
        echo "invalid sudoers rule, removing it"
        rm -f /etc/sudoers.d/020-poderebot-update
        exit 1
    fi
    if ls /etc/sudoers.d/ | grep -qv '^020-poderebot-update$' && grep -rls 'NOPASSWD: *ALL' /etc/sudoers.d/ >/dev/null 2>&1; then
        echo "warning: a broad NOPASSWD rule already exists in /etc/sudoers.d, the restricted rule is not the only one"
    fi

    echo - recording the installation fingerprint
    {
        echo "install_sha=$(sha256sum "$ROOT/install.sh" | cut -d' ' -f1)"
        echo "units_sha=$(cat /etc/systemd/system/poderebot.service /etc/systemd/system/poderebot-wd.service /etc/systemd/system/poderebot-wd.timer /etc/systemd/system/poderebot-update.service /etc/systemd/system/poderebot-update.timer /etc/systemd/system/poderebot-kiosk.service | sha256sum | cut -d' ' -f1)"
        echo "installed_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    } > "$ROOT/.install-state"
    chown "$user":"$user" "$ROOT/.install-state"

    echo - reloading daemon
    systemctl daemon-reload
    echo - enabling services
    systemctl enable poderebot.service
    systemctl enable poderebot-wd.service
    systemctl enable poderebot-wd.timer
    systemctl enable poderebot-update.timer
    systemctl enable poderebot-kiosk.service

    systemctl start poderebot-wd.timer
    systemctl start poderebot-update.timer
}

setup_units
rm -f "$ROOT/patch.sh"

# Runtime files created by a previous root run must belong to the service user.
for runtime_file in db.json users.json usage.json panel.db panel.db-wal panel.db-shm; do
    if [ -e "$ROOT/$runtime_file" ]; then
        chown "$user":"$user" "$ROOT/$runtime_file"
    fi
done

# A changed unit needs a restart even when the build is already current.
if [ "$MODE" = auto ] && [ -n "$build_sha" ] && [ "$build_sha" = "$target_sha" ]; then
    echo - restarting the service for the updated units
    systemctl restart poderebot.service
    write_state "updated" "$build_sha" "$target_sha" "units reinstalled"
    exit 0
fi

if [ "$MODE" = auto ]; then
    update_args=(--no-lock)
else
    update_args=(--force --no-lock)
fi
if [ "$NO_PULL" -eq 1 ]; then
    update_args+=(--no-fetch)
fi
"$ROOT/update.sh" "${update_args[@]}"

echo - installation completed
echo "  directory: $ROOT"
echo "  update:    sudo $ROOT/install.sh (manual) / $ROOT/install.sh --auto (timer)"
echo "  logs:      journalctl -u poderebot.service -f"
echo "  panel:     http://127.0.0.1:5051/panel/ (kiosk) · journalctl -u poderebot-kiosk.service -f"
echo "  pairing:   $ROOT/panel-pair.sh \"nome dispositivo\""
echo "  autoupdate: journalctl -u poderebot-update.service -f, state in $ROOT/update-state.json"
