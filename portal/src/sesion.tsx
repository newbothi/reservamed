import { createContext, ReactNode, useContext, useState } from 'react';
import { guardarSesion, leerSesion, Sesion } from './api';

interface Ctx {
  sesion: Sesion | null;
  entrar: (s: Sesion) => void;
  salir: () => void;
}

const SesionContext = createContext<Ctx>({ sesion: null, entrar: () => {}, salir: () => {} });

export function ProveedorSesion({ children }: { children: ReactNode }) {
  const [sesion, setSesion] = useState<Sesion | null>(leerSesion);
  const entrar = (s: Sesion) => {
    guardarSesion(s);
    setSesion(s);
  };
  const salir = () => {
    guardarSesion(null);
    setSesion(null);
  };
  return <SesionContext.Provider value={{ sesion, entrar, salir }}>{children}</SesionContext.Provider>;
}

export const useSesion = () => useContext(SesionContext);
