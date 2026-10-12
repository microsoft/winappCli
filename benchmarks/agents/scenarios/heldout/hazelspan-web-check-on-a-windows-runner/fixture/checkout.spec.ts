import { test, expect } from '@playwright/test';
test('pays for basket', async ({ page }) => {
  await page.goto('http://localhost:4173/checkout');
  await page.locator('.spinner').waitFor({ state: 'hidden' });
  await page.locator('#pay').click();
  await expect(page.getByText('Receipt ready')).toBeVisible();
});
