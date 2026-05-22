#!/usr/bin/env bash
# Deploy da PoV pra VM no GCP.
#
# O que esse script faz:
#   1. Verifica gcloud auth + Docker Hub login
#   2. (opcional) Build multi-arch + push pra Docker Hub
#   3. Bootstrap da VM se Docker não estiver lá
#   4. scp do docker-compose, .env e nginx config
#   5. ssh + docker compose pull + up -d
#   6. nginx reload + (1ª vez) certbot --nginx
#   7. Curl em https://$PUBLIC_DOMAIN pra confirmar
#
# Customize VM_NAME, VM_ZONE, VM_PROJECT, PUBLIC_DOMAIN antes do 1º deploy.
#
# Uso:
#   ./deploy.sh              # deploy completo
#   ./deploy.sh --skip-build # pula buildx push, usa o image que já tá no Hub
#   ./deploy.sh --build-only # só faz buildx push, não toca na VM
#   ./deploy.sh --logs       # ssh + tail dos logs dos containers

set -euo pipefail
cd "$(dirname "$0")"

# ============================================================
# Config — ajustar pro seu setup
# ============================================================
VM_NAME="${VM_NAME:-gabs-gabi-globo-autoscaler-field-engineering-pov}"
VM_ZONE="${VM_ZONE:-us-east1-c}"
VM_PROJECT="${VM_PROJECT:-central-beach-194106}"
REMOTE_DIR="${REMOTE_DIR:-redis-banco-extrato}"
IMAGE_NAME="${IMAGE_NAME:-gacerioni/redis-banco-extrato}"
IMAGE_TAG="${IMAGE_TAG:-0.1.0}"
PUBLIC_DOMAIN="${PUBLIC_DOMAIN:-extrato.platformengineer.io}"
PUBLIC_URL="https://$PUBLIC_DOMAIN"
BUILDX_BUILDER="${BUILDX_BUILDER:-imusica-builder}"
CERT_EMAIL="${CERT_EMAIL:-gabriel.cerioni@redis.com}"

# ============================================================
# Pretty
# ============================================================
GREEN='\033[0;32m'; ORANGE='\033[0;33m'; RED='\033[0;31m'; BOLD='\033[1m'; DIM='\033[2m'; NC='\033[0m'
ok()   { echo -e "${GREEN}✓${NC} $*"; }
warn() { echo -e "${ORANGE}⚠${NC} $*"; }
err()  { echo -e "${RED}✗${NC} $*" >&2; }
step() { echo -e "\n${BOLD}→${NC} $*"; }

# ============================================================
# Helpers
# ============================================================
gcloud_ssh() {
    gcloud compute ssh "$VM_NAME" --zone "$VM_ZONE" --project "$VM_PROJECT" --command "$1"
}
gcloud_scp() {
    gcloud compute scp --zone "$VM_ZONE" --project "$VM_PROJECT" "$@"
}

# ============================================================
# Sub-commando: logs
# ============================================================
if [[ "${1:-}" == "--logs" ]]; then
    step "Streaming dos logs da VM…"
    gcloud_ssh "cd $REMOTE_DIR && sudo docker compose -f docker-compose.yml -f docker-compose.cloud.yml logs -f --tail=80"
    exit 0
fi

# ============================================================
# Sub-commando: build-only
# ============================================================
SKIP_BUILD=0
BUILD_ONLY=0
if [[ "${1:-}" == "--skip-build" ]]; then SKIP_BUILD=1; fi
if [[ "${1:-}" == "--build-only" ]]; then BUILD_ONLY=1; fi

# ============================================================
# Pré-flight
# ============================================================
step "Pré-flight"
command -v gcloud >/dev/null 2>&1 || { err "gcloud não encontrado no PATH."; exit 1; }
command -v docker >/dev/null 2>&1 || { err "docker não encontrado."; exit 1; }
ok "gcloud + docker disponíveis"

if ! grep -q "index.docker.io" ~/.docker/config.json 2>/dev/null; then
    err "Não vejo auth do Docker Hub. Rode 'docker login' primeiro."
    exit 1
fi
ok "Docker Hub autenticado"

if [[ ! -f .env ]]; then
    err ".env não encontrado. Crie a partir de .env.example com OPENAI_API_KEY válida."
    exit 1
fi
ok ".env presente"

# ============================================================
# Build + push (a menos que --skip-build)
# ============================================================
if [[ $SKIP_BUILD -eq 0 ]]; then
    step "Buildx multi-arch (linux/amd64 + linux/arm64) → push pro Docker Hub"

    if ! docker buildx inspect "$BUILDX_BUILDER" >/dev/null 2>&1; then
        warn "Builder '$BUILDX_BUILDER' não existe — criando…"
        docker buildx create --name "$BUILDX_BUILDER" --driver docker-container
    fi
    docker buildx use "$BUILDX_BUILDER"
    docker buildx inspect --bootstrap >/dev/null

    docker buildx build \
        --builder "$BUILDX_BUILDER" \
        --platform linux/amd64,linux/arm64 \
        -f Dockerfile \
        -t "$IMAGE_NAME:$IMAGE_TAG" \
        -t "$IMAGE_NAME:latest" \
        --push .

    ok "Image publicada: $IMAGE_NAME:$IMAGE_TAG + :latest"
