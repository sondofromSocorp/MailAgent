#!/usr/bin/env bash
# Installe MailAgent + Ollama sur un VPS Ubuntu (teste pour Oracle Cloud Ampere A1, arm64).
# Idempotent : relancable sans casse (mise a jour du code comprise).
# Usage : sudo bash setup-vps.sh
set -euo pipefail

REPO=https://github.com/sondofromSocorp/MailAgent.git
APP_DIR=/opt/mailagent
ENV_FILE=/etc/mailagent.env
MODEL=qwen2.5:7b

[ "$(id -u)" -eq 0 ] || { echo "Lance-moi avec sudo."; exit 1; }

echo "=== 1/5 Paquets (git, .NET 8 SDK) ==="
apt-get update -q
apt-get install -y -q git curl dotnet-sdk-8.0

echo "=== 2/5 Ollama + modele $MODEL ==="
command -v ollama >/dev/null || curl -fsSL https://ollama.com/install.sh | sh
systemctl enable --now ollama
ollama pull "$MODEL"

echo "=== 3/5 Code de l'agent ==="
if [ -d "$APP_DIR/src/.git" ]; then
    git -C "$APP_DIR/src" pull --ff-only
else
    mkdir -p "$APP_DIR"
    git clone "$REPO" "$APP_DIR/src"
fi
dotnet publish "$APP_DIR/src" -c Release -o "$APP_DIR/publish"

echo "=== 4/5 Secrets ==="
if [ ! -f "$ENV_FILE" ]; then
    cp "$APP_DIR/src/deploy/mailagent.env.example" "$ENV_FILE"
    chmod 600 "$ENV_FILE"
    echo ">>> Remplis $ENV_FILE (nano $ENV_FILE) puis relance : sudo bash $0"
    exit 0
fi
grep -q "ton.adresse@gmail.com" "$ENV_FILE" && { echo ">>> $ENV_FILE contient encore les valeurs d'exemple : remplis-le puis relance."; exit 1; }

echo "=== 5/5 Service systemd (toutes les 5 min) ==="
cp "$APP_DIR/src/deploy/mailagent.service" /etc/systemd/system/
cp "$APP_DIR/src/deploy/mailagent.timer" /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now mailagent.timer

echo
echo "Termine. Premiere passe lancee en arriere-plan :"
systemctl start mailagent.service --no-block
echo "  - suivre les logs : journalctl -u mailagent -f"
echo "  - etat du timer   : systemctl list-timers mailagent.timer"
