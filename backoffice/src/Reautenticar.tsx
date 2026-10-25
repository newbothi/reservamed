import { FormEvent, useState } from 'react';
import { api, ErrorApi } from './api';

/**
 * ASVS V3.7.1: las acciones sensibles (administrar usuarios) piden el código del autenticador
 * si no se confirmó en los últimos 5 minutos. La API responde 403 REAUTENTICAR; aquí se pide
 * el código, se confirma y se reintenta la misma acción.
 */
export function useReautenticacion() {
  const [pendiente, setPendiente] = useState<{ accion: () => Promise<void>; error?: string } | null>(null);
  const [codigo, setCodigo] = useState('');

  const ejecutar = async (accion: () => Promise<void>) => {
    try {
      await accion();
    } catch (e) {
      if (e instanceof ErrorApi && e.codigo === 'REAUTENTICAR') setPendiente({ accion });
      else throw e;
    }
  };

  const confirmar = async (e: FormEvent) => {
    e.preventDefault();
    if (!pendiente) return;
    try {
      await api('/api/cuenta/reautenticar', { cuerpo: { codigo } });
      const { accion } = pendiente;
      setPendiente(null);
      setCodigo('');
      await accion();
    } catch (err) {
      setPendiente({ ...pendiente, error: (err as Error).message });
    }
  };

  const dialogo = pendiente && (
    <div className="modal" role="dialog" aria-modal="true" aria-labelledby="reaut-titulo">
      <form className="tarjeta formulario" onSubmit={confirmar}>
        <h2 id="reaut-titulo">Confirme su identidad</h2>
        <p>Esta acción requiere el código de 6 dígitos de su autenticador.</p>
        <label>Código
          <input value={codigo} onChange={(e) => setCodigo(e.target.value.replace(/\D/g, '').slice(0, 6))}
                 inputMode="numeric" autoComplete="one-time-code" autoFocus required pattern="\d{6}" />
        </label>
        {pendiente.error && <p className="error" role="alert">{pendiente.error}</p>}
        <div className="acciones">
          <button type="button" className="secundario" onClick={() => { setPendiente(null); setCodigo(''); }}>Cancelar</button>
          <button type="submit">Confirmar</button>
        </div>
      </form>
    </div>
  );

  return { ejecutar, dialogo };
}
