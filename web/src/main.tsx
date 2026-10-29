import "@fontsource-variable/inter";
import "@fontsource-variable/plus-jakarta-sans";
import "./index.css";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "@tanstack/react-router";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { initSentry } from "./observability/sentry.ts";
import { createAppRouter } from "./router.tsx";

// Antes de montar a aplicação, para capturar erros desde a primeira renderização (opcional por VITE_SENTRY_DSN).
initSentry();

const container = document.getElementById("root");
if (!container) {
  throw new Error('Elemento "#root" não encontrado em index.html.');
}

const queryClient = new QueryClient();
const router = createAppRouter(queryClient);

createRoot(container).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
);
