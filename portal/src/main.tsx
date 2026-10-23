import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Link, Navigate, NavLink, Route, Routes } from 'react-router-dom';
import { ReactNode } from 'react';
import { ProveedorSesion, useSesion } from './sesion';
import Buscar from './paginas/Buscar';
import Reservar from './paginas/Reservar';
import MisCitas from './paginas/MisCitas';
import RetornoPago from './paginas/RetornoPago';
import { Login, Registro } from './paginas/Acceso';
import './estilos.css';

function Privada({ children }: { children: ReactNode }) {
  const { sesion } = useSesion();
  return sesion ? <>{children}</> : <Navigate to={`/login?volver=${encodeURIComponent(location.pathname + location.search)}`} replace />;
}

function Cabecera() {
  const { sesion, salir } = useSesion();
  return (
    <header className="cabecera">
      <Link to="/" className="logo">Reserva<span>Med</span></Link>
      <nav>
        <NavLink to="/" end>Reservar hora</NavLink>
        {sesion && <NavLink to="/mis-citas">Mis citas</NavLink>}
        {sesion ? (
          <button className="link" onClick={salir}>Salir ({sesion.nombre})</button>
        ) : (
          <NavLink to="/login">Ingresar</NavLink>
        )}
      </nav>
    </header>
  );
}

function App() {
  return (
    <BrowserRouter>
      <ProveedorSesion>
        <Cabecera />
        <main className="contenedor">
          <Routes>
            <Route path="/" element={<Buscar />} />
            <Route path="/login" element={<Login />} />
            <Route path="/registro" element={<Registro />} />
            <Route path="/reservar" element={<Privada><Reservar /></Privada>} />
            <Route path="/mis-citas" element={<Privada><MisCitas /></Privada>} />
            <Route path="/pago/retorno" element={<Privada><RetornoPago /></Privada>} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </main>
        <footer className="pie">ReservaMed · proyecto de portafolio · datos ficticios</footer>
      </ProveedorSesion>
    </BrowserRouter>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
