#!/usr/bin/env bash
# Atalho pra SSH na VM que hospeda a demo pública.
#
# Customize VM_NAME/VM_ZONE/VM_PROJECT pra apontar pra sua VM.
#
# Uso:
#   ./scripts/ssh.sh                 # sessão interativa
#   ./scripts/ssh.sh -- 'comando'    # roda comando direto (não-interativo)

VM_NAME="${VM_NAME:-gabs-gabi-globo-autoscaler-field-engineering-pov}"
VM_ZONE="${VM_ZONE:-us-east1-c}"
VM_PROJECT="${VM_PROJECT:-central-beach-194106}"

if [[ "${1:-}" == "--" ]]; then
    shift
    exec gcloud compute ssh "$VM_NAME" \
        --zone "$VM_ZONE" --project "$VM_PROJECT" \
        --command "$*"
else
    exec gcloud compute ssh "$VM_NAME" \
        --zone "$VM_ZONE" --project "$VM_PROJECT" "$@"
fi
