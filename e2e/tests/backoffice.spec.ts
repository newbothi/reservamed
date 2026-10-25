import { expect, test } from '@playwright/test';
import { entrarAdmin } from './util';

test.describe('Backoffice', () => {
  test('el administrador entra con TOTP y recorre agenda, usuarios, seguridad y su cuenta', async ({ page }) => {
    await entrarAdmin(page);
    await expect(page.getByRole('link', { name: 'Agenda' })).toBeVisible();

    await page.getByRole('link', { name: 'Usuarios' }).click();
    await expect(page.getByRole('heading', { name: 'Usuarios' })).toBeVisible();
    await expect(page.locator('table.tabla tbody tr').first()).toBeVisible();

    // Acción sensible: pide el código del autenticador antes de ejecutarse (V3.7.1).
    const fila = page.locator('table.tabla tbody tr').filter({ hasText: 'PACIENTE' }).filter({ has: page.getByRole('button', { name: 'Desactivar' }) }).first();
    await fila.getByRole('button', { name: 'Desactivar' }).click();
    await expect(page.getByRole('dialog', { name: 'Confirme su identidad' })).toBeVisible();
    await page.getByRole('button', { name: 'Cancelar' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.getByRole('link', { name: 'Seguridad' }).click();
    await expect(page.getByRole('heading', { name: 'Alertas de seguridad' })).toBeVisible();

    await page.getByRole('link', { name: 'Mi cuenta' }).click();
    await expect(page.getByText('Esta sesión')).toBeVisible();
  });
});
