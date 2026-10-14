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
import { LoginPage } from "./features/auth/LoginPage.tsx";
import { SignupPage } from "./features/auth/SignupPage.tsx";
import { DashboardPage } from "./features/dashboard/DashboardPage.tsx";
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

// Rota protegida: sem sessão (GET /api/v1/me → 401), vai para /entrar.
const dashboardRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/painel",
  beforeLoad: async ({ context }) => {
    if (!(await context.queryClient.ensureQueryData(meQuery))) {
      throw redirect({ to: "/entrar" });
    }
  },
  component: DashboardPage,
});

const routeTree = rootRoute.addChildren([homeRoute, signupRoute, loginRoute, dashboardRoute]);

export function createAppRouter(queryClient: QueryClient) {
  return createRouter({ routeTree, context: { queryClient }, defaultPreload: false });
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
