import { useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { type FormEvent, useState } from "react";
import { login } from "../../api/generated/vitrine.ts";
import { meQuery, type Problem } from "../../api/session.ts";
import { ErrorAlert } from "../../ui/Alert.tsx";
import { Button } from "../../ui/Button.tsx";
import { ArrowRightIcon, LockIcon } from "../../ui/icons.tsx";
import { TextField } from "../../ui/TextField.tsx";
import { useDocumentTitle } from "../../ui/useDocumentTitle.ts";
import { AuthLayout, footerLinkClass } from "./AuthLayout.tsx";

export function LoginPage() {
  useDocumentTitle("Entrar");
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [error, setError] = useState<string>();
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    setSubmitting(true);
    setError(undefined);
    try {
      const response = await login({
        email: String(data.get("email") ?? ""),
        password: String(data.get("password") ?? ""),
      });
      if (response.status === 204) {
        queryClient.removeQueries({ queryKey: meQuery.queryKey });
        await navigate({ to: "/painel" });
        return;
      }
      setError(loginError(response.status, response.data as Problem));
    } catch {
      setError("Não foi possível falar com o Vitrine. Verifique sua conexão e tente de novo.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <AuthLayout
      footer={
        <>
          Ainda não tem conta?
          <Link to="/cadastro" className={footerLinkClass}>
            Criar conta
          </Link>
        </>
      }
    >
      <h1 className="text-3xl font-bold tracking-tight lg:text-[1.75rem]">
        Que bom te ver de novo
      </h1>
      <p className="mt-2 text-lg text-muted lg:text-base">
        Acesse seus anúncios e perguntas do Mercado Livre.
      </p>

      <form noValidate onSubmit={handleSubmit} className="mt-8 flex flex-col gap-5">
        <TextField
          label="E-mail"
          name="email"
          type="email"
          autoComplete="email"
          inputMode="email"
        />
        <div className="flex flex-col gap-3">
          <TextField
            label="Senha"
            name="password"
            type="password"
            autoComplete="current-password"
          />
          <p className="flex items-center gap-2 text-sm text-muted">
            <button type="button" disabled className="cursor-not-allowed">
              Esqueci minha senha
            </button>
            <span className="rounded-md bg-primary-soft px-2 py-0.5 text-xs font-semibold tracking-wide text-muted uppercase">
              Em breve
            </span>
          </p>
        </div>
        {error && <ErrorAlert>{error}</ErrorAlert>}
        <Button type="submit" disabled={submitting} className="w-full">
          Entrar
          <ArrowRightIcon className="size-5" />
        </Button>
        <p className="flex items-center justify-center gap-2 text-sm text-muted lg:hidden">
          <LockIcon className="size-4" />
          Conexão criptografada e segura
        </p>
      </form>
    </AuthLayout>
  );
}

function loginError(status: number, problem: Problem | undefined): string {
  if (status === 423) {
    const minutes = Math.max(1, Math.ceil((problem?.retryAfterSeconds ?? 900) / 60));
    return `Muitas tentativas seguidas. Tente de novo em ${minutes} ${minutes === 1 ? "minuto" : "minutos"}.`;
  }
  if (status === 401) {
    return "E-mail ou senha incorretos.";
  }
  return "Não foi possível entrar. Tente de novo.";
}
