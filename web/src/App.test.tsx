import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { App } from "./App.tsx";

describe("App", () => {
  it("SPEC-0003:UT-02 — renderiza o componente raiz com o título Vitrine", () => {
    render(<App />);

    expect(screen.getByRole("heading", { level: 1, name: "Vitrine" })).toBeDefined();
  });
});
