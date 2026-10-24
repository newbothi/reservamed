/**
 * Traduce /bff/bo/api/... a /api/... (y /bff/bo/fhir/... a /fhir/...) y rechaza lo que podría salir de /api o llegar a /api/auth
 * (los tokens nunca se exponen al navegador): "..", barras invertidas, dobles barras y cualquier
 * carácter codificado en la ruta (la API decodifica %61uth como "auth"). La consulta (?a=b) se conserva.
 */
export function rutaProxyPermitida(originalUrl: string): string | null {
  const destino = originalUrl.replace(/^\/bff\/bo/, '');
  const [ruta] = destino.split('?', 1);
  if (!/^\/(api|fhir)\/[A-Za-z0-9\-_.~/]*$/.test(ruta)) return null;
  if (/(^|\/)\.{1,2}(\/|$)/.test(ruta) || ruta.includes('//')) return null;
  if (/^\/api\/auth(\/|$)/i.test(ruta)) return null;
  return destino;
}
