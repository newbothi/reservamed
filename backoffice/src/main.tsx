import { StrictMode, useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Navigate, NavLink, Route, Routes } from 'react-router-dom';
import { api, EVENTO_SESION_TERMINADA, fijarSesion, Sesion } from './api';
import { Login } from './Acceso';
import Agenda from './paginas/Agenda';
import Admision from './paginas/Admision';
import Pacientes from './paginas/Pacientes';
import Auditoria from './paginas/Auditoria';
import Modelos from './paginas/Modelos';
import Usuarios from './paginas/Usuarios';
import Seguridad from './paginas/Seguridad';
import MiCuenta from './paginas/MiCuenta';
import './estilos.css';

function App() {
  // undefined: consultando al BFF si hay una sesión viva en la cookie.
  const [sesion, setSesionEstado] = useState<Sesion | null | undefined>(undefined);
  const setSesion = (s: Sesion | null) => {
    fijarSesion(s);
    setSesionEstado(s);
  };

  useEffect(() => {
    api<{ usuario: Sesion }>('/auth/sesion')
      .then((r) => setSesion(r.usuario))
      .catch(() => setSesion(null));
    const terminar = () => setSesion(null);
    window.addEventListener(EVENTO_SESION_TERMINADA, terminar);
    return () => window.removeEventListener(EVENTO_SESION_TERMINADA, terminar);
  }, []);

  if (sesion === undefined) return <main className="contenedor"><p className="sutil">Cargando…</p></main>;
  if (!sesion) return <Login alEntrar={setSesion} />;

  const staff = sesion.rol === 'RECEPCION' || sesion.rol === 'ADMIN';
  const salir = async () => {
    try {
      await api('/auth/salir', { cuerpo: {} });
    } finally {
      setSesion(null);
    }
  };

  return (
    <BrowserRouter basename="/backoffice">
      <header className="cabecera">
        <span className="logo">Reserva<span>Med</span> <small className="sutil">backoffice</small></span>
        <nav>
          <NavLink to="/" end>Agenda</NavLink>
          {staff && <NavLink to="/pacientes">Pacientes</NavLink>}
          {sesion.rol === 'ADMIN' && <NavLink to="/ml">Machine learning</NavLink>}
          {sesion.rol === 'ADMIN' && <NavLink to="/auditoria">Auditoría</NavLink>}
          {sesion.rol === 'ADMIN' && <NavLink to="/usuarios">Usuarios</NavLink>}
          {sesion.rol === 'ADMIN' && <NavLink to="/seguridad">Seguridad</NavLink>}
          <NavLink to="/mi-cuenta">Mi cuenta</NavLink>
          <button className="link" onClick={salir}>Salir ({sesion.nombre})</button>
        </nav>
      </header>
      <main className="contenedor ancho">
        <Routes>
          <Route path="/" element={<Agenda />} />
          {staff && <Route path="/pacientes" element={<Pacientes />} />}
          {staff && <Route path="/admision/:id" element={<Admision />} />}
          {sesion.rol === 'ADMIN' && <Route path="/ml" element={<Modelos />} />}
          {sesion.rol === 'ADMIN' && <Route path="/auditoria" element={<Auditoria />} />}
          {sesion.rol === 'ADMIN' && <Route path="/usuarios" element={<Usuarios />} />}
          {sesion.rol === 'ADMIN' && <Route path="/seguridad" element={<Seguridad />} />}
          <Route path="/mi-cuenta" element={<MiCuenta />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
    </BrowserRouter>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
