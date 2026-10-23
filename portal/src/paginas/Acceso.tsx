import { FormEvent, useEffect, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { api, ErrorApi, formatearRut, Sesion } from '../api';
import { useSesion } from '../sesion';
import type { Catalogo } from './Buscar';

function useDestino() {
  const [params] = useSearchParams();
  return params.get('volver') ?? '/mis-citas';
}

export function Login() {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const { entrar } = useSesion();
  const navegar = useNavigate();
  const destino = useDestino();

  const enviar = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    try {
      const s = await api<Sesion>('/auth/login', { cuerpo: { email, password } });
      if (s.rol !== 'PACIENTE') {
        setError('Esta cuenta es del personal de la clínica: ingrese por el backoffice.');
        return;
      }
      entrar(s);
      navegar(destino, { replace: true });
    } catch (err) {
      setError((err as Error).message);
    }
  };

  return (
    <form className="tarjeta centrada formulario" onSubmit={enviar}>
      <h1>Ingresar</h1>
      <label>Correo<input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required /></label>
      <label>Contraseña<input type="password" value={password} onChange={(e) => setPassword(e.target.value)} required /></label>
      {error && <p className="error">{error}</p>}
      <button type="submit">Ingresar</button>
      <p className="sutil">¿No tiene cuenta? <Link to={`/registro?volver=${encodeURIComponent(destino)}`}>Regístrese</Link></p>
      <p className="sutil demo">Demo: ignacio@demo.cl (Colmena, carga) · martina@demo.cl (Fonasa C) · benjamin@demo.cl (Fonasa A) · sofia@demo.cl (particular) — clave Demo1234!</p>
    </form>
  );
}

export function Registro() {
  const [catalogo, setCatalogo] = useState<Catalogo | null>(null);
  const [f, setF] = useState({
    rut: '', nombres: '', apellidoPaterno: '', apellidoMaterno: '', fechaNacimiento: '', sexo: 'F',
    email: '', telefono: '', comuna: '', password: '',
    financiadorId: '', planSaludId: '', tramoFonasa: '', esCarga: false, afiliadoRut: '', afiliadoNombre: '',
  });
  const [errores, setErrores] = useState<string[]>([]);
  const { entrar } = useSesion();
  const navegar = useNavigate();
  const destino = useDestino();

  useEffect(() => {
    api<Catalogo>('/catalogo').then(setCatalogo).catch(() => {});
  }, []);

  const fin = catalogo?.financiadores.find((x) => String(x.id) === f.financiadorId);
  const cambiar = (campo: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [campo]: e.target.value });

  const enviar = async (e: FormEvent) => {
    e.preventDefault();
    setErrores([]);
    try {
      const s = await api<Sesion>('/auth/registro', {
        cuerpo: {
          ...f,
          financiadorId: Number(f.financiadorId),
          planSaludId: f.planSaludId ? Number(f.planSaludId) : null,
          tramoFonasa: fin?.tipo === 'FONASA' ? f.tramoFonasa || null : null,
          esCarga: fin?.tipo !== 'PARTICULAR' && f.esCarga,
          afiliadoRut: f.esCarga ? f.afiliadoRut : null,
          afiliadoNombre: f.esCarga ? f.afiliadoNombre : null,
        },
      });
      entrar(s);
      navegar(destino, { replace: true });
    } catch (err) {
      const ea = err as ErrorApi;
      setErrores(ea.detalles?.map((d) => d.mensaje) ?? [ea.message]);
    }
  };

  return (
    <form className="tarjeta centrada formulario" onSubmit={enviar}>
      <h1>Crear cuenta</h1>
      <div className="grilla">
        <label>RUT<input value={f.rut} onChange={(e) => setF({ ...f, rut: formatearRut(e.target.value) })} placeholder="12.345.678-9" required /></label>
        <label>Fecha de nacimiento<input type="date" value={f.fechaNacimiento} onChange={cambiar('fechaNacimiento')} required /></label>
        <label>Nombres<input value={f.nombres} onChange={cambiar('nombres')} required /></label>
        <label>Apellido paterno<input value={f.apellidoPaterno} onChange={cambiar('apellidoPaterno')} required /></label>
        <label>Apellido materno<input value={f.apellidoMaterno} onChange={cambiar('apellidoMaterno')} /></label>
        <label>Sexo
          <select value={f.sexo} onChange={cambiar('sexo')}>
            <option value="F">Femenino</option><option value="M">Masculino</option><option value="X">Otro / no informa</option>
          </select>
        </label>
        <label>Correo<input type="email" value={f.email} onChange={cambiar('email')} required /></label>
        <label>Celular<input value={f.telefono} onChange={cambiar('telefono')} placeholder="+56 9 1234 5678" /></label>
        <label>Comuna<input value={f.comuna} onChange={cambiar('comuna')} /></label>
        <label>Contraseña<input type="password" value={f.password} onChange={cambiar('password')} minLength={8} required /></label>
      </div>

      <h2>Previsión de salud</h2>
      <div className="grilla">
        <label>Previsión
          <select value={f.financiadorId} onChange={(e) => setF({ ...f, financiadorId: e.target.value, planSaludId: '', tramoFonasa: '' })} required>
            <option value="">Seleccione…</option>
            {catalogo?.financiadores.map((x) => <option key={x.id} value={x.id}>{x.nombre}</option>)}
          </select>
        </label>
        {fin?.tipo === 'ISAPRE' && (
          <label>Plan
            <select value={f.planSaludId} onChange={cambiar('planSaludId')} required>
              <option value="">Seleccione…</option>
              {fin.planes.map((p) => <option key={p.id} value={p.id}>{p.nombre}</option>)}
            </select>
          </label>
        )}
        {fin?.tipo === 'FONASA' && (
          <label>Tramo Fonasa
            <select value={f.tramoFonasa} onChange={cambiar('tramoFonasa')} required>
              <option value="">Seleccione…</option>
              {['A', 'B', 'C', 'D'].map((t) => <option key={t} value={t}>Tramo {t}</option>)}
            </select>
          </label>
        )}
      </div>
      {fin && fin.tipo !== 'PARTICULAR' && (
        <>
          <label className="check">
            <input type="checkbox" checked={f.esCarga} onChange={(e) => setF({ ...f, esCarga: e.target.checked })} />
            Soy carga (el titular del plan es otra persona)
          </label>
          {f.esCarga && (
            <div className="grilla">
              <label>RUT del titular<input value={f.afiliadoRut} onChange={(e) => setF({ ...f, afiliadoRut: formatearRut(e.target.value) })} required /></label>
              <label>Nombre del titular<input value={f.afiliadoNombre} onChange={cambiar('afiliadoNombre')} required /></label>
            </div>
          )}
        </>
      )}

      {errores.map((x) => <p key={x} className="error">{x}</p>)}
      <button type="submit">Crear cuenta</button>
      <p className="sutil">Sus datos se tratan conforme a la Ley 19.628 y la Ley 20.584.</p>
    </form>
  );
}
