import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// El backoffice se sirve bajo /backoffice/ y habla con la API a través del BFF (/bff/bo).
export default defineConfig({
  base: '/backoffice/',
  plugins: [react()],
  server: {
    port: 5174,
    proxy: {
      '/bff': 'http://localhost:3000',
    },
  },
});
