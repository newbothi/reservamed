import { InputHTMLAttributes, useState } from 'react';

/**
 * ASVS V2.1.12: contraseña con botón para verla mientras se escribe.
 * Vuelve a ocultarse al enviar el formulario (el padre se re-monta) o al pulsar de nuevo.
 */
export function CampoClave(props: Omit<InputHTMLAttributes<HTMLInputElement>, 'type'>) {
  const [visible, setVisible] = useState(false);
  return (
    <span className="campo-clave">
      <input {...props} type={visible ? 'text' : 'password'} autoCapitalize="off" spellCheck={false} />
      <button
        type="button"
        className="ver-clave"
        aria-label={visible ? 'Ocultar contraseña' : 'Mostrar contraseña'}
        aria-pressed={visible}
        onClick={() => setVisible((v) => !v)}
      >
        {visible ? 'Ocultar' : 'Ver'}
      </button>
    </span>
  );
}
