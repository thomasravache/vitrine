import { useQueryClient, useSuspenseQuery } from "@tanstack/react-query";
import { Navigate, Outlet, useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { logout } from "../../api/generated/vitrine.ts";
import { meQuery } from "../../api/session.ts";
import { ErrorAlert } from "../../ui/Alert.tsx";
import { AppShell } from "./AppShell.tsx";

/** Telas autenticadas (Painel, Anúncios, Contas): a moldura do AppShell com "Sair" e a tela da rota. */
export function AuthenticatedLayout() {
  const { data: me } = useSuspenseQuery(meQuery);
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [signingOut, setSigningOut] = useState(false);
  const [signOutError, setSignOutError] = useState<string>();

  if (!me) {
    // Sessão acabou (expirou ou foi encerrada em outra aba): volta para o login.
    return <Navigate to="/entrar" replace />;
  }

  // Sai da tela quando o servidor confirmou (204) ou já não havia sessão (401); em outra falha, avisa e revalida
  // a sessão em vez de fingir — se ela tiver acabado, a moldura leva para /entrar.
  async function signOut() {
    setSigningOut(true);
    setSignOutError(undefined);
    try {
      const response = await logout();
      if (response.status === 204 || response.status === 401) {
        queryClient.setQueryData(meQuery.queryKey, null);
        queryClient.removeQueries({ predicate: (query) => query.queryKey[0] !== "me" });
        await navigate({ to: "/entrar" });
        return;
      }
      setSignOutError("Não foi possível sair agora. Tente de novo.");
    } catch {
      setSignOutError(
        "Não foi possível falar com o Vitrine. Verifique sua conexão e tente de novo.",
      );
    } finally {
      setSigningOut(false);
    }
    await queryClient.invalidateQueries({ queryKey: meQuery.queryKey });
  }

  return (
    <AppShell me={me} onSignOut={signOut} signingOut={signingOut}>
      {signOutError && (
        <div className="mb-6">
          <ErrorAlert>{signOutError}</ErrorAlert>
        </div>
      )}
      <Outlet />
    </AppShell>
  );
}
