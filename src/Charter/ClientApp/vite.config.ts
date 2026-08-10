import { fileURLToPath, URL } from 'node:url';
import process from 'node:process';
import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

/**
 * Section 3.1: the frontend is bundled with the application, not deployed separately.
 *
 * - `npm run build` emits into `../wwwroot`, which the ASP.NET Core app serves as static files.
 *   The publish-time MSBuild target in Charter.csproj drives this.
 * - `npm run dev` is started for you by Microsoft.AspNetCore.SpaProxy when you run `dotnet run`,
 *   so HMR works from one command. The proxy entries below only matter if you hit the Vite dev
 *   server directly rather than going through Kestrel.
 */
const backendOrigin = process.env.CHARTER_BACKEND_ORIGIN ?? 'http://localhost:8080';
const devServerPort = Number(process.env.CHARTER_SPA_PORT ?? 5173);

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  build: {
    outDir: fileURLToPath(new URL('../wwwroot', import.meta.url)),
    emptyOutDir: true,
    sourcemap: true,
    reportCompressedSize: false,
  },
  server: {
    port: devServerPort,
    strictPort: true,
    proxy: {
      '/api': { target: backendOrigin, changeOrigin: true },
      '/health': { target: backendOrigin, changeOrigin: true },
      '/ready': { target: backendOrigin, changeOrigin: true },
      // SignalR hub for live session events (section 2.1), same port as HTTP.
      '/hub': { target: backendOrigin, changeOrigin: true, ws: true },
    },
  },
});
