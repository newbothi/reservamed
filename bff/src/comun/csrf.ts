/**
 * Defensa CSRF del BFF. Con la sesión en cookie, otra página no debe poder disparar acciones:
 * - la cookie es SameSite=Strict (el navegador no la envía desde otros sitios);
 * - toda petición que cambia estado exige la cabecera X-ReservaMed: 1 (un formulario o un
 *   <img> de otro sitio no pueden agregarla, y fetch cross-origin la bloquea el preflight CORS);
 * - si viene Origin o Sec-Fetch-Site, deben ser del propio sitio.
 */
type Req = { method: string; headers: Record<string, string | string[] | undefined> };
type Res = { status(c: number): Res; json(b: unknown): unknown };

const SEGUROS = new Set(['GET', 'HEAD', 'OPTIONS']);

export function revisarCsrf(req: Req, origenes: string[]): string | null {
  if (SEGUROS.has(req.method.toUpperCase())) return null;
  if (req.headers['x-reservamed'] !== '1') return 'Solicitud rechazada (falta la cabecera de la aplicación).';
  const origen = req.headers.origin as string | undefined;
  if (origen && !origenes.includes(origen)) return 'Solicitud rechazada (origen no permitido).';
  const sitio = req.headers['sec-fetch-site'] as string | undefined;
  if (sitio && sitio !== 'same-origin' && sitio !== 'none') return 'Solicitud rechazada (petición desde otro sitio).';
  return null;
}

export function middlewareCsrf(origenes: string[]) {
  return (req: Req, res: Res, next: () => void) => {
    const error = revisarCsrf(req, origenes);
    if (error) {
      res.status(403).json({ status: 403, title: error, codigo: 'CSRF' });
      return;
    }
    next();
  };
}
