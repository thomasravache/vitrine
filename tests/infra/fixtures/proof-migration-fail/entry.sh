# SPEC-0005:IT-04 — wrapper do dublê proof-migration-fail (executado pelo busybox estático).
if [ "${1:-}" = "--migrate" ]; then
  echo "proof-migration-fail: migration failed on purpose" >&2
  exit 1
fi
# shellcheck disable=SC2086 # entrypoint original da base, separado em palavras de propósito
exec $PROOF_APP_ENTRYPOINT "$@"
