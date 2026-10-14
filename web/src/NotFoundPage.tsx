import { Link } from "@tanstack/react-router";
import { useDocumentTitle } from "./ui/useDocumentTitle.ts";

export function NotFoundPage() {
  useDocumentTitle("Página não encontrada");
  return (
    <main className="flex min-h-svh flex-col items-center justify-center gap-4 p-6 text-center">
      <h1 className="text-3xl font-bold">Página não encontrada</h1>
      <Link to="/" className="font-semibold text-primary underline underline-offset-2">
        Voltar ao início
      </Link>
    </main>
  );
}
