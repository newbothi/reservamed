import { Controller, Get } from '@nestjs/common';
import { CoreClient } from '../comun/core-client.service';

/**
 * Catálogos casi estáticos (especialidades, sedes, previsiones).
 * El BFF los guarda 5 minutos en memoria para no consultar la API Core en cada carga del portal.
 */
@Controller('catalogo')
export class CatalogoController {
  private readonly cache = new Map<string, { hasta: number; datos: unknown }>();

  constructor(private readonly core: CoreClient) {}

  private async cacheado<T>(clave: string, ruta: string): Promise<T> {
    const hit = this.cache.get(clave);
    if (hit && hit.hasta > Date.now()) return hit.datos as T;
    const datos = await this.core.get<T>(ruta);
    this.cache.set(clave, { hasta: Date.now() + 5 * 60_000, datos });
    return datos;
  }

  /** Una sola llamada con todo lo que el portal necesita para sus filtros. */
  @Get()
  async todo() {
    const [especialidades, sedes, financiadores] = await Promise.all([
      this.cacheado('esp', '/api/catalogo/especialidades'),
      this.cacheado('sed', '/api/catalogo/sedes'),
      this.cacheado('fin', '/api/catalogo/financiadores'),
    ]);
    return { especialidades, sedes, financiadores };
  }
}
