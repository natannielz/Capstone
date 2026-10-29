// Shared steps for the staff browser rehearsals: how the product shows a date, and how a person moves between a workspace page and
// the account page through the workspace frame. Nothing here touches the API; it only reads and clicks what a person sees.
import assert from 'node:assert/strict';

/**
 * A date as the product shows it. Same rule as frontend/src/lib/format.ts formatDate: Intl id-ID in the Asia/Jakarta calendar,
 * a short month without its trailing dot ("29 Okt 2026"), "long" gives "29 Oktober 2026". The rehearsals never hold a literal.
 */
export function displayDate(value, style = 'short') {
  const date = new Date(value);
  assert.ok(!Number.isNaN(date.getTime()), 'Not a date: ' + String(value));
  const parts = {};
  for (const part of new Intl.DateTimeFormat('id-ID', { timeZone: 'Asia/Jakarta', day: 'numeric', month: style === 'long' ? 'long' : 'short', year: 'numeric' }).formatToParts(date))
    if (part.type !== 'literal') parts[part.type] = part.value;
  return parts.day + ' ' + parts.month.replace(/\.$/, '') + ' ' + parts.year;
}

/**
 * Opens "Akun saya" from the workspace frame the way a person does: the sidebar on a wide screen, the "Menu" sheet on a phone
 * (the sidebar is not displayed there). Must be called on a page inside the frame.
 */
export async function openAccountFromFrame(page) {
  const menuButton = page.getByRole('button', { name: 'Buka menu ruang kerja', exact: true });
  if (await menuButton.isVisible()) {
    await menuButton.click();
    await page.getByRole('navigation', { name: 'Menu ruang kerja', exact: true }).getByRole('link', { name: 'Akun saya', exact: true }).click();
  } else await page.getByRole('navigation', { name: 'Ruang kerja', exact: true }).getByRole('link', { name: 'Akun saya', exact: true }).click();
}

/**
 * From the account page back to one workspace page: "Kembali ke Pekerjaan saya", then that page's card in the workspace directory.
 * Asserts that the directory offers the page exactly once, so a lost permission would show here.
 */
export async function returnToWorkspacePage(page, href) {
  await page.getByRole('link', { name: 'Kembali ke Pekerjaan saya', exact: true }).click();
  const destination = page.getByRole('navigation', { name: 'Ruang kerja akun', exact: true }).locator('a[href="' + href + '"]');
  await destination.waitFor({ timeout: 60000 });
  assert.equal(await destination.count(), 1);
  await destination.click();
}
