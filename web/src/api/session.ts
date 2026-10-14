import { queryOptions } from "@tanstack/react-query";
import { getMe, type MeResponse, type ProblemDetails } from "./generated/vitrine.ts";

/** Problem Details do Vitrine: sempre com `code`; validação traz `errors`, bloqueio traz `retryAfterSeconds`. */
export type Problem = ProblemDetails & {
  code?: string;
  errors?: Record<string, string[]>;
  retryAfterSeconds?: number;
};

/** "Quem sou eu": o usuário da sessão, ou `null` sem sessão (401). */
export const meQuery = queryOptions({
  queryKey: ["me"],
  queryFn: async (): Promise<MeResponse | null> => {
    const response = await getMe();
    if (response.status === 200) {
      return response.data;
    }
    if (response.status === 401) {
      return null;
    }
    throw new Error("GET /api/v1/me respondeu com um status inesperado.");
  },
  staleTime: 60_000,
  retry: false,
});
