import { expect, Locator, Page } from '@playwright/test';

/** The six demo personas (labels of the "Viewing as" switcher). */
export type Persona = 'Employee' | 'Manager' | 'Application owner' | 'HR' | 'Security officer' | 'Administrator';

/**
 * Page object for the portal frame: navigation, persona switch, toasts, the assistant.
 * Tests talk in user terms ("open approvals", "act as manager") instead of selectors.
 */
export class Portal {
  constructor(readonly page: Page) {}

  async open(path = '/') {
    await this.page.goto(`./#${path}`);
    await expect(this.page.locator('h1')).toBeVisible();
  }

  async actAs(persona: Persona) {
    const select = this.page.getByLabel('View the portal as');
    const value = await select.locator('option', { hasText: new RegExp(`^${persona}`) }).getAttribute('value');
    await select.selectOption(value!);
    await expect(select.locator('option:checked')).toHaveText(new RegExp(`^${persona} – `));
  }

  nav(name: string | RegExp): Locator {
    return this.page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name });
  }

  menu(name: string): Locator {
    return this.page.getByRole('navigation', { name: 'Main' }).getByRole('button', { name });
  }

  heading(): Locator {
    return this.page.locator('h1');
  }

  async expectToast(text: string | RegExp) {
    await expect(this.page.getByRole('status').filter({ hasText: text })).toBeVisible();
  }

  async action(name: string) {
    await this.page.locator('.action-bar').getByRole('button', { name, exact: true }).click();
  }

  /** Fills and confirms the comment / score / date dialog an action opens. */
  async confirmDialog(value: string, confirm: string) {
    const dialog = this.page.getByRole('dialog');
    await dialog.locator('textarea, input').fill(value);
    await dialog.getByRole('button', { name: confirm }).click();
  }
}

/** The access request form and detail page. */
export class AccessRequests {
  constructor(readonly page: Page) {}

  async request(application: string, role: string, why: string) {
    await this.page.goto('./#/requests/new');
    await this.page.locator('.pick-item', { hasText: application }).click();
    await this.page.locator('.pick-item', { hasText: role }).first().click();
    await this.page.getByLabel('Why is it needed?').fill(why);
    await this.page.getByRole('button', { name: 'Submit request' }).click();
    await expect(this.page).toHaveURL(/#\/requests\/\d+$/);
    return Number(/requests\/(\d+)$/.exec(this.page.url())![1]);
  }

  /** The station of the workflow rail that is current. */
  current(): Locator {
    return this.page.locator('.rail li[aria-current="step"]');
  }
}
