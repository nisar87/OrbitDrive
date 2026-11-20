// After an update the PC may still hold the PREVIOUS release's default relay/web addresses. Those, and only those, move to the new defaults;
// an address the owner chose is never touched. Real agent processes, real state folders, real shipped-defaults file.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { startAgent, connectIpc, tempDir } from './harness.mjs';

const OLD = { gateway: 'https://old-relay.example.test', web: 'https://old-web.example.test' };
const NEW = { gateway: 'https://new-relay.example.test', web: 'https://new-web.example.test' };

/** First run with the given addresses (as the owner or an earlier release set them), then stop. */
async function firstRun(state, gateway, web) {
  const agent = await startAgent({ url: gateway }, state, 'Test PC', web, { refused: true });
  await agent.stop();
}
/** Second run, as after an update, with a shipped defaults file; returns what the agent reports. */
async function afterUpdate(state, defaults) {
  const file = join(tempDir('defaults'), 'defaults.json');
  writeFileSync(file, JSON.stringify(defaults));
  const agent = await startAgent(null, state, 'Test PC', undefined, { extraArgs: ['--defaults', file] });
  const ipc = await connectIpc(agent.ready.pipe);
  try {
    const status = (await ipc.send('status')).data;
    return { gateway: status.gateway, webOrigin: status.webOrigin, migrated: agent.events.some((e) => e.event === 'defaults_migrated') };
  } finally { ipc.close(); await agent.stop(); }
}
const shipped = { gateway: NEW.gateway, webOrigin: NEW.web, supersedes: [OLD.gateway, OLD.web] };

test('previous default addresses move to the new defaults after an update', { timeout: 90_000 }, async () => {
  const state = tempDir('mig-a');
  await firstRun(state, OLD.gateway, OLD.web);
  const after = await afterUpdate(state, shipped);
  assert.deepEqual(after, { gateway: NEW.gateway, webOrigin: NEW.web, migrated: true });
  const again = await afterUpdate(state, shipped);                       // idempotent: a later start changes nothing
  assert.deepEqual(again, { gateway: NEW.gateway, webOrigin: NEW.web, migrated: false });
});

test('an address the owner chose is never changed, even when the other one was a previous default', { timeout: 90_000 }, async () => {
  const custom = tempDir('mig-b');
  await firstRun(custom, 'https://my-own-relay.example.test', 'https://my-own-web.example.test');
  assert.deepEqual(await afterUpdate(custom, shipped), { gateway: 'https://my-own-relay.example.test', webOrigin: 'https://my-own-web.example.test', migrated: false });
  const partial = tempDir('mig-c');                                      // own relay, previous default web address: only the web address moves
  await firstRun(partial, 'https://my-own-relay.example.test', OLD.web);
  assert.deepEqual(await afterUpdate(partial, shipped), { gateway: 'https://my-own-relay.example.test', webOrigin: NEW.web, migrated: true });
});

test('a defaults file without a supersedes list, or with an unusable address, changes nothing', { timeout: 90_000 }, async () => {
  const old = tempDir('mig-d');
  await firstRun(old, OLD.gateway, OLD.web);
  assert.deepEqual(await afterUpdate(old, { gateway: NEW.gateway, webOrigin: NEW.web }), { gateway: OLD.gateway, webOrigin: OLD.web, migrated: false });
  assert.deepEqual(await afterUpdate(old, { gateway: 'http://not-loopback.example.test', webOrigin: NEW.web, supersedes: [OLD.gateway, OLD.web] }), { gateway: OLD.gateway, webOrigin: OLD.web, migrated: false });
});
