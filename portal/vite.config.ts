import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// En desarrollo, Vite reenvía /bff al BFF de Nest y /webpay-mock a la API Core.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/bff': 'http://localhost:3000',
      '/webpay-mock': 'http://localhost:5080',
    },
  },
});
