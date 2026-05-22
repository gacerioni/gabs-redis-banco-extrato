#!/usr/bin/env bash
# Bootstrap one-shot na VM: instala Docker + Docker Compose v2 se faltar.
# Rodado pelo deploy.sh; pode ser rodado manualmente também.
#
# Idempotente — se Docker já tá lá, só reporta a versão e sai.

set -euo pipefail

GREEN='\033[0;32m'; ORANGE='\033[0;33m'; NC='\033[0m'
ok()   { echo -e "${GREEN}✓${NC} $*"; }
step() { echo -e "${ORANGE}→${NC} $*"; }

step "Bootstrap da VM"

# Check Docker
if command -v docker >/dev/null 2>&1; then
    ok "Docker já instalado: $(docker --version)"
else
    step "Instalando Docker (script oficial)…"
    curl -fsSL https://get.docker.com -o /tmp/get-docker.sh
    sudo sh /tmp/get-docker.sh
    rm -f /tmp/get-docker.sh
    sudo usermod -aG docker "$USER"
    ok "Docker instalado. NOTA: re-login pra entrar no grupo 'docker' (ou usar sudo nos compose commands)."
fi

# Check Docker Compose v2 (vem junto com Docker Engine moderno)
if docker compose version >/dev/null 2>&1; then
    ok "Compose v2 disponível: $(docker compose version --short)"
else
    step "Docker Compose v2 não detectado. Tentando via plugin..."
    sudo apt-get update -y && sudo apt-get install -y docker-compose-plugin || {
        echo "Falha ao instalar compose plugin. Continue manualmente."
        exit 1
    }
    ok "Compose v2 instalado: $(docker compose version --short)"
fi

# Firewall: nginx precisa de 80 e 443. No GCP, isso é controlado por firewall
# rules do projeto. Esse comando só age se UFW estiver ativo (não é default
# em GCE).
if command -v ufw >/dev/null 2>&1 && sudo ufw status 2>/dev/null | grep -q "Status: active"; then
    step "Liberando 80/443 no UFW…"
    sudo ufw allow 80/tcp || true
    sudo ufw allow 443/tcp || true
fi

ok "VM pronta pro deploy."
