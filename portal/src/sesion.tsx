import { createContext, ReactNode, useCallback, useContext, useEffect, useState } from 'react';
import { api, EVENTO_SESION_TERMINADA, Usuario } from './api';

interface Ctx {
  /** undefined mientras se consulta al BFF; null sin sesión. */
  sesion: Usuario | null | undefined;
  entrar: (u: Usuario | null) => void;
  salir: () => Promise<void>;
}

const SesionContext = createContext<Ctx>({ sesion: undefined, entrar: () => {}, salir: async () => {} });

/** La sesión real está en la cookie HttpOnly; aquí solo se guarda quién es para pintar la interfaz. */
export function ProveedorSesion({ children }: { children: ReactNode }) {
  const [sesion, setSesion] = useState<Usuario | null | undefined>(undefined);

  useEffect(() => {
    api<{ usuario: Usuario }>('/auth/sesion')
      .then((r) => setSesion(r.usuario))
      .catch(() => setSesion(null));
    const terminar = () => setSesion(null);
    window.addEventListener(EVENTO_SESION_TERMINADA, terminar);
    return () => window.removeEventListener(EVENTO_SESION_TERMINADA, terminar);
  }, []);

  const salir = useCallback(async () => {
    try {
      await api('/auth/salir', { cuerpo: {} });
    } finally {
      setSesion(null);
    }
  }, []);

  return <SesionContext.Provider value={{ sesion, entrar: setSesion, salir }}>{children}</SesionContext.Provider>;
}

export const useSesion = () => useContext(SesionContext);
