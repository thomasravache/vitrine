# SPEC-0005:IT-03 — dublê proof-health-fail: `--migrate` funciona (delegado à imagem base);
# sem argumentos o processo sai com erro, então /health/live nunca fica 200.
# Construído por tests/infra/lib.sh (build_proof) FROM a imagem base real.
ARG BUSYBOX_IMAGE=busybox:1.37.0-musl
ARG BASE_IMAGE
FROM ${BUSYBOX_IMAGE} AS busybox

FROM ${BASE_IMAGE}
ARG APP_ENTRYPOINT
ENV PROOF_APP_ENTRYPOINT="${APP_ENTRYPOINT}"
COPY --from=busybox /bin/busybox /proof/busybox
COPY entry.sh /proof/entry.sh
LABEL spec="SPEC-0005" proof="health-fail"
ENTRYPOINT ["/proof/busybox", "sh", "/proof/entry.sh"]
CMD []
