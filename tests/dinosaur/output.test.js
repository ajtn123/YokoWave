import test from "node:test";
import assert from "node:assert/strict";
import { setImmediate } from "node:timers/promises";
import { CollisionOutput, collisionPlayback, outputChannels } from "../../modules/dinosaur/output.js";

const TOKEN = "test-only-dinosaur-token-123456789";
const DEVICE = { id: "device", name: "Test output", kind: "dualChannel", channelMask: 5 };
const SETTINGS = { channel: 2, strength: 25, frequency: 20, duration: 350 };

class TestSocket extends EventTarget {
  constructor(handler = () => undefined) {
    super();
    this.readyState = 0;
    this.requests = [];
    this.handler = handler;
    queueMicrotask(() => {
      this.readyState = 1;
      this.dispatchEvent(new Event("open"));
    });
  }

  send(text) {
    const request = JSON.parse(text);
    this.requests.push(request);
    if (this.handler(request, this) === false) return;
    const results = {
      "system.authenticate": true,
      "system.hello": { version: 2, clockMilliseconds: 1000 },
      "devices.list": [DEVICE, { ...DEVICE, id: "pump", kind: "pump" }, { ...DEVICE, id: "empty", channelMask: 0 }],
      "lease.acquire": { id: "lease", deviceIds: [DEVICE.id], type: "control", leaseMilliseconds: 1000 },
      "lease.renew": { clockMilliseconds: 2000 }
    };
    // The host also announces revocation when the client releases a lease normally.
    if (request.method === "lease.release") queueMicrotask(() => this.dispatchEvent(new MessageEvent("message", {
      data: JSON.stringify({ jsonrpc: "2.0", method: "lease.revoked", params: { lease: "lease" } })
    })));
    queueMicrotask(() => this.reply(request, results[request.method] ?? true));
  }

  reply(request, result, error) {
    this.dispatchEvent(new MessageEvent("message", { data: JSON.stringify({ jsonrpc: "2.0", id: request.id, ...(error ? { error } : { result }) }) }));
  }

  close() {
    this.readyState = 3;
    this.dispatchEvent(new Event("close"));
  }
}

async function connected(t, handler, options = {}) {
  let socket;
  const output = new CollisionOutput({ createSocket: () => (socket = new TestSocket(handler)), ...options });
  t.after(() => output.close());
  await output.connect("ws://127.0.0.1:9000/ws/test", TOKEN);
  return { output, socket };
}

test("collision payload is finite, non-repeating, and routes only the selected channel", () => {
  const playback = collisionPlayback(SETTINGS, "Collision", "00000000-0000-0000-0000-000000000001");
  assert.equal(playback.program.order, "once");
  assert.equal(playback.program.outputLimit, 25);
  assert.equal(playback.waveform.periodSeconds, .35);
  assert.equal(playback.durationSeconds, 1);
  assert.deepEqual([playback.channelA, playback.channelB, playback.channelC], [0, 0, 25]);
  assert.deepEqual(outputChannels(DEVICE), [0, 2]);
  for (const invalid of [{ duration: 0 }, { duration: Infinity }, { strength: NaN }, { channel: 3 }, { frequency: 0 }]) {
    assert.throws(() => collisionPlayback({ ...SETTINGS, ...invalid }, "Collision"), { key: "Dinosaur.InvalidPulse" });
  }
});

test("authenticate, discover, acquire, pulse, stop and release use ordered protocol requests", async t => {
  let now = 0;
  const { output, socket } = await connected(t, undefined, { now: () => now });
  assert.deepEqual(output.devices, [DEVICE]);
  await output.arm(DEVICE.id);
  now = 100;
  assert.equal(await output.pulse(SETTINGS, "Collision"), true);
  await output.stopPulse();
  await output.disconnect();
  assert.equal(output.connected, false);
  assert.equal(output.enabled, false);
  const control = socket.requests.filter(request => request.method.startsWith("control."));
  assert.deepEqual(control.map(request => request.params.sequence), [1, 2, 3]);
  assert.ok(control.every(request => request.params.deadline === 1600));
  assert.equal(socket.requests.at(-1).method, "lease.release");
  assert.deepEqual(socket.requests.map(request => request.id), socket.requests.map((_, index) => index + 1));
  assert.equal(socket.requests[0].params.token, TOKEN);
});

