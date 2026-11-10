export class OutputError extends Error {
  constructor(key) {
    super(key);
    this.key = key;
  }
}

export function outputChannels(device) {
  return [0, 1, 2].filter(channel => (device?.channelMask & (1 << channel)) !== 0);
}

/** A finite server-side waveform; the browser does not schedule the final zero. */
export function collisionPlayback(settings, name, id = crypto.randomUUID()) {
  const { channel, strength, frequency, duration } = settings;
  if (!Number.isInteger(channel) || channel < 0 || channel > 2 || !Number.isFinite(strength) || strength < 0 || strength > 100 ||
      !Number.isFinite(frequency) || frequency < 1 || frequency > 100 || !Number.isFinite(duration) || duration < 200 || duration > 2000) {
    throw new OutputError("Dinosaur.InvalidPulse");
  }
  const levels = [0, 0, 0];
  levels[channel] = strength;
  return {
    program: { kind: "waveform", order: "once", outputLimit: strength, offsetChannelB: false },
    waveform: {
      formatVersion: 1, id, name, shape: "sampled", periodSeconds: duration / 1000, frequency,
      samples: [{ level: 1, frequencyHz: frequency, pulseWidth: 1 }, { level: 1, frequencyHz: frequency, pulseWidth: 1 }]
    },
    channelA: levels[0], channelB: levels[1], channelC: levels[2], durationSeconds: Math.max(1, duration / 1000)
  };
}

/** Game-specific control calls over the browser's native WebSocket, with one device lease. */
export class CollisionOutput extends EventTarget {
  #socket = null;
  #pending = new Map();
  #nextId = 0;
  #lease = null;
  #releasing = null;
  #sequence = 0;
  #clock = 0;
  #clockAt = 0;
  #heartbeat = null;
  #tail = Promise.resolve();
  #epoch = 0;
  #enabled = false;
  #authenticated = false;

  constructor({ createSocket = (url, protocol) => new WebSocket(url, protocol), now = () => performance.now(), timeout = 3000 } = {}) {
    super();
    this.createSocket = createSocket;
    this.now = now;
    this.timeout = timeout;
    this.devices = [];
    this.error = null;
  }

  get connected() { return this.#authenticated; }
  get enabled() { return this.#enabled; }

  async connect(address, token) {
    let url;
    try {
      url = new URL(address);
    } catch {
      throw new OutputError("Dinosaur.InvalidAddress");
    }
    if (!["ws:", "wss:"].includes(url.protocol) || url.username || url.password || url.search || url.hash) throw new OutputError("Dinosaur.InvalidAddress");
    if (token.length < 24 || token.length > 256) throw new OutputError("Error.RemoteAuthentication");
    if (this.#socket) throw new OutputError("Dinosaur.ConnectionBusy");

    this.error = null;
    this.#nextId = 0;
    const socket = this.createSocket(url.href, "yokowave.v2");
    this.#socket = socket;
    socket.addEventListener("message", event => {
      if (this.#socket === socket) this.#receive(event.data);
    });
    socket.addEventListener("close", () => {
      if (this.#socket === socket) this.close(new OutputError("Error.RemoteDisconnected"));
    });
    socket.addEventListener("error", () => {
      if (this.#socket === socket) this.close(new OutputError("Dinosaur.ConnectionFailed"));
    });
    try {
      await this.#opened(socket);
      await this.#call("system.authenticate", { token });
      const hello = await this.#call("system.hello");
      if (hello.version !== 2) throw new OutputError("Error.RemoteVersion");
      this.#syncClock(hello);
      const devices = await this.#call("devices.list");
      if (!Array.isArray(devices)) throw new OutputError("Error.RemoteRequest");
      this.devices = devices.filter(device => device && typeof device.id === "string" && typeof device.name === "string" &&
        ["dualChannel", "vibration"].includes(device.kind) && Number.isInteger(device.channelMask) && outputChannels(device).length > 0);
      this.#authenticated = true;
      this.#changed();
    } catch (error) {
      this.close(error);
      throw error;
    }
  }

  arm(deviceId) {
    const epoch = ++this.#epoch;
    return this.#enqueue(async () => {
      if (!this.connected || this.#lease || !this.devices.some(device => device.id === deviceId)) throw new OutputError("Error.RemoteLease");
      const lease = await this.#call("lease.acquire", { deviceIds: [deviceId], type: "control" });
      if (!lease || typeof lease.id !== "string" || lease.type !== "control" || !Array.isArray(lease.deviceIds) ||
          lease.deviceIds.length !== 1 || lease.deviceIds[0] !== deviceId || !Number.isFinite(lease.leaseMilliseconds) ||
          lease.leaseMilliseconds < 1000 || lease.leaseMilliseconds > 10000) {
        this.close(new OutputError("Error.RemoteLease"));
        throw new OutputError("Error.RemoteLease");
      }
      this.#lease = lease;
      this.#sequence = 0;
      if (epoch !== this.#epoch) {
        await this.#release();
        return;
      }
      this.#enabled = true;
      this.#scheduleHeartbeat(lease);
      this.#changed();
    });
  }

  pulse(settings, name) {
    const epoch = this.#epoch;
    const playback = collisionPlayback(settings, name);
    return this.#enqueue(async () => {
      if (!this.#enabled || epoch !== this.#epoch || settings.strength === 0) return false;
      const device = this.devices.find(device => device.id === this.#lease?.deviceIds[0]);
      if (!outputChannels(device).includes(settings.channel)) throw new OutputError("Error.ModeDeviceMismatch");
      await this.#control("control.play", { playback });
      return true;
    });
  }

  stopPulse() {
    return this.#enqueue(async () => {
      if (this.#lease) await this.#control("control.stop");
    });
  }

  disarm() {
    ++this.#epoch;
    this.#enabled = false;
    clearTimeout(this.#heartbeat);
    this.#changed();
    return this.#enqueue(() => this.#release());
  }

  async disconnect() {
    try {
      await this.disarm();
    } finally {
      this.close();
    }
  }

  /** Closing the socket also invokes the host's lease cleanup, including on page unload. */
  close(error = null) {
    ++this.#epoch;
    clearTimeout(this.#heartbeat);
    this.#enabled = false;
    this.#authenticated = false;
    this.#lease = null;
    this.#releasing = null;
    this.devices = [];
    this.error = error;
    const socket = this.#socket;
    this.#socket = null;
    for (const pending of this.#pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(error ?? new OutputError("Error.RemoteDisconnected"));
    }
    this.#pending.clear();
    if (socket && socket.readyState < 2) socket.close();
    this.#changed();
  }

  async #release() {
    const lease = this.#lease;
    if (!lease) return;
    try {
      await this.#control("control.stop");
      this.#releasing = lease.id;
      await this.#call("lease.release", { lease: lease.id });
      this.#lease = null;
    } catch (error) {
      this.close(error);
      throw error;
    } finally {
      this.#releasing = null;
    }
    this.#changed();
  }

  #control(method, parameters = {}) {
    if (!this.#lease) throw new OutputError("Error.RemoteLease");
    return this.#call(method, { ...parameters, lease: this.#lease.id, sequence: ++this.#sequence, deadline: Math.floor(this.#clock + this.now() - this.#clockAt + 500) });
  }

  #enqueue(operation) {
    const task = this.#tail.then(operation);
    // Keep the queue available for cleanup; callers still receive the original failure.
    this.#tail = task.catch(() => {});
    return task;
  }

  #scheduleHeartbeat(lease) {
    this.#heartbeat = setTimeout(async () => {
      try {
        await this.#enqueue(async () => {
          if (!this.#enabled || this.#lease !== lease) return;
          this.#syncClock(await this.#call("lease.renew", { lease: lease.id }));
          if (this.#enabled && this.#lease === lease) this.#scheduleHeartbeat(lease);
        });
      } catch (error) {
        this.close(error);
      }
    }, Math.floor(lease.leaseMilliseconds / 3));
  }

  #syncClock(value) {
    if (!Number.isFinite(value?.clockMilliseconds)) throw new OutputError("Error.RemoteRequest");
    this.#clock = value.clockMilliseconds;
    this.#clockAt = this.now();
  }

