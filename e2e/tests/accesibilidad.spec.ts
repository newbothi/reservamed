import { test } from '@playwright/test';
import { entrarAdmin, entrarPaciente, sinViolacionesGraves } from './util';

/** F5b: WCAG 2.1 AA con axe-core en las pantallas principales (sin violaciones serias ni críticas). */
test.describe('Accesibilidad', () => {
  for (const ruta of ['/', '/login', '/registro', '/privacidad']) {
    test(`portal ${ruta}`, async ({ page }) => {
      await page.goto(ruta);
      await page.waitForLoadState('networkidle');
      await sinViolacionesGraves(page, ruta);
    });
  }

  test('portal con sesión: búsqueda, Mis citas y Mi cuenta', async ({ page }) => {
    await entrarPaciente(page, 'benjamin@demo.cl');
    await page.getByLabel('Especialidad').selectOption({ index: 1 });
    await page.locator('button.hora').first().waitFor();
    await sinViolacionesGraves(page, 'búsqueda con horas');
    for (const ruta of ['/mis-citas', '/mi-cuenta']) {
      await page.goto(ruta);
      await page.waitForLoadState('networkidle');
      await sinViolacionesGraves(page, ruta);
    }
  });

  test('backoffice: ingreso', async ({ page }) => {
    await page.goto('/backoffice/');
    await page.waitForLoadState('networkidle');
    await sinViolacionesGraves(page, 'ingreso al backoffice');
  });

  test('backoffice con sesión: agenda, pacientes, usuarios y seguridad', async ({ page }) => {
    await entrarAdmin(page);
    for (const enlace of ['Agenda', 'Pacientes', 'Usuarios', 'Seguridad', 'Mi cuenta']) {
      await page.getByRole('link', { name: enlace, exact: true }).click();
      await page.waitForLoadState('networkidle');
      await sinViolacionesGraves(page, `backoffice ${enlace}`);
    }
  });
});
