import type { QueryClient } from "@tanstack/react-query";
import {
  createRootRouteWithContext,
  createRoute,
  createRouter,
  Outlet,
  redirect,
} from "@tanstack/react-router";
import { App } from "./App.tsx";
import { meQuery } from "./api/session.ts";
import { AccountsPage } from "./features/accounts/AccountsPage.tsx";
import { ConnectionReturnPage } from "./features/accounts/ConnectionReturnPage.tsx";
import { LoginPage } from "./features/auth/LoginPage.tsx";
import { SignupPage } from "./features/auth/SignupPage.tsx";
import { AuthenticatedLayout } from "./features/dashboard/AuthenticatedLayout.tsx";
import { DashboardPage } from "./features/dashboard/DashboardPage.tsx";
import { ListingsPage, validateListingsSearch } from "./features/listings/ListingsPage.tsx";
import { NotFoundPage } from "./NotFoundPage.tsx";

type RouterContext = { queryClient: QueryClient };

const rootRoute = createRootRouteWithContext<RouterContext>()({
  component: Outlet,
  notFoundComponent: NotFoundPage,
});

const homeRoute = createRoute({ getParentRoute: () => rootRoute, path: "/", component: App });

// Com sessão ativa, as telas de acesso levam direto ao painel.
async function redirectIfSignedIn({ context }: { context: RouterContext }) {
  if (await context.queryClient.ensureQueryData(meQuery)) {
    throw redirect({ to: "/painel" });
  }
}

const signupRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/cadastro",
  beforeLoad: redirectIfSignedIn,
  component: SignupPage,
});

const loginRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/entrar",
  beforeLoad: redirectIfSignedIn,
  component: LoginPage,
});

// Rotas protegidas (moldura do AppShell): sem sessão (GET /api/v1/me → 401), vão para /entrar.
const authenticatedRoute = createRoute({
  getParentRoute: () => rootRoute,
  id: "autenticado",
  beforeLoad: async ({ context }) => {
    if (!(await context.queryClient.ensureQueryData(meQuery))) {
      throw redirect({ to: "/entrar" });
    }
  },
  component: AuthenticatedLayout,
});

const dashboardRoute = createRoute({
  getParentRoute: () => authenticatedRoute,
  path: "/painel",
  component: DashboardPage,
});

const accountsRoute = createRoute({
  getParentRoute: () => authenticatedRoute,
  path: "/contas",
  component: AccountsPage,
});

const connectionReturnRoute = createRoute({
  getParentRoute: () => authenticatedRoute,
  path: "/contas/mercado-livre/retorno",
  component: ConnectionReturnPage,
});

const listingsRoute = createRoute({
  getParentRoute: () => authenticatedRoute,
  path: "/anuncios",
  validateSearch: validateListingsSearch,
  component: function ListingsRoute() {
    return <ListingsPage search={listingsRoute.useSearch()} />;
  },
});

const routeTree = rootRoute.addChildren([
  homeRoute,
  signupRoute,
  loginRoute,
  authenticatedRoute.addChildren([
    dashboardRoute,
    accountsRoute,
    connectionReturnRoute,
    listingsRoute,
  ]),
]);

// Search params como texto simples (?status=paused&q=fone), sem a serialização JSON padrão do router: o que se
// digita na busca chega igual na Api, e a URL fica legível e compartilhável.
function parseSearch(search: string): Record<string, unknown> {
  return Object.fromEntries(new URLSearchParams(search));
}

function stringifySearch(search: Record<string, unknown>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(search)) {
    if (value !== undefined && value !== null && value !== "") {
      params.set(key, String(value));
    }
  }
  const text = params.toString();
  return text ? `?${text}` : "";
}

export function createAppRouter(queryClient: QueryClient) {
  return createRouter({
    routeTree,
    context: { queryClient },
    defaultPreload: false,
    parseSearch,
    stringifySearch,
  });
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }

  interface HistoryState {
    /** Aviso de sucesso em /contas depois do retorno do OAuth (SPEC-0025). */
    connectedNickname?: string;
  }
}
