import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// Em desenvolvimento (AppHost), /api vai para o host Vitrine.Api pelo proxy do Vite: a SPA continua na mesma
// origem da API, como em produção, onde o próprio host serve os arquivos estáticos (ADR-0013, ADR-0007).
const apiUrl = process.env.services__api__https__0 ?? process.env.services__api__http__0;

// Fontes nunca viram data: URI no CSS: a CSP estrita da Api (default-src 'self', sem font-src data: — SPEC-0008)
// bloquearia o carregamento. Demais assets seguem o limite padrão do Vite.
const FONT_FILE = /\.(woff2?|ttf|otf|eot)$/i;

export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    assetsInlineLimit: (filePath) => (FONT_FILE.test(filePath) ? false : undefined),
  },
  server: apiUrl ? { proxy: { "/api": { target: apiUrl } } } : {},
});
