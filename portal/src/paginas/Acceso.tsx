import { FormEvent, useEffect, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { api, destinoSeguro, ErrorApi, formatearRut, fortaleza, pruebaAntiBots, Usuario } from '../api';
import { useSesion } from '../sesion';
import type { Catalogo } from './Buscar';
import { CampoClave } from '../CampoClave';

function useDestino() {
  const [params] = useSearchParams();
  return destinoSeguro(params.get('volver'));   // B1: nunca a otro sitio
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
      const r = await api<{ usuario: Usuario }>('/auth/login', { cuerpo: { email, password } });
      entrar(r.usuario);
      navegar(destino, { replace: true });
    } catch (err) {
      const e = err as ErrorApi;
      if (e.codigo === 'CORREO_NO_VERIFICADO')
        setError('Debe activar su cuenta: le reenviamos el enlace a su correo.');
      else if (e.status === 429)
        setError('Demasiados intentos. Espere unos minutos antes de volver a intentar.');
      else setError(e.message);
    }
  };

  return (
    <form className="tarjeta centrada formulario" onSubmit={enviar}>
      <h1>Ingresar</h1>
      <label>Correo<input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required /></label>
      <label>Contraseña<CampoClave value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" required /></label>
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
    aceptaPrivacidad: false,
  });
  const [errores, setErrores] = useState<string[]>([]);
  const [enviando, setEnviando] = useState(false);
  const [listo, setListo] = useState('');

  useEffect(() => {
    api<Catalogo>('/catalogo').then(setCatalogo).catch(() => {});
  }, []);

  const fin = catalogo?.financiadores.find((x) => String(x.id) === f.financiadorId);
  const cambiar = (campo: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [campo]: e.target.value });

  const enviar = async (e: FormEvent) => {
    e.preventDefault();
    setErrores([]);
    setEnviando(true);
    try {
      const pow = await pruebaAntiBots();
      const r = await api<{ mensaje: string }>('/auth/registro', {
        cuerpo: {
          ...f,
          pow,
          financiadorId: Number(f.financiadorId),
          planSaludId: f.planSaludId ? Number(f.planSaludId) : null,
          tramoFonasa: fin?.tipo === 'FONASA' ? f.tramoFonasa || null : null,
          esCarga: fin?.tipo !== 'PARTICULAR' && f.esCarga,
          afiliadoRut: f.esCarga ? f.afiliadoRut : null,
          afiliadoNombre: f.esCarga ? f.afiliadoNombre : null,
        },
      });
      setListo(r.mensaje);
    } catch (err) {
      const ea = err as ErrorApi;
      setErrores(ea.detalles?.map((d) => d.mensaje) ?? [ea.message]);
    } finally {
      setEnviando(false);
    }
  };

  const fuerza = fortaleza(f.password, f.email);

  // B4: la misma respuesta exista o no una cuenta con ese correo o RUT.
  if (listo)
    return (
      <section className="tarjeta centrada">
        <h1>Revise su correo</h1>
        <p>{listo}</p>
        <p className="sutil">El enlace vence en 24 horas. Si no lo ve, revise la carpeta de spam.</p>
        <Link className="boton" to="/login">Ir a ingresar</Link>
      </section>
    );

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
        <label>Contraseña
          <CampoClave value={f.password} onChange={cambiar('password')} minLength={12} maxLength={128} autoComplete="new-password" required />
          {f.password && <span className={`medidor n${fuerza.nivel}`}>{fuerza.texto}</span>}
        </label>
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

      <label className="check">
        <input type="checkbox" checked={f.aceptaPrivacidad} onChange={(e) => setF({ ...f, aceptaPrivacidad: e.target.checked })} required />
        <span>Leí y acepto la <Link to="/privacidad" target="_blank" rel="noopener">política de privacidad</Link>: para qué usamos mis datos, por cuánto tiempo y cómo ejercer mis derechos.</span>
      </label>

      {errores.map((x) => <p key={x} className="error">{x}</p>)}
      <button type="submit" disabled={enviando || !f.aceptaPrivacidad}>{enviando ? 'Verificando…' : 'Crear cuenta'}</button>
    </form>
  );
}

/** Destino del enlace del correo de activación. El token va en el cuerpo del POST, no en registros. */
export function VerificarCorreo() {
  const [params] = useSearchParams();
  const [estado, setEstado] = useState<'cargando' | 'ok' | 'error'>('cargando');
  const [mensaje, setMensaje] = useState('');

  useEffect(() => {
    const token = params.get('token') ?? '';
    // Saca el token de la barra de direcciones y del historial.
    window.history.replaceState(null, '', '/verificar-correo');
    api<{ mensaje: string }>('/auth/verificar-correo', { cuerpo: { token } })
      .then((r) => { setMensaje(r.mensaje); setEstado('ok'); })
      .catch((e: Error) => { setMensaje(e.message); setEstado('error'); });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <section className="tarjeta centrada">
      <h1>{estado === 'ok' ? 'Cuenta activada' : estado === 'error' ? 'No pudimos activar la cuenta' : 'Activando…'}</h1>
      {mensaje && <p>{mensaje}</p>}
      {estado === 'error' && <p className="sutil">El enlace pudo vencer o ya se usó. Al intentar ingresar le enviaremos uno nuevo.</p>}
      {estado !== 'cargando' && <Link className="boton" to="/login">Ingresar</Link>}
    </section>
  );
}
