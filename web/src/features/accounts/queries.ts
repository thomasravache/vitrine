import { queryOptions, useSuspenseQuery } from "@tanstack/react-query";
import {
  listMercadoLivreAccounts,
  type MercadoLivreAccountResponse,
} from "../../api/generated/vitrine.ts";
import { meQuery } from "../../api/session.ts";

/** Contas do Mercado Livre do workspace (SPEC-0020: GET /mercadolivre/accounts). */
export const accountsQuery = queryOptions({
  queryKey: ["mercadolivre", "accounts"],
  queryFn: async (): Promise<MercadoLivreAccountResponse[]> => {
    const response = await listMercadoLivreAccounts();
    if (response.status === 200) {
      return response.data;
    }
    throw new Error(`GET das contas do Mercado Livre respondeu ${response.status}.`);
  },
});

export const ACCOUNT_STATUS = {
  connected: "connected",
  reauthorizationRequired: "reauthorization_required",
} as const;

/** O usuário da sessão é dono (Owner) do workspace: só ele conecta, reconecta e desconecta contas. */
export function useIsOwner(): boolean {
  const { data: me } = useSuspenseQuery(meQuery);
  return me?.workspace.role === "Owner";
}

/** Texto para quem não é dono do workspace (SPEC-0025 §6, emenda v1.1). */
export const MEMBER_NOTE = "Só o dono do workspace pode conectar ou desconectar contas.";
