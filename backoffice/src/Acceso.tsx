import { FormEvent, useEffect, useState } from 'react';
import QRCode from 'qrcode';
import { api, ErrorApi, Sesion } from './api';
import { CampoClave } from './CampoClave';

type Paso =
  | { tipo: 'clave' }
  | { tipo: 'totp' }
  | { tipo: 'enrolar'; secreto: string; uri: string };

interface RespuestaLogin {
  estado: 'OK' | 'TOTP' | 'ENROLAR_TOTP';
  usuario?: Sesion;
  secretoTotp?: string | null;
  uriTotp?: string | null;
}

function mensaje(e: unknown): string {
  const err = e as ErrorApi;
  if (err.codigo === 'BLOQUEADA' || err.codigo === 'LIMITE' || err.status === 429)
    return 'Demasiados intentos. Espere unos minutos antes de volver a intentar.';
  return err.message;
}

/** Código QR para el autenticador, generado en el navegador (el secreto no sale a terceros). */
function CodigoQr({ uri }: { uri: string }) {
  const [src, setSrc] = useState('');
  useEffect(() => {
    QRCode.toDataURL(uri, { margin: 1, width: 220, errorCorrectionLevel: 'M' }).then(setSrc).catch(() => setSrc(''));
  }, [uri]);
  return src ? <img className="qr" src={src} width={220} height={220} alt="Código QR para la app autenticadora" /> : null;
}

/**
 * Ingreso del personal (A2): contraseña y luego un código TOTP de 6 dígitos de una app
 * autenticadora (Google Authenticator, Microsoft Authenticator, 1Password…).
 * El primer ingreso enrola el autenticador escaneando un QR.
 */
export function Login({ alEntrar }: { alEntrar: (s: Sesion) => void }) {
  const [email, setEmail] = useState('recepcion@reservamed.cl');
  const [password, setPassword] = useState('');
  const [codigo, setCodigo] = useState('');
  const [paso, setPaso] = useState<Paso>({ tipo: 'clave' });
  const [error, setError] = useState('');
  const [enviando, setEnviando] = useState(false);

  const conClave = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    setEnviando(true);
    try {
      const r = await api<RespuestaLogin>('/auth/login', { cuerpo: { email, password } });
      setPassword('');
      if (r.estado === 'OK' && r.usuario) alEntrar(r.usuario);
      else if (r.estado === 'ENROLAR_TOTP') setPaso({ tipo: 'enrolar', secreto: r.secretoTotp ?? '', uri: r.uriTotp ?? '' });
      else setPaso({ tipo: 'totp' });
    } catch (err) {
      setError(mensaje(err));
    } finally {
      setEnviando(false);
    }
  };

  const conCodigo = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    setEnviando(true);
    try {
      const r = await api<RespuestaLogin>('/auth/totp', { cuerpo: { codigo } });
      if (r.usuario) alEntrar(r.usuario);
    } catch (err) {
      const ea = err as ErrorApi;
      setCodigo('');
      setError(mensaje(err));
      if (ea.codigo === 'DESAFIO' || ea.status === 429) setPaso({ tipo: 'clave' });
    } finally {
      setEnviando(false);
    }
  };

  const volver = () => {
    setPaso({ tipo: 'clave' });
    setCodigo('');
    setError('');
  };

  return (
    <main className="contenedor">
      {paso.tipo === 'clave' ? (
        <form className="tarjeta centrada formulario" onSubmit={conClave}>
          <h1>Backoffice clínica</h1>
          <label>Correo<input type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="username" required /></label>
          <label>Contraseña<CampoClave value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" required /></label>
          {error && <p className="error">{error}</p>}
          <button type="submit" disabled={enviando}>Continuar</button>
          <p className="sutil demo">
            Demo (clave Demo1234!): recepcion@reservamed.cl (Providencia) · recepcion.lc@reservamed.cl (Las Condes) ·
            dra.soto@reservamed.cl (médico) · admin@reservamed.cl. El primer ingreso de cada cuenta pide enrolar un autenticador.
          </p>
        </form>
      ) : (
        <form className="tarjeta centrada formulario" onSubmit={conCodigo}>
          <h1>{paso.tipo === 'enrolar' ? 'Active la verificación en dos pasos' : 'Verificación en dos pasos'}</h1>
          {paso.tipo === 'enrolar' && (
            <>
              <p>Escanee este código con su app autenticadora. Se muestra una sola vez.</p>
              <CodigoQr uri={paso.uri} />
              <details>
                <summary>¿No puede escanear? Ingrese la clave a mano</summary>
                <code className="secreto">{paso.secreto.replace(/(.{4})/g, '$1 ').trim()}</code>
              </details>
            </>
          )}
          <label>Código de 6 dígitos
            <input
              value={codigo}
              onChange={(e) => setCodigo(e.target.value.replace(/\D/g, '').slice(0, 6))}
              inputMode="numeric"
              autoComplete="one-time-code"
              pattern="\d{6}"
              autoFocus
              required
            />
          </label>
          {error && <p className="error">{error}</p>}
          <button type="submit" disabled={enviando || codigo.length !== 6}>Verificar</button>
          <button type="button" className="link" onClick={volver}>Volver</button>
        </form>
      )}
    </main>
  );
}
