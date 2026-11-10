import { CollisionOutput, OutputError, outputChannels } from "./output.js";

/** DOM controls for one remote output. Credentials are never persisted. */
export class OutputPanel {
  constructor(localization) {
    this.localization = localization;
    this.output = new CollisionOutput();
    this.busy = false;
    this.arming = false;
    this.message = "Dinosaur.OutputOff";
    this.failed = false;
    this.elements = Object.fromEntries(["url", "token", "connect", "device", "channel", "strength", "strength-value", "frequency", "duration", "armed", "stop-output", "output-fields", "output-status", "connection-state"].map(id => [id, document.getElementById(id)]));
    this.output.addEventListener("change", () => {
      if (this.output.error) this.showError(this.output.error);
      this.render();
    });
    document.getElementById("connection-form").addEventListener("submit", event => {
      event.preventDefault();
      this.run(async () => {
        if (this.output.connected) {
          await this.output.disconnect();
          this.message = "Dinosaur.OutputOff";
        } else {
          await this.output.connect(this.elements.url.value.trim(), this.elements.token.value);
          this.elements.token.value = "";
          this.populateDevices();
          this.message = this.output.devices.length ? "Dinosaur.OutputOff" : "Dinosaur.NoDevices";
        }
      });
    });
    this.elements.device.addEventListener("change", () => this.populateChannels());
    this.elements.strength.addEventListener("input", () => this.render());
    this.elements.armed.addEventListener("change", () => {
      const enable = this.elements.armed.checked;
      this.arming = enable;
      this.run(async () => {
        if (enable) {
          this.readSettings();
          await this.output.arm(this.elements.device.value);
          this.message = this.output.enabled ? "Dinosaur.OutputReady" : "Dinosaur.OutputOff";
        } else {
          await this.output.disarm();
          this.message = "Dinosaur.OutputOff";
        }
      });
    });
    this.elements["stop-output"].addEventListener("click", () => this.suspend());
    this.render();
  }

  readSettings() {
    for (const id of ["frequency", "duration"]) {
      if (!this.elements[id].reportValidity()) throw new OutputError("Dinosaur.InvalidPulse");
    }
    const settings = { channel: Number(this.elements.channel.value), strength: Number(this.elements.strength.value), frequency: Number(this.elements.frequency.value), duration: Number(this.elements.duration.value) };
    const device = this.output.devices.find(item => item.id === this.elements.device.value);
    if (!outputChannels(device).includes(settings.channel)) throw new OutputError("Dinosaur.NoDevices");
    return settings;
  }

  async collision() {
    if (!this.output.enabled) return;
    try {
      const sent = await this.output.pulse(this.readSettings(), this.localization.text("Dinosaur.CollisionPulse"));
      if (!this.output.enabled) return;
      this.message = sent ? "Dinosaur.PulseSent" : "Dinosaur.ZeroStrength";
      this.render();
    } catch (error) {
      this.output.close(error);
      this.showError(error);
    }
  }

  async stopPulse() {
    try {
      await this.output.stopPulse();
    } catch (error) {
      this.output.close(error);
      this.showError(error);
    }
  }

  suspend() {
    // Invalidate queued collisions immediately, even if a UI action is awaiting a reply.
    const cleanup = this.output.disarm();
    this.arming = false;
    this.message = this.output.error?.key ?? "Dinosaur.OutputOff";
    this.failed = this.output.error !== null;
    cleanup.catch(error => {
      this.output.close(error);
      this.showError(error);
    });
    this.render();
  }

  async run(operation) {
    if (this.busy) return;
    this.busy = true;
    this.failed = false;
    this.render();
    try {
      await operation();
    } catch (error) {
      this.showError(error);
    } finally {
      this.arming = false;
      this.busy = false;
      this.render();
    }
  }

  showError(error) {
    this.message = error.key ?? "Dinosaur.ConnectionFailed";
    this.failed = true;
    this.render();
  }

  populateDevices() {
    this.elements.device.replaceChildren(...this.output.devices.map(device => new Option(device.name, device.id)));
    this.populateChannels();
  }

  populateChannels() {
    const device = this.output.devices.find(item => item.id === this.elements.device.value);
    const selected = this.elements.channel.value;
    this.elements.channel.replaceChildren(...outputChannels(device).map(channel => new Option(this.localization.text("Dinosaur.ChannelName", String.fromCharCode(65 + channel)), String(channel))));
    if ([...this.elements.channel.options].some(option => option.value === selected)) this.elements.channel.value = selected;
  }

  localize() {
    this.populateChannels();
    this.render();
  }

  render() {
    const elements = this.elements;
    const connected = this.output.connected;
    elements.url.disabled = connected || this.busy;
    elements.token.disabled = connected || this.busy;
    elements.connect.disabled = this.busy;
    elements.connect.textContent = this.localization.text(this.busy ? "Dinosaur.Connecting" : connected ? "Dinosaur.Disconnect" : "Dinosaur.Connect");
    elements["output-fields"].disabled = !connected || this.busy || this.output.enabled;
    elements.armed.disabled = !connected || this.busy || !this.output.devices.length;
    elements.armed.checked = this.output.enabled || this.busy && this.arming;
    elements["stop-output"].disabled = !connected || (!this.output.enabled && !this.busy);
    elements["strength-value"].value = `${elements.strength.value}%`;
    elements["connection-state"].textContent = this.localization.text(connected ? "Dinosaur.Connected" : "Dinosaur.Disconnected");
    elements["connection-state"].classList.toggle("connected", connected);
    elements["output-status"].textContent = this.localization.text(this.message);
    elements["output-status"].classList.toggle("error", this.failed);
  }
}
