import { expect, test } from '@playwright/test';
import { entrarPaciente } from './util';

test.describe('Portal de pacientes', () => {
  test('busca horas, reserva pagando en la clínica, la ve en Mis citas y la anula', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Reserve su hora médica' })).toBeVisible();

    await entrarPaciente(page);
    await page.goto('/');
    const especialidad = page.getByLabel('Especialidad');
    await expect(especialidad.locator('option')).not.toHaveCount(1);
    await especialidad.selectOption({ index: 1 });
    // Dos semanas adelante: no choca con las reservas de las verificaciones.
    await page.getByRole('button', { name: 'Siguiente →' }).click();
    await page.getByRole('button', { name: 'Siguiente →' }).click();
    const horas = page.locator('button.hora');
    await expect(horas.first()).toBeVisible();
    await horas.last().click();

    await expect(page.getByRole('heading', { name: 'Confirme su reserva' })).toBeVisible();
    await page.getByText('Pagar en la clínica', { exact: true }).click();
    await page.getByRole('button', { name: 'Confirmar reserva' }).click();
    await expect(page.getByRole('heading', { name: '¡Hora reservada!' })).toBeVisible();

    await page.goto('/mis-citas');
    const cita = page.locator('article.cita').filter({ has: page.getByRole('button', { name: 'Anular' }) }).last();
    await expect(cita).toBeVisible();
    const antes = await page.locator('article.cita').count();
    page.once('dialog', (d) => d.accept());
    await cita.getByRole('button', { name: 'Anular' }).click();
    await expect(page.locator('article.cita')).toHaveCount(antes - 1);
  });

  test('Mi cuenta muestra la sesión actual y la descarga de datos', async ({ page }) => {
    await entrarPaciente(page, 'sofia@demo.cl');
    await page.getByRole('link', { name: 'Mi cuenta' }).click();
    await expect(page.getByRole('heading', { name: 'Mi cuenta', level: 1 })).toBeVisible();
    await expect(page.getByText('Este equipo')).toBeVisible();
    const descarga = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Descargar mis datos (JSON)' }).click();
    expect((await descarga).suggestedFilename()).toMatch(/^mis-datos-reservamed-\d{4}-\d{2}-\d{2}\.json$/);
  });

  test('la contraseña se puede ver mientras se escribe', async ({ page }) => {
    await page.goto('/login');
    const campo = page.locator('input[autocomplete="current-password"]');
    await campo.fill('secreta');
    await expect(campo).toHaveAttribute('type', 'password');
    await page.getByRole('button', { name: 'Mostrar contraseña' }).click();
    await expect(campo).toHaveAttribute('type', 'text');
  });

  test('registro exige aceptar la política de privacidad', async ({ page }) => {
    await page.goto('/registro');
    await expect(page.getByRole('button', { name: 'Crear cuenta' })).toBeDisabled();
    await page.getByRole('link', { name: 'política de privacidad' }).isVisible();
    await page.goto('/privacidad');
    await expect(page.getByRole('heading', { name: 'Política de privacidad' })).toBeVisible();
  });

  test('una sesión ajena o inexistente lleva al login', async ({ page }) => {
    await page.goto('/mis-citas');
    await expect(page).toHaveURL(/\/login\?volver=%2Fmis-citas/);
  });
});
