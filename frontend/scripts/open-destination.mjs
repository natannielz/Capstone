// Opens a page the way a person does since the one-flow change (R2): a buyer task from the storefront account menu, a staff
// page from the workspace directory on /workspace ("Pekerjaan saya"). "Akun saya" (/identity/account) holds account settings only.
const escape = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** Opens `label` from the storefront account menu when it lists it, else from the workspace directory. */
export async function openDestination(page, origin, label) {
  const base = typeof origin === 'string' ? origin : origin.origin;
  await page.goto(base + '/identity/account');
  const menuButton = page.locator('button[aria-controls="menu-akun"]');
  await menuButton.click();
  const menuLink = page.locator('#menu-akun').getByRole('link', { name: label, exact: true });
  if (await menuLink.count()) { await menuLink.click(); return; }
  await menuButton.click();
  await openFromWorkspace(page, base, label);
}

/** Opens `label` from the workspace directory (the destination cards, not the sidebar entry with the same name). */
export async function openFromWorkspace(page, origin, label) {
  const base = typeof origin === 'string' ? origin : origin.origin;
  await page.goto(base + '/workspace');
  await page.getByRole('navigation', { name: 'Ruang kerja akun', exact: true }).getByRole('link', { name: new RegExp('^' + escape(label) + '\\b') }).click();
}
