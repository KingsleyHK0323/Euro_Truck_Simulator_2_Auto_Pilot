// Edited by: OpenAI-Codex_Responsible: Website
import { defineConfig } from "astro/config";
import { readFileSync } from "node:fs";

const registry = JSON.parse(readFileSync(new URL("./src/i18n/locales.json", import.meta.url), "utf8"));

export default defineConfig({
  site: "https://kingsleyhk0323.github.io",
  base: "/Euro_Truck_Simulator_2_Auto_Pilot/",
  output: "static",
  trailingSlash: "always",
  i18n: {
    defaultLocale: registry.default,
    locales: registry.locales.filter((locale) => locale.enabled).map((locale) => locale.code),
    routing: {
      prefixDefaultLocale: true,
    },
  },
});
