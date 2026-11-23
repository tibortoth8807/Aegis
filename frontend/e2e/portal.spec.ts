import { expect, test } from '@playwright/test';
import { AccessRequests, Portal } from './portal';

// Every test starts with a fresh browser context, so the demo database is seeded anew.

test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  (page as unknown as { errors: string[] }).errors = errors;
});

test.afterEach(async ({ page }) => {
  expect((page as unknown as { errors: string[] }).errors, 'no errors in the browser console').toEqual([]);
});

test('an access request goes from the employee through manager and owner to active access', async ({ page }) => {
  const portal = new Portal(page);
  const requests = new AccessRequests(page);
  await portal.open();

  const id = await requests.request('Source code hosting', 'Developer', 'Contributing to the analytics dashboards');
  await portal.expectToast(/submitted - waiting for .+'s manager/);
  await expect(requests.current()).toHaveText('Manager');

  await portal.actAs('Manager');
  await expect(portal.nav(/Approvals/)).toContainText(/\d/);
  await portal.open('/approvals');
  await page.locator('tr', { hasText: 'Source code hosting' }).filter({ hasText: 'your turn' }).getByRole('link').click();
  await portal.action('Approve');
  await expect(requests.current()).toHaveText('Application owner');

  // the manager cannot approve the next step as well (four eyes)
  await expect(page.locator('.action-bar').getByRole('button', { name: 'Approve' })).toHaveCount(0);

  await portal.actAs('Administrator');
  await page.goto(`./#/requests/${id}`);
  await portal.action('Approve');
  await expect(requests.current()).toHaveText('Active');
  await portal.expectToast(/approved - the access is active/);

  await portal.actAs('Employee');
  await page.goto('./#/me');
  await expect(page.locator('section', { hasText: 'Access' }).getByRole('cell', { name: 'Source code hosting' })).toBeVisible();
});

test('a rejection needs a reason and ends the workflow', async ({ page }) => {
  const portal = new Portal(page);
  const requests = new AccessRequests(page);
  await portal.open();
  const id = await requests.request('IT service management', 'Agent', 'Joining the service desk rotation');

  await portal.actAs('Manager');
  await page.goto(`./#/requests/${id}`);
  await portal.action('Reject');
  await expect(page.getByRole('dialog').getByRole('button', { name: 'Reject' })).toBeDisabled();
  await portal.confirmDialog('Use the shared team role instead', 'Reject');
  await expect(page.locator('.rail-outcome')).toContainText('Rejected');
  await expect(page.locator('.history')).toContainText('Use the shared team role instead');
});

test('business rules come back as readable messages', async ({ page }) => {
  const portal = new Portal(page);
  await portal.open('/requests/new');
  await page.locator('.pick-item', { hasText: 'Cloud subscription' }).click();
  await page.locator('.pick-item', { hasText: 'Reader' }).click();
  await page.getByLabel('Why is it needed?').fill('Need to look at the production logs');
  await page.getByRole('button', { name: 'Submit request' }).click();
  await expect(page.getByRole('alert')).toHaveText('Cloud subscription requires the training "Privileged access handling" first.');
});

test('menus follow the roles: HR and admin screens are hidden from employees', async ({ page }) => {
  const portal = new Portal(page);
  await portal.open();
  await expect(portal.menu('Administration')).toHaveCount(0);
  await portal.actAs('HR');
  await portal.menu('Administration').click();
  await expect(page.getByRole('link', { name: 'Export lists' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Import from directory' })).toBeVisible();
});

test('HR registers a joiner, who starts in preboarding with the mandatory trainings', async ({ page }) => {
  const portal = new Portal(page);
  await portal.open();
  await portal.actAs('HR');
  await portal.open('/employees/new');
  await page.getByLabel('First name').fill('Teszt');
  await page.getByLabel('Last name').fill('Elek');
  await page.getByLabel('Employee number').fill('E20001');
  await page.getByLabel('E-mail').fill('teszt.elek@aegis.example');
  await page.getByLabel('Job title').fill('QA Engineer');
  await page.getByLabel('Team').selectOption({ label: 'Platform' });
  await page.getByLabel('Start date').fill('2030-01-07');
  await page.getByRole('button', { name: 'Register employee' }).click();

  await expect(portal.heading()).toHaveText('Teszt Elek');
  await expect(page.locator('.rail li[aria-current="step"]')).toHaveText('Preboarding');
  await expect(page.locator('section', { hasText: 'Trainings' }).locator('tbody tr')).toHaveCount(3);
});

test('exports download real Excel files', async ({ page }) => {
  const portal = new Portal(page);
  await portal.open();
  await portal.actAs('HR');
  await portal.open('/exports');
  const [download] = await Promise.all([page.waitForEvent('download'), page.locator('li', { hasText: 'Training compliance' }).getByRole('button', { name: 'Download Excel' }).click()]);
  expect(download.suggestedFilename()).toMatch(/^aegis-training-\d{8}-\d{4}\.xlsx$/);
});

test('the assistant answers in Hungarian from the user\'s own data', async ({ page }) => {
  const portal = new Portal(page);
  await portal.open();
  await portal.actAs('Manager');
  await page.getByRole('button', { name: 'Open the assistant' }).click();
  await page.getByLabel('Your question').fill('Mit kell jóváhagynom?');
  await page.getByLabel('Your question').press('Enter');
  const answer = page.locator('.msg.assistant').last();
  await expect(answer).toContainText(/waiting for your decision/);
  await answer.getByRole('link', { name: 'Open approvals' }).click();
  await expect(portal.heading()).toHaveText('Approvals');
});

test('directory import previews before it saves @mobile', async ({ page }) => {
  const portal = new Portal(page);
  await portal.open();
  await portal.actAs('HR');
  await portal.open('/import');
  await page.getByRole('button', { name: 'Use a sample file' }).click();
  await page.getByRole('button', { name: 'Preview changes' }).click();
  await expect(page.locator('.summary')).toContainText('2 new');
  await expect(page.locator('.warnings')).toContainText('register externals manually');
  await page.getByRole('button', { name: 'Apply import' }).click();
  await portal.expectToast('Imported: 2 new, 0 updated.');
});
