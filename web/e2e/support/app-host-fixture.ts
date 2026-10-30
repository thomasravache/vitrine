import { spawn } from "node:child_process";
import { once } from "node:events";
import path from "node:path";
import { createInterface } from "node:readline";

/** Aplicação subida pelo AppHost (Aspire.Hosting.Testing) para o E2E. */
export interface AppHostHandle {
  /** URL pública da SPA servida pela aplicação. */
  baseURL: string;
  /** URL do simulador do Mercado Livre (SPEC-0024), também exportada em `process.env.ML_SIMULATOR_URL`. */
  mlSimulatorURL: string | undefined;
  /** Para a aplicação e libera os recursos. */
  stop: () => Promise<void>;
}

// Primeira execução compila AppHost + Api e pode baixar a imagem do PostgreSQL.
const STARTUP_TIMEOUT_MS = 5 * 60_000;
const STOP_TIMEOUT_MS = 60_000;
const BASE_URL_LINE = /^BASE_URL=(?<url>\S+)$/;
const ML_SIMULATOR_URL_LINE = /^ML_SIMULATOR_URL=(?<url>\S+)$/;

/**
 * AppHostFixture: roda `AppHostFixture.cs` (app de arquivo único do .NET), que sobe o AppHost com
 * Aspire.Hosting.Testing e escreve `ML_SIMULATOR_URL=<url>` e depois `BASE_URL=<url>` quando a SPA
 * responde. A URL do simulador do Mercado Livre (SPEC-0024) é exportada em `process.env.ML_SIMULATOR_URL`
 * (herdada pelos workers do Playwright). Fechar a entrada padrão do processo derruba a aplicação.
 */
export async function startAppHost(): Promise<AppHostHandle> {
  const fixture = path.join(import.meta.dirname, "AppHostFixture.cs");
  const child = spawn("dotnet", ["run", "--file", fixture], {
    cwd: import.meta.dirname,
    stdio: ["pipe", "pipe", "inherit"],
  });
  const exited = once(child, "exit");

  const stop = async (): Promise<void> => {
    if (child.exitCode !== null || child.signalCode !== null) {
      return;
    }

    child.stdin.end();
    const timer = setTimeout(() => child.kill("SIGKILL"), STOP_TIMEOUT_MS);
    await exited;
    clearTimeout(timer);
  };

  try {
    const { baseURL, mlSimulatorURL } = await readUrls(child.stdout, exited);
    if (mlSimulatorURL) {
      process.env.ML_SIMULATOR_URL = mlSimulatorURL;
    }
    return { baseURL, mlSimulatorURL, stop };
  } catch (error) {
    await stop();
    throw error;
  }
}

async function readUrls(
  stdout: NodeJS.ReadableStream,
  exited: Promise<unknown>,
): Promise<{ baseURL: string; mlSimulatorURL: string | undefined }> {
  const lines = createInterface({ input: stdout });
  const found = (async () => {
    let mlSimulatorURL: string | undefined;
    for await (const line of lines) {
      const simulator = ML_SIMULATOR_URL_LINE.exec(line)?.groups?.url;
      if (simulator) {
        mlSimulatorURL = simulator;
        continue;
      }
      const baseURL = BASE_URL_LINE.exec(line)?.groups?.url;
      if (baseURL) {
        return { baseURL, mlSimulatorURL };
      }
      console.log(`[AppHost] ${line}`);
    }
    throw new Error("AppHostFixture terminou sem informar BASE_URL.");
  })();

  let timer: NodeJS.Timeout | undefined;
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(
      () => reject(new Error(`AppHost não ficou pronto em ${STARTUP_TIMEOUT_MS / 1000} s.`)),
      STARTUP_TIMEOUT_MS,
    );
  });
  const crashed = exited.then(() => {
    throw new Error("AppHostFixture encerrou antes de informar BASE_URL (veja a saída acima).");
  });

  try {
    return await Promise.race([found, timeout, crashed]);
  } finally {
    clearTimeout(timer);
  }
}
