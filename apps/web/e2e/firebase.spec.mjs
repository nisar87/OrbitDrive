// W1 browser test: real Firebase Auth SDK against the LOCAL Auth emulator, real gateway (emulator-token mode) and agent.
// This proves the sign-up / verify / sign-in / reset / sign-out flows and that the gateway accepts only verified accounts.
// It is not evidence about the live orbitdrive-dev project or real email delivery.
import { test, expect } from '@playwright/test';
import { readFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { startGateway, startAgent, agentCli, tempDir, makeFixtureRoot, sha256, repoRoot } from '../../../tests/integration/harness.mjs';

const WEB = 'http://localhost:5174', EMULATOR = 'http://127.0.0.1:9099', PROJECT = 'demo-orbitdrive';
const shots = join(repoRoot, 'artifacts', 'web-screenshots');
let gateway, agent, root;

test.beforeAll(async () => {
  mkdirSync(shots, { recursive: true });
  root = makeFixtureRoot();
  const state = tempDir('fb-e2e-state');
  agentCli(state, 'roots', 'add', 'Test Share', root);
  gateway = await startGateway({
    OrbitDrive__DevIdentity__Enabled: 'false', OrbitDrive__DevIdentity__Secret: '', OrbitDrive__AllowedWebOrigins__0: WEB,
    OrbitDrive__Firebase__ProjectId: PROJECT, OrbitDrive__Firebase__AcceptEmulatorTokens: 'true', OrbitDrive__Firebase__KeysUrl: `${EMULATOR}/no-keys-needed`,
  }, 5198);
  agent = await startAgent(gateway, state, 'Office PC', WEB);
});
test.afterAll(async () => { await agent?.stop(); await gateway?.stop(); });

const oobCodes = async (email, type) => (await (await fetch(`${EMULATOR}/emulator/v1/projects/${PROJECT}/oobCodes`)).json()).oobCodes.filter((c) => c.email === email && c.requestType === type);

test('create account, verify email, pair, download, sign out, sign in again, reset password', async ({ page }, info) => {
  const email = `owner-${info.project.name}-${Date.now()}@example.test`;
  const password = 'correct horse battery';
  const shot = (name) => page.screenshot({ path: join(shots, `${info.project.name}-fb-${name}.png`) });

  await page.goto(WEB);
  await expect(page.getByRole('heading', { name: 'Sign in' }).first()).toBeVisible();
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill('whatever-password');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('alert')).toHaveText('Email or password is not correct.');
  await shot('01-sign-in-error');

  await page.getByRole('button', { name: 'Create an account' }).click();
  await page.getByLabel('Password').fill('short');
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('alert')).toContainText('at least 8 characters');
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('heading', { name: 'Check your inbox' })).toBeVisible();
  await shot('02-verify-email');

  // Unverified: the UI stays on the verification screen and the gateway refuses the account outright.
  await page.getByRole('button', { name: 'I have verified my email' }).click();
  await expect(page.getByRole('status')).toContainText('Not verified yet');
  const signedIn = await (await fetch(`${EMULATOR}/identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key=emulator-key`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email, password, returnSecureToken: true }) })).json();
  const refused = await fetch('http://127.0.0.1:5198/v1/pcs', { headers: { authorization: `Bearer ${signedIn.idToken}` } });
  expect(refused.status).toBe(403);
  expect((await refused.json()).error.message).toMatch(/Verify your email/);

  const [verification] = await oobCodes(email, 'VERIFY_EMAIL');
  expect(verification, 'a verification email was requested').toBeTruthy();
  expect((await fetch(verification.oobLink)).ok).toBe(true); // the user opening the emailed link
  await page.getByRole('button', { name: 'I have verified my email' }).click();
  await expect(page.getByRole('heading', { name: 'Pair with your PC' }).first()).toBeVisible();

  agent.send('pair');
  const pairing = await agent.next('pairing');
  await page.goto(pairing.url);
  const code = page.getByTestId('confirmation-code');
  await expect(code).toBeVisible();
  const shown = await agent.next('pairing_claimed');
  expect(await code.textContent()).toBe(shown.confirmationCode);
  expect(shown.accountLabel).toBe(email);
  agent.send('approve');
  await page.getByRole('button', { name: /Test Share/ }).click();
  const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Download data.bin' }).click()]);
  expect(sha256(readFileSync(await download.path()))).toBe(sha256(readFileSync(join(root, 'data.bin'))));
  await shot('03-explorer-signed-in');
  expect(await page.evaluate(() => localStorage.getItem('orbitdrive.session.v1')), 'no account token is stored by OrbitDrive in Firebase mode').toBeNull();

  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByRole('heading', { name: 'Sign in' }).first()).toBeVisible();
  expect(await page.evaluate(() => Object.keys(localStorage).filter((k) => k.startsWith('orbitdrive.device'))), 'device credential cleared on sign-out').toEqual([]);

  await page.getByRole('button', { name: 'Create an account' }).click();
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('alert')).toContainText('already exists');

  await page.getByRole('button', { name: 'I already have an account' }).click();
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  // Signed in again as the same verified account, but this browser was revoked at sign-out, so it must pair again.
  await expect(page.getByRole('heading', { name: 'Pair with your PC' }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Sign out' }).click();

  await page.getByRole('button', { name: 'Forgot password?' }).click();
  await page.getByLabel('Email').fill(email);
  await page.getByRole('button', { name: 'Send reset link' }).click();
  await expect(page.getByRole('status')).toContainText('If an account exists');
  expect((await oobCodes(email, 'PASSWORD_RESET')).length).toBe(1);
  const unknown = `nobody-${Date.now()}@example.test`;
  await page.getByLabel('Email').fill(unknown);
  await page.getByRole('button', { name: 'Send reset link' }).click();
  await expect(page.getByRole('status')).toContainText('If an account exists'); // identical answer: no account enumeration
  expect((await oobCodes(unknown, 'PASSWORD_RESET')).length).toBe(0);
  await shot('04-reset-requested');
});