fi

if [[ $BUILD_ONLY -eq 1 ]]; then
    ok "Build-only concluído. Use --skip-build pra deployar."
    exit 0
fi

# ============================================================
# Conexão com a VM
# ============================================================
step "Verificando conectividade com $VM_NAME"
if ! gcloud_ssh "echo ok" >/dev/null 2>&1; then
    err "Não consegui SSH na VM. Confira gcloud auth login e o nome/zone/project."
    exit 1
fi
ok "VM acessível"

# ============================================================
# Bootstrap (Docker na VM)
# ============================================================
step "Bootstrap da VM (instala Docker se faltar)"
gcloud_scp scripts/bootstrap-vm.sh "$VM_NAME:~/bootstrap-vm.sh" >/dev/null
gcloud_ssh "chmod +x ~/bootstrap-vm.sh && ~/bootstrap-vm.sh"

# ============================================================
# Copia os arquivos do deploy
# ============================================================
step "Copiando docker-compose, .env e nginx config pra VM"
gcloud_ssh "mkdir -p ~/$REMOTE_DIR" >/dev/null
gcloud_scp docker-compose.yml docker-compose.cloud.yml .env \
    "$VM_NAME:~/$REMOTE_DIR/" >/dev/null
gcloud_scp deploy/nginx-extrato.conf "$VM_NAME:~/$REMOTE_DIR/" >/dev/null
ok "Arquivos copiados em ~/$REMOTE_DIR"

# ============================================================
# nginx: copia config e instala se ainda não tem
# ============================================================
step "Configurando nginx (site $PUBLIC_DOMAIN)"
gcloud_ssh "
    set -e
    if ! command -v nginx >/dev/null 2>&1; then
        sudo apt-get update -y
        sudo apt-get install -y nginx certbot python3-certbot-nginx
    fi
    sudo cp ~/$REMOTE_DIR/nginx-extrato.conf /etc/nginx/sites-available/$PUBLIC_DOMAIN
    sudo ln -sf /etc/nginx/sites-available/$PUBLIC_DOMAIN /etc/nginx/sites-enabled/$PUBLIC_DOMAIN
    sudo nginx -t
    sudo systemctl reload nginx
"
ok "nginx reload OK"

# ============================================================
# Deploy
# ============================================================
step "Pull + up -d na VM"
gcloud_ssh "cd ~/$REMOTE_DIR && \
    sudo IMAGE_TAG='$IMAGE_TAG' docker compose -f docker-compose.yml -f docker-compose.cloud.yml pull && \
    sudo IMAGE_TAG='$IMAGE_TAG' docker compose -f docker-compose.yml -f docker-compose.cloud.yml up -d --remove-orphans"

# ============================================================
# certbot — só na 1ª vez (idempotente: skip se cert já existe)
# ============================================================
step "Verificando TLS (Let's Encrypt) pra $PUBLIC_DOMAIN"
gcloud_ssh "
    if [ ! -f /etc/letsencrypt/live/$PUBLIC_DOMAIN/fullchain.pem ]; then
        echo '  Cert não existe — emitindo via certbot --nginx…'
        sudo certbot --nginx -d $PUBLIC_DOMAIN --non-interactive --agree-tos --email '$CERT_EMAIL' --redirect
    else
        echo '  Cert já existe — pulando emissão. (renovação roda via certbot.timer)'
    fi
"

# ============================================================
# Aguarda app ficar saudável
# ============================================================
step "Aguardando /api/health responder 200…"
for i in $(seq 1 30); do
    if curl -sSfI -o /dev/null --max-time 5 "$PUBLIC_URL/api/health"; then
        ok "/api/health 200 OK em $PUBLIC_URL após ${i}×2s"
        break
    fi
    sleep 2
done

# ============================================================
# Smoke test público
# ============================================================
step "Smoke test"
if curl -sSf --max-time 10 "$PUBLIC_URL/api/health" | grep -q "ok"; then
    ok "/api/health respondeu OK"
else
    warn "/api/health não respondeu. Cheque com ./scripts/ssh.sh + docker logs."
fi

if curl -sSf --max-time 10 "$PUBLIC_URL/api/redis/info" >/dev/null; then
    ok "/api/redis/info respondeu"
fi

# ============================================================
# Done
# ============================================================
echo
echo "════════════════════════════════════════════════════════════════"
echo -e "  ${GREEN}${BOLD}Deploy concluído.${NC}"
echo "════════════════════════════════════════════════════════════════"
echo
echo -e "  Demo pública:  ${BOLD}$PUBLIC_URL${NC}"
echo -e "  Admin panel:   ${BOLD}$PUBLIC_URL/admin.html${NC}"
echo -e "  ${DIM}SSH na VM:${NC}    ./scripts/ssh.sh"
echo -e "  ${DIM}Logs ao vivo:${NC} ./deploy.sh --logs"
echo
