import { FormEvent, StrictMode, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Navigate, NavLink, Route, Routes } from 'react-router-dom';
import { api, guardarSesion, leerSesion, Sesion } from './api';
import Agenda from './paginas/Agenda';
import Admision from './paginas/Admision';
import Pacientes from './paginas/Pacientes';
import Auditoria from './paginas/Auditoria';
import Modelos from './paginas/Modelos';
import './estilos.css';

function Login({ alEntrar }: { alEntrar: (s: Sesion) => void }) {
  const [email, setEmail] = useState('recepcion@reservamed.cl');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');

  const enviar = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    try {
      const s = await api<Sesion>('/api/auth/login', { cuerpo: { email, password } });
      if (s.rol === 'PACIENTE') {
        setError('Los pacientes ingresan por el portal web.');
        return;
      }
      guardarSesion(s);
      alEntrar(s);
    } catch (err) {
      setError((err as Error).message);
    }
  };

  return (
    <main className="contenedor">
      <form className="tarjeta centrada formulario" onSubmit={enviar}>
        <h1>Backoffice clínica</h1>
        <label>Correo<input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required /></label>
        <label>Contraseña<input type="password" value={password} onChange={(e) => setPassword(e.target.value)} required /></label>
        {error && <p className="error">{error}</p>}
        <button type="submit">Ingresar</button>
        <p className="sutil demo">
          Demo (clave Demo1234!): recepcion@reservamed.cl (Providencia) · recepcion.lc@reservamed.cl (Las Condes) ·
          dra.soto@reservamed.cl (médico) · admin@reservamed.cl
        </p>
      </form>
    </main>
  );
}

function App() {
  const [sesion, setSesion] = useState<Sesion | null>(leerSesion);
  if (!sesion) return <Login alEntrar={setSesion} />;

  const staff = sesion.rol === 'RECEPCION' || sesion.rol === 'ADMIN';
  const salir = () => {
    guardarSesion(null);
    setSesion(null);
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
