import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react-swc';
import path from 'path';
const nodeProcess = (globalThis as typeof globalThis & { process?: { env?: Record<string, string | undefined> } }).process;
const appApiProxy = nodeProcess?.env?.APP_API_PROXY ?? 'http://127.0.0.1:8787';
export default defineConfig({ base: '/', plugins: [react()], resolve: { alias: { '@': path.resolve(__dirname, './src') } }, server: { proxy: { '/api': appApiProxy, '/auth': appApiProxy } } });
