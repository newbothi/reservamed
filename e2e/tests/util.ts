import AxeBuilder from '@axe-core/playwright';
import { expect, Page } from '@playwright/test';
import { createHmac } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

export const CLAVE_DEMO = 'Demo1234!';

/** WCAG 2.1 AA: no se aceptan violaciones de impacto serio o crítico. */
export async function sinViolacionesGraves(page: Page, nombre: string) {
  const r = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
  const graves = r.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  const detalle = graves.map((v) => `${v.id} (${v.impact}): ${v.help} → ${v.nodes.slice(0, 3).map((n) => n.target.join(' ')).join(' | ')}`);
  expect(detalle, `Accesibilidad de ${nombre}`).toEqual([]);
}

function base32(s: string): Buffer {
  const abc = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = 0, valor = 0;
  const out: number[] = [];
  for (const c of s.replace(/=+$/, '').replace(/\s/g, '').toUpperCase()) {
    valor = (valor << 5) | abc.indexOf(c);
    bits += 5;
    if (bits >= 8) { out.push((valor >>> (bits - 8)) & 0xff); bits -= 8; }
  }
  return Buffer.from(out);
}

export function totp(secreto: string, paso = Math.floor(Date.now() / 30_000)): string {
  const contador = Buffer.alloc(8);
  contador.writeBigUInt64BE(BigInt(paso));
  const h = createHmac('sha1', base32(secreto)).update(contador).digest();
  const o = h[h.length - 1] & 0x0f;
  return String(((h.readUInt32BE(o) & 0x7fffffff) % 1_000_000)).padStart(6, '0');
}

/** Secreto TOTP de la cuenta de QA (lo crean scripts/verificar-fase3..5a en certs/qa-totp.txt). */
export function secretoQa(): string | null {
  const ruta = join(__dirname, '..', '..', 'certs', 'qa-totp.txt');
  return existsSync(ruta) ? readFileSync(ruta, 'utf8').trim() : null;
}

export async function entrarPaciente(page: Page, email = 'martina@demo.cl') {
  await page.goto('/login');
  await page.getByLabel('Correo').fill(email);
  await page.getByLabel('Contraseña', { exact: false }).first().fill(CLAVE_DEMO);
  await page.getByRole('button', { name: 'Ingresar' }).click();
  await expect(page.getByRole('button', { name: /Salir/ })).toBeVisible();
}

/** Backoffice con la cuenta de QA: contraseña y TOTP (si el paso actual ya se usó, espera el siguiente). */
export async function entrarAdmin(page: Page) {
  const secreto = secretoQa();
  if (!secreto) throw new Error('Falta certs/qa-totp.txt: ejecute antes scripts/verificar-fase3.ps1 (enrola la cuenta de QA).');
  await page.goto('/backoffice/');
  await page.getByLabel('Correo').fill('qa@reservamed.cl');
  await page.getByLabel('Contraseña', { exact: false }).first().fill(CLAVE_DEMO);
  await page.getByRole('button', { name: 'Continuar' }).click();
  for (let intento = 0; intento < 2; intento++) {
    await page.getByLabel(/Código de 6 dígitos/).fill(totp(secreto));
    await page.getByRole('button', { name: 'Verificar' }).click();
    const ok = await page.getByRole('button', { name: /Salir/ }).waitFor({ timeout: 8_000 }).then(() => true, () => false);
    if (ok) return;
    await page.waitForTimeout(31_000);
  }
  await expect(page.getByRole('button', { name: /Salir/ })).toBeVisible();
}
