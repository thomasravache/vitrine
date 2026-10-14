import { useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { type FormEvent, useState } from "react";
import { register } from "../../api/generated/vitrine.ts";
import { meQuery, type Problem } from "../../api/session.ts";
import { ErrorAlert } from "../../ui/Alert.tsx";
import { Button } from "../../ui/Button.tsx";
import { ArrowRightIcon, BoltIcon } from "../../ui/icons.tsx";
import { TextField } from "../../ui/TextField.tsx";
import { useDocumentTitle } from "../../ui/useDocumentTitle.ts";
import { AuthLayout, footerLinkClass } from "./AuthLayout.tsx";
import { focusFirstInvalid } from "./focusFirstInvalid.ts";

type Field = "name" | "email" | "password";
type FieldErrors = Partial<Record<Field, string>>;

export function SignupPage() {
  useDocumentTitle("Criar conta");
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [errors, setErrors] = useState<FieldErrors>({});
  const [formError, setFormError] = useState<string>();
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    setSubmitting(true);
    setFormError(undefined);
    try {
      const response = await register({
        name: String(data.get("name") ?? ""),
        email: String(data.get("email") ?? ""),
        password: String(data.get("password") ?? ""),
      });
      if (response.status === 201) {
        queryClient.removeQueries({ queryKey: meQuery.queryKey });
        await navigate({ to: "/painel" });
        return;
      }

      const problem = response.data as Problem;
      const next = fieldErrors(response.status, problem);
      setErrors(next);
      if (Object.keys(next).length === 0) {
        setFormError(
          response.status === 404
            ? "O cadastro não está aberto no momento."
            : "Não foi possível criar sua conta. Tente de novo.",
        );
      }
      focusFirstInvalid(form);
    } catch {
      setFormError("Não foi possível falar com o Vitrine. Verifique sua conexão e tente de novo.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <AuthLayout
      footer={
        <>
          Já tem conta?
          <Link to="/entrar" className={footerLinkClass}>
            Entrar
          </Link>
        </>
      }
    >
      <p className="mb-4 inline-flex items-center gap-2 rounded-full bg-primary-soft px-3 py-1.5 text-sm font-semibold text-primary">
        <BoltIcon className="size-4" />
        Configuração em menos de 1 minuto
      </p>
      <h1 className="text-3xl font-bold tracking-tight lg:text-[1.75rem]">Crie sua conta</h1>
      <p className="mt-2 text-lg text-muted lg:text-base">
        Seus anúncios melhores e perguntas respondidas, a qualquer hora.
      </p>

      <form noValidate onSubmit={handleSubmit} className="mt-8 flex flex-col gap-5">
        <TextField
          label="Seu nome"
          name="name"
          autoComplete="name"
          placeholder="Como prefere ser chamado?"
          error={errors.name}
        />
        <TextField
          label="E-mail"
          name="email"
          type="email"
          autoComplete="email"
          inputMode="email"
          placeholder="voce@loja.com.br"
          error={errors.email}
        />
        <TextField
          label="Senha"
          name="password"
          type="password"
          autoComplete="new-password"
          hint="Mínimo de 10 caracteres"
          error={errors.password}
        />
        {formError && <ErrorAlert>{formError}</ErrorAlert>}
        <Button type="submit" disabled={submitting} className="mt-2 w-full">
          Criar conta
          <ArrowRightIcon className="size-5" />
        </Button>
        <p className="text-center text-sm text-muted">
          Ao continuar você concorda com os Termos de Uso e a Política de Privacidade.
        </p>
      </form>
    </AuthLayout>
  );
}

function fieldErrors(status: number, problem: Problem | undefined): FieldErrors {
  if (status === 409) {
    return { email: "Este e-mail já está cadastrado." };
  }
  if (status !== 400 || !problem?.errors) {
    return {};
  }

  const errors: FieldErrors = {};
  for (const field of ["name", "email", "password"] as const) {
    const message = problem.errors[field]?.[0];
    if (message) {
      errors[field] = message;
    }
  }
  return errors;
}
