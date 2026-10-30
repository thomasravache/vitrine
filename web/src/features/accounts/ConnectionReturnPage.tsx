import { useQueryClient } from "@tanstack/react-query";
import { Link, Navigate, useNavigate } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import {
  completeMercadoLivreConnection,
  type completeMercadoLivreConnectionResponse,
} from "../../api/generated/vitrine.ts";
import type { Problem } from "../../api/session.ts";
import { ErrorAlert } from "../../ui/Alert.tsx";
import { Button, buttonClass } from "../../ui/Button.tsx";
import { InfoIcon } from "../../ui/icons.tsx";
import { useDocumentTitle } from "../../ui/useDocumentTitle.ts";
import {
  CANCELLED_MESSAGE,
  connectionError,
  type ErrorView,
  NETWORK_ERROR,
  UNKNOWN_CONNECTION_ERROR,
} from "./errorMessages.ts";
import { accountsQuery } from "./queries.ts";
import { useStartConnection } from "./useStartConnection.ts";

export const RETURN_PATH = "/contas/mercado-livre/retorno";

type Callback =
  | { kind: "complete"; code: string; state: string }
  | { kind: "error"; error: string }
  | { kind: "none" };

/** Lê só code/state/error da query do retorno do ML. */
function readCallback(search: string): Callback {
  const params = new URLSearchParams(search);
  const error = params.get("error");
  if (error) {
    return { kind: "error", error };
  }
  const code = params.get("code");
  const state = params.get("state");
  return code && state ? { kind: "complete", code, state } : { kind: "none" };
}

// Uma conclusão por par code/state, mesmo com o StrictMode montando a tela duas vezes (o code é de uso único).
const completions = new Map<string, Promise<completeMercadoLivreConnectionResponse>>();

function completeOnce(code: string, state: string) {
  const key = `${state}\n${code}`;
  let completion = completions.get(key);
  if (!completion) {
    completion = completeMercadoLivreConnection({ code, state });
    completions.set(key, completion);
    completion.catch(() => {}).finally(() => completions.delete(key));
  }
  return completion;
}

type View = { kind: "completing" } | { kind: "cancelled" } | { kind: "failed"; error: ErrorView };

/**
 * Retorno do OAuth do Mercado Livre: tira code/state da URL (history.replaceState) antes de qualquer outra coisa,
 * conclui a conexão uma única vez e volta para Contas com o aviso de sucesso, ou mostra o erro do `code`.
 */
export function ConnectionReturnPage() {
  useDocumentTitle("Conectando conta");
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [callback] = useState(() => readCallback(window.location.search));
  const [view, setView] = useState<View>(() =>
    callback.kind === "error"
      ? callback.error === "access_denied"
        ? { kind: "cancelled" }
        : { kind: "failed", error: UNKNOWN_CONNECTION_ERROR }
      : { kind: "completing" },
  );

  useEffect(() => {
    if (window.location.search) {
      window.history.replaceState(window.history.state, "", RETURN_PATH);
    }
  }, []);

  useEffect(() => {
    if (callback.kind !== "complete") {
      return;
    }
    let active = true;
    completeOnce(callback.code, callback.state)
      .then(async (response) => {
        if (!active) {
          return;
        }
        if (response.status === 200 || response.status === 201) {
          await queryClient.invalidateQueries({ queryKey: accountsQuery.queryKey });
          await queryClient.invalidateQueries({ queryKey: ["listings"] });
          await navigate({
            to: "/contas",
            replace: true,
            state: { connectedNickname: response.data.nickname },
          });
          return;
        }
        setView({ kind: "failed", error: connectionError(response.data as Problem) });
      })
      .catch(() => {
        if (active) {
          setView({ kind: "failed", error: NETWORK_ERROR });
        }
      });
    return () => {
      active = false;
    };
  }, [callback, navigate, queryClient]);

  if (callback.kind === "none") {
    return <Navigate to="/contas" replace />;
  }

  return (
    <div className="mx-auto max-w-[560px]">
      <h1 className="text-3xl font-bold tracking-tight">Conectar conta do Mercado Livre</h1>
      <div className="mt-8">
        {view.kind === "completing" && (
          <p role="status" className="text-lg text-muted">
            Concluindo a conexão…
          </p>
        )}
        {view.kind === "cancelled" && (
          <p className="flex items-start gap-3 rounded-xl border border-line-soft bg-surface px-4 py-3 text-ink">
            <InfoIcon className="mt-0.5 size-5 shrink-0 text-primary" />
            <span>{CANCELLED_MESSAGE}</span>
          </p>
        )}
        {view.kind === "failed" && <ErrorAlert>{view.error.message}</ErrorAlert>}
      </div>
      {view.kind !== "completing" && (
        <Actions canRetry={view.kind === "failed" && view.error.canRetry} />
      )}
    </div>
  );
}

function Actions({ canRetry }: { canRetry: boolean }) {
  const { start, starting, error } = useStartConnection();
  return (
    <>
      {error && (
        <div className="mt-4">
          <ErrorAlert>{error.message}</ErrorAlert>
        </div>
      )}
      <div className="mt-6 flex flex-col gap-3 sm:flex-row">
        {canRetry && (
          <Button onClick={start} disabled={starting}>
            Tentar de novo
          </Button>
        )}
        <Link to="/contas" className={buttonClass(canRetry ? "outline" : "primary")}>
          Voltar para Contas
        </Link>
      </div>
    </>
  );
}
