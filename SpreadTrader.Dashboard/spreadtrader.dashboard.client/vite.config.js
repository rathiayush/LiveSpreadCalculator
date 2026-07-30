import { fileURLToPath, URL } from 'node:url';

import { defineConfig } from 'vite';
import plugin from '@vitejs/plugin-react';
import fs from 'fs';
import path from 'path';
import child_process from 'child_process';
import { env } from 'process';

// https://vitejs.dev/config/
//
// Function form, not the plain-object form this started as - defineConfig calls this once per
// Vite invocation with `command` set to 'serve' (the dev server, `npm run dev`) or 'build'
// (`npm run build` / `vite build`, what CI and every Docker build here use). Everything below
// that only makes sense for a live dev server - the HTTPS dev certificate and the proxy to a
// locally running backend - is gated on `command === 'serve'` so a production build never runs
// it. Previously this file ran that certificate/proxy setup unconditionally at module load, which
// meant even `vite build` tried to invoke `dotnet dev-certs https` - inside the Docker build
// stage (node:20-alpine, no dotnet SDK at all) that spawnSync call fails outright, throwing
// "Could not create certificate." and failing the whole image build. That's not a hypothetical -
// it's what happened the first time this repo's Dockerfile was actually built end-to-end. The
// production build has no use for a dev proxy anyway: the built app is served by the .NET backend
// itself from its own wwwroot (see LiveSpreadCalculator/Dockerfile), so relative '/api'/'/hubs'
// paths already resolve correctly with no proxy involved.
export default defineConfig(({ command }) => {
    const isServe = command === 'serve';

    let httpsConfig;
    let proxyTarget;

    if (isServe) {
        const baseFolder =
            env.APPDATA !== undefined && env.APPDATA !== ''
                ? `${env.APPDATA}/ASP.NET/https`
                : `${env.HOME}/.aspnet/https`;

        const certificateName = "spreadtrader.dashboard.client";
        const certFilePath = path.join(baseFolder, `${certificateName}.pem`);
        const keyFilePath = path.join(baseFolder, `${certificateName}.key`);

        if (!fs.existsSync(baseFolder)) {
            fs.mkdirSync(baseFolder, { recursive: true });
        }

        if (!fs.existsSync(certFilePath) || !fs.existsSync(keyFilePath)) {
            if (0 !== child_process.spawnSync('dotnet', [
                'dev-certs',
                'https',
                '--export-path',
                certFilePath,
                '--format',
                'Pem',
                '--no-password',
            ], { stdio: 'inherit', }).status) {
                throw new Error("Could not create certificate.");
            }
        }

        httpsConfig = {
            key: fs.readFileSync(keyFilePath),
            cert: fs.readFileSync(certFilePath),
        };

        // Points at the LiveSpreadCalculator Api project (see its Properties/launchSettings.json -
        // the "https" profile listens on 7105). Override with ASPNETCORE_HTTPS_PORT / ASPNETCORE_URLS
        // if you change that port.
        proxyTarget = env.ASPNETCORE_HTTPS_PORT ? `https://localhost:${env.ASPNETCORE_HTTPS_PORT}` :
            env.ASPNETCORE_URLS ? env.ASPNETCORE_URLS.split(';')[0] : 'https://localhost:7105';
    }

    return {
        plugins: [plugin()],
        resolve: {
            alias: {
                '@': fileURLToPath(new URL('./src', import.meta.url))
            }
        },
        server: isServe ? {
            proxy: {
                // Everything the dashboard needs from the backend - REST endpoints and the
                // SignalR hub - is proxied here so client code can use plain relative paths
                // ('/api/...', '/hubs/spreads') in both dev and prod (behind a shared ingress).
                '^/api': {
                    target: proxyTarget,
                    secure: false,
                    changeOrigin: true,
                },
                '^/hubs': {
                    target: proxyTarget,
                    secure: false,
                    changeOrigin: true,
                    ws: true,
                },
            },
            port: parseInt(env.DEV_SERVER_PORT || '63061'),
            https: httpsConfig,
        } : undefined,
    };
});
