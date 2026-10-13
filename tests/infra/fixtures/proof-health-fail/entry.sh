# SPEC-0005:IT-03 — wrapper do dublê proof-health-fail (executado pelo busybox estático).
if [ "${1:-}" = "--migrate" ]; then
  # shellcheck disable=SC2086 # entrypoint original da base, separado em palavras de propósito
  exec $PROOF_APP_ENTRYPOINT "$@"
fi
echo "proof-health-fail: application refuses to start" >&2
exit 1
