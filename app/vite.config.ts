/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// In a browser (not the Tauri webview) the frontend reaches the engine through dwsim-devserver;
// `pnpm dev:engine` starts it on this port.
const devserver = "http://127.0.0.1:7878";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  clearScreen: false,
  server: {
    port: 1420,
    strictPort: true,
    proxy: {
      "/rpc": devserver,
      "/events": devserver,
      "/samples": devserver,
      "/cancel": devserver,
    },
  },
  envPrefix: ["VITE_", "TAURI_ENV_"],
  build: {
    target: "es2022",
    sourcemap: true,
  },
  test: {
    environment: "jsdom",
  },
});
