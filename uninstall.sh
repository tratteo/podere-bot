#!/bin/bash
# Remove PodereBot from this machine: systemd services and timers, generated scripts,
# the updater sudoers rule and the published build.
# Run it with sudo from the repository. Data and sources are kept unless --purge or
# --delete-repo is given.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$SCRIPT_DIR"
PURGE=0
DELETE_REPO=0
ASSUME_YES=0
DRY_RUN=0

UNITS=(
    poderebot-kiosk.service
    poderebot-update.timer
    poderebot-update.service
    poderebot-wd.timer
    poderebot-wd.service
    poderebot.service
)

usage() {
    cat <<EOF
usage: sudo $0 [options]

  --purge         also remove .env and runtime data (db.json, users.json, panel.db, ...)
  --delete-repo   remove the whole repository folder (implies --purge)
  --root DIR      repository path, default the folder containing this script
  --yes, -y       do not ask for confirmation
  --dry-run       print what would be removed, change nothing
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --purge) PURGE=1 ;;
        --delete-repo)
            DELETE_REPO=1
            PURGE=1
            ;;
        --root)
            ROOT="${2:-}"
            shift
            ;;
        --yes | -y) ASSUME_YES=1 ;;
        --dry-run) DRY_RUN=1 ;;
        -h | --help)
            usage
            exit 0
            ;;
        *)
            echo "unknown option: $1"
            usage
            exit 1
            ;;
    esac
    shift
done

ROOT="${ROOT%/}"

if [ ! -f "$ROOT/PodereBot.csproj" ]; then
    echo "$ROOT does not look like the PodereBot repository"
    exit 1
fi

if [ "$DRY_RUN" -ne 1 ] && [ "$(id -u)" -ne 0 ]; then
    echo "run this script with sudo"
    exit 1
fi

present=()
for unit in "${UNITS[@]}"; do
    if [ -f "/etc/systemd/system/$unit" ]; then
        present+=("$unit")
    fi
done

echo "PodereBot uninstall"
echo "  repository: $ROOT"
echo "  units:      ${present[*]:-none}"
echo "  build:      $ROOT/build"
if [ "$PURGE" -eq 1 ]; then
    echo "  data:       remove (.env included)"
else
    echo "  data:       keep"
fi
if [ "$DELETE_REPO" -eq 1 ]; then
    echo "  folder:     delete"
else
    echo "  folder:     keep"
fi

if [ "$DRY_RUN" -eq 1 ]; then
    ASSUME_YES=1
fi

if [ "$ASSUME_YES" -ne 1 ]; then
    read -r -p "Continue? (y/N) " answer
    case "$answer" in
        [Yy]*) ;;
        *)
            echo "aborted"
            exit 1
            ;;
    esac

    if [ "$PURGE" -eq 0 ]; then
        read -r -p "Also delete .env and runtime data (.env, db.json, users.json, panel.db)? (y/N) " answer
        case "$answer" in [Yy]*) PURGE=1 ;; esac
    fi

    if [ "$DELETE_REPO" -eq 1 ]; then
        read -r -p "Delete the entire folder $ROOT? (y/N) " answer
        case "$answer" in
            [Yy]*) ;;
            *) DELETE_REPO=0 ;;
        esac
    fi
fi

run() {
    if [ "$DRY_RUN" -eq 1 ]; then
        echo "  would: $*"
    else
        "$@"
    fi
}

run_quiet() {
    if [ "$DRY_RUN" -eq 1 ]; then
        echo "  would: $*"
    else
        "$@" >/dev/null 2>&1 || true
    fi
}

echo "- stopping services and timers"
for unit in "${UNITS[@]}"; do
    if systemctl cat "$unit" >/dev/null 2>&1; then
        run_quiet systemctl stop "$unit"
    fi
done

echo "- disabling services and timers"
for unit in "${UNITS[@]}"; do
    if systemctl cat "$unit" >/dev/null 2>&1; then
        run_quiet systemctl disable "$unit"
    fi
done

echo "- removing units, drop-ins and the kiosk wants link"
for unit in "${UNITS[@]}"; do
    run rm -f "/etc/systemd/system/$unit"
    run rm -rf "/etc/systemd/system/$unit.d"
done
run rm -rf /etc/systemd/system/poderebot.service.wants

echo "- removing the updater sudoers rule"
run rm -f /etc/sudoers.d/020-poderebot-update

echo "- reloading systemd"
run systemctl daemon-reload
for unit in "${UNITS[@]}"; do
    run_quiet systemctl reset-failed "$unit"
done
run rm -rf /run/poderebot-kiosk

echo "- removing generated scripts and the published build"
for file in run.sh patch.sh update.sh panel.sh panel-pair.sh; do
    run rm -f "$ROOT/$file"
done
for file in .install-state update-state.json .update.lock; do
    run rm -f "$ROOT/$file"
done
run rm -rf "$ROOT/build" "$ROOT/build.next" "$ROOT/build.old" "$ROOT/buildruntimes"
if compgen -G "$ROOT/build.failed.*" >/dev/null; then
    for directory in "$ROOT"/build.failed.*; do
        run rm -rf "$directory"
    done
fi

if [ "$PURGE" -eq 1 ]; then
    echo "- removing runtime data"
    for file in .env db.json users.json usage.json startup.json panel.db panel.db-wal panel.db-shm .db-probe; do
        run rm -f "$ROOT/$file"
    done
    if compgen -G "$ROOT/*.tmp" >/dev/null; then
        for file in "$ROOT"/*.tmp; do
            run rm -f "$file"
        done
    fi
fi

if [ "$DELETE_REPO" -eq 1 ]; then
    echo "- removing the repository folder"
    if [ "$ROOT" = "/" ] || [ ! -f "$ROOT/PodereBot.csproj" ]; then
        echo "refusing to delete $ROOT"
    else
        cd /
        run rm -rf "$ROOT"
    fi
fi

echo "- done"
if [ "$DRY_RUN" -ne 1 ]; then
    echo "  services, timers, sudoers rule and build output removed"
    if [ "$PURGE" -eq 1 ]; then
        echo "  runtime data removed"
    fi
    if [ "$DELETE_REPO" -eq 1 ]; then
        echo "  repository folder removed"
    fi
    echo "  cage, cog and the system user were left untouched"
fi