test("zero strength and disarmed collisions do not send output", async t => {
  const { output, socket } = await connected(t);
  assert.equal(await output.pulse(SETTINGS, "Collision"), false);
  await output.arm(DEVICE.id);
  assert.equal(await output.pulse({ ...SETTINGS, strength: 0 }, "Collision"), false);
  assert.equal(socket.requests.some(request => request.method === "control.play"), false);
  await assert.rejects(output.pulse({ ...SETTINGS, channel: 1 }, "Collision"), { key: "Error.ModeDeviceMismatch" });
});

test("disarm invalidates a queued collision before waiting for the in-flight call", async t => {
  let held;
  const { output, socket } = await connected(t, request => {
    if (request.method === "control.play") {
      held = request;
      return false;
    }
  });
  await output.arm(DEVICE.id);
  const first = output.pulse(SETTINGS, "Collision");
  await setImmediate();
  const queued = output.pulse(SETTINGS, "Collision");
  const cleanup = output.disarm();
  assert.equal(output.enabled, false);
  socket.reply(held, true);
  assert.equal(await first, true);
  assert.equal(await queued, false);
  await cleanup;
  assert.equal(output.connected, true);
  assert.equal(socket.requests.filter(request => request.method === "control.play").length, 1);
  assert.deepEqual(socket.requests.slice(-2).map(request => request.method), ["control.stop", "lease.release"]);
});

test("an arm request cancelled before acquisition completes is stopped and released", async t => {
  let held;
  const { output, socket } = await connected(t, request => {
    if (request.method === "lease.acquire") {
      held = request;
      return false;
    }
  });
  const arm = output.arm(DEVICE.id);
  await setImmediate();
  const cleanup = output.disarm();
  socket.reply(held, { id: "lease", type: "control", deviceIds: [DEVICE.id], leaseMilliseconds: 1000 });
  await arm;
  await cleanup;
  assert.equal(output.enabled, false);
  assert.equal(socket.requests.at(-1).method, "lease.release");
});

test("heartbeat refreshes the monotonic deadline and does not renew after disarm", async t => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  let now = 0;
  const { output, socket } = await connected(t, undefined, { now: () => now });
  await output.arm(DEVICE.id);
  now = 333;
  t.mock.timers.tick(333);
  await setImmediate();
  now = 433;
  await output.pulse(SETTINGS, "Collision");
  assert.equal(socket.requests.find(request => request.method === "control.play").params.deadline, 2600);
  await output.disarm();
  const renewals = socket.requests.filter(request => request.method === "lease.renew").length;
  t.mock.timers.tick(1000);
  await setImmediate();
  assert.equal(socket.requests.filter(request => request.method === "lease.renew").length, renewals);
});

test("timeout closes rather than retries a control request", async t => {
  const { output, socket } = await connected(t, request => request.method !== "control.play", { timeout: 25 });
  await output.arm(DEVICE.id);
  await assert.rejects(output.pulse(SETTINGS, "Collision"), { key: "Dinosaur.RequestTimeout" });
  assert.equal(output.connected, false);
  assert.equal(output.enabled, false);
  assert.equal(socket.readyState, 3);
  assert.equal(socket.requests.filter(request => request.method === "control.play").length, 1);
});

test("lease revocation and malformed messages close and disarm the connection", async t => {
  for (const revoked of [true, false]) {
    const { output, socket } = await connected(t);
    await output.arm(DEVICE.id);
    socket.dispatchEvent(new MessageEvent("message", { data: revoked ? JSON.stringify({ jsonrpc: "2.0", method: "lease.revoked", params: { lease: "lease" } }) : "{" }));
    assert.equal(output.connected, false);
    assert.equal(output.enabled, false);
  }
});

test("authentication failure is visible and no lease or output is attempted", async t => {
  const output = new CollisionOutput({ createSocket: () => new TestSocket((request, socket) => {
    if (request.method === "system.authenticate") {
      queueMicrotask(() => socket.reply(request, null, { code: -32001, message: "Error.RemoteAuthentication" }));
      return false;
    }
  }) });
  t.after(() => output.close());
  await assert.rejects(output.connect("ws://127.0.0.1:9000/ws/test", TOKEN), { key: "Error.RemoteAuthentication" });
  assert.equal(output.connected, false);
});
