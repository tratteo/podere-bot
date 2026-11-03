#!/usr/bin/env bash
# Creates a long-lived self-signed certificate for the LAN panel.
# Usage: scripts/make-cert.sh [host] [ip]
#   host  hostname in the certificate (default poderebot.local)
#   ip    LAN address of the Pi (default 192.168.254.12)
# Environment:
#   CERT_DAYS             validity in days (default 36500, about 100 years)
#   PANEL_CERT_PASSWORD   PFX password (default poderebot)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
HOST="${1:-poderebot.local}"
IP="${2:-192.168.254.12}"
DAYS="${CERT_DAYS:-36500}"
PASSWORD="${PANEL_CERT_PASSWORD:-poderebot}"

if ! command -v openssl >/dev/null 2>&1; then
    echo "openssl non trovato: installalo o usa Git Bash su Windows." >&2
    exit 1
fi

MSYS_NO_PATHCONV=1 openssl req -x509 -newkey rsa:2048 -sha256 -days "$DAYS" -nodes \
    -keyout panel.key -out panel.crt \
    -subj "/CN=$HOST" \
    -addext "subjectAltName=DNS:$HOST,DNS:localhost,IP:$IP,IP:127.0.0.1" \
    -addext "basicConstraints=critical,CA:TRUE" \
    -addext "keyUsage=critical,digitalSignature,keyCertSign" \
    -addext "extendedKeyUsage=serverAuth"

MSYS_NO_PATHCONV=1 openssl pkcs12 -export -out panel.pfx \
    -inkey panel.key -in panel.crt \
    -name "PodereBot" -passout "pass:$PASSWORD"

chmod 600 panel.key panel.pfx

echo "Certificato creato:"
echo "  $ROOT/panel.crt  (PEM, validita $DAYS giorni)"
echo "  $ROOT/panel.key  (chiave privata)"
echo "  $ROOT/panel.pfx  (PKCS#12, password: $PASSWORD)"
echo
echo "Per abilitare HTTPS imposta in appsettings.json:"
echo '  "Panel": { "Https": true, "CertPassword": "'"$PASSWORD"'" }'
echo "e riavvia il bot: il pannello LAN passa a https://$HOST:5052."
echo
echo "Chrome: importa panel.crt tra le autorita di certificazione radice attendibili."
echo "Windows: certutil -addstore -user Root panel.crt"