  #opened(socket) {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => finish(new OutputError("Dinosaur.ConnectionFailed")), this.timeout);
      const opened = () => finish();
      const failed = () => finish(new OutputError("Dinosaur.ConnectionFailed"));
      const finish = error => {
        clearTimeout(timer);
        socket.removeEventListener("open", opened);
        socket.removeEventListener("close", failed);
        socket.removeEventListener("error", failed);
        if (error) reject(error);
        else resolve();
      };
      socket.addEventListener("open", opened, { once: true });
      socket.addEventListener("close", failed, { once: true });
      socket.addEventListener("error", failed, { once: true });
    });
  }

  #call(method, parameters = {}) {
    return new Promise((resolve, reject) => {
      if (this.#socket?.readyState !== 1) {
        reject(new OutputError("Error.RemoteDisconnected"));
        return;
      }
      const id = ++this.#nextId;
      const timer = setTimeout(() => this.close(new OutputError("Dinosaur.RequestTimeout")), this.timeout);
      this.#pending.set(id, { resolve, reject, timer });
      try {
        this.#socket.send(JSON.stringify({ jsonrpc: "2.0", id, method, params: parameters }));
      } catch (error) {
        this.close(error);
      }
    });
  }

  #receive(data) {
    try {
      if (typeof data !== "string" || data.length > 1024 * 1024) throw new OutputError("Error.RemoteRequest");
      const message = JSON.parse(data);
      if (!message || message.jsonrpc !== "2.0") throw new OutputError("Error.RemoteRequest");
      if (message.method === "lease.revoked" && this.#lease && message.params?.lease === this.#lease.id) {
        if (message.params.lease !== this.#releasing) this.close(new OutputError("Error.RemoteLease"));
        return;
      }
      const pending = this.#pending.get(message.id);
      if (!pending) return;
      this.#pending.delete(message.id);
      clearTimeout(pending.timer);
      if (message.error) pending.reject(new OutputError(message.error.message || "Error.RemoteRequest"));
      else if (Object.hasOwn(message, "result")) pending.resolve(message.result);
      else pending.reject(new OutputError("Error.RemoteRequest"));
    } catch (error) {
      this.close(error);
    }
  }

  #changed() { this.dispatchEvent(new Event("change")); }
}
