import { startAppHost } from "./support/app-host-fixture.ts";

// Sem BASE_URL, sobe a aplicação pelo AppHost (AppHostFixture) e exporta BASE_URL para os workers.
export default async function globalSetup(): Promise<(() => Promise<void>) | undefined> {
  if (process.env.BASE_URL) {
    return undefined;
  }

  const appHost = await startAppHost();
  // SPEC-0025: com o AppHost, o simulador do ML sempre sobe junto. Sem a URL dele, a jornada conectar-conta viraria
  // skip (inclusive o @smoke) e o E2E passaria verde sem prová-la; o skip só vale com BASE_URL externo.
  if (!appHost.mlSimulatorURL) {
    await appHost.stop();
    throw new Error(
      "AppHostFixture não informou ML_SIMULATOR_URL: o simulador do Mercado Livre (recurso ml-simulator) " +
        "não subiu ou mudou de nome.",
    );
  }
  process.env.BASE_URL = appHost.baseURL;
  return appHost.stop;
}
