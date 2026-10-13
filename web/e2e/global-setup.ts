import { startAppHost } from "./support/app-host-fixture.ts";

// Sem BASE_URL, sobe a aplicação pelo AppHost (AppHostFixture) e exporta BASE_URL para os workers.
export default async function globalSetup(): Promise<(() => Promise<void>) | undefined> {
  if (process.env.BASE_URL) {
    return undefined;
  }

  const appHost = await startAppHost();
  process.env.BASE_URL = appHost.baseURL;
  return appHost.stop;
}
