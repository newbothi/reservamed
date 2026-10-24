import { BadRequestException, Injectable } from '@nestjs/common';
import { leerConfig } from './config';
import { crearDesafio, Desafio, Solucion, SolucionesUsadas, verificarSolucion } from './pow';

@Injectable()
export class ServicioPow {
  private readonly cfg = leerConfig();
  private readonly usadas = new SolucionesUsadas();

  nuevo(): Desafio {
    return crearDesafio(this.cfg.clavePow, this.cfg.dificultadPow);
  }

  /** Lanza 400 si la solución no es válida, expiró o ya se usó. */
  exigir(solucion: unknown): void {
    const error = verificarSolucion(solucion as Partial<Solucion>, this.cfg.clavePow, this.cfg.dificultadPow, this.usadas);
    if (error) throw new BadRequestException({ status: 400, title: error, codigo: 'POW' });
  }
}
