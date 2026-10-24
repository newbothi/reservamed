import { Controller, Get, Header } from '@nestjs/common';
import { ServicioPow } from '../comun/pow.service';

@Controller('pow')
export class PowController {
  constructor(private readonly pow: ServicioPow) {}

  /** Desafío anti-bots para registro y reserva (A8). */
  @Get()
  @Header('Cache-Control', 'no-store')
  desafio() {
    return this.pow.nuevo();
  }
}
