import { DinosaurGame } from "./game.js";
import { GameRenderer } from "./renderer.js";
import { Localization } from "./localization.js";
import { OutputPanel } from "./output-panel.js";

class DinosaurApp {
  constructor(localization) {
    this.localization = localization;
    this.game = new DinosaurGame();
    this.canvas = document.getElementById("game");
    this.renderer = new GameRenderer(this.canvas);
    this.output = new OutputPanel(localization);
    this.best = this.readBest();
    this.lastFrame = null;
    this.starting = false;
    this.playEpoch = 0;
    this.elements = Object.fromEntries(["overlay", "game-state", "game-hint", "play", "pause", "score", "best", "language", "game-announcement"].map(id => [id, document.getElementById(id)]));
    this.bindControls();
    this.localize();
    requestAnimationFrame(timestamp => this.frame(timestamp));
  }

  bindControls() {
    this.elements.play.addEventListener("click", () => this.play());
    this.elements.pause.addEventListener("click", () => this.pause());
    this.elements.language.value = this.localization.language;
    this.elements.language.addEventListener("change", () => {
      this.localization.language = this.elements.language.value;
      this.localize();
    });
    document.getElementById("jump").addEventListener("pointerdown", event => {
      event.preventDefault();
      this.jump();
    });
    const duck = document.getElementById("duck");
    duck.addEventListener("pointerdown", event => {
      event.preventDefault();
      duck.setPointerCapture(event.pointerId);
      this.game.duck(true);
    });
    for (const name of ["pointerup", "pointercancel", "lostpointercapture"]) duck.addEventListener(name, () => this.game.duck(false));
    this.canvas.addEventListener("pointerdown", event => {
      event.preventDefault();
      this.canvas.focus();
      this.jump();
    });
    document.addEventListener("keydown", event => this.keyDown(event));
    document.addEventListener("keyup", event => {
      if (event.code === "ArrowDown") this.game.duck(false);
    });
    window.addEventListener("blur", () => this.pause());
    document.addEventListener("visibilitychange", () => {
      if (document.hidden) this.pause();
    });
    window.addEventListener("pagehide", () => this.output.output.close());
  }

  localize() {
    this.localization.apply();
    this.output.localize();
    this.renderState();
  }

  keyDown(event) {
    if (event.code === "Escape") {
      this.pause();
      return;
    }
    if (event.target.closest("input, select, textarea, button, a, [contenteditable]")) return;
    if (["Space", "ArrowUp", "ArrowDown", "KeyP"].includes(event.code)) event.preventDefault();
    if (event.repeat) return;
    if (event.code === "Space" || event.code === "ArrowUp") this.jump();
    if (event.code === "ArrowDown") this.game.duck(true);
    if (event.code === "KeyP") {
      if (this.game.state === "paused") this.play();
      else this.pause();
    }
  }

  jump() {
    if (this.game.state === "running") this.game.jump();
    else this.play();
  }

  async play() {
    if (this.starting || this.game.state === "running") return;
    this.starting = true;
    const epoch = ++this.playEpoch;
    this.elements.play.disabled = true;
    try {
      await this.output.stopPulse();
      if (document.hidden || epoch !== this.playEpoch) return;
      if (this.game.state === "paused") this.game.resume();
      else this.game.start();
      this.lastFrame = null;
      this.canvas.focus();
      this.renderState();
    } finally {
      this.starting = false;
      this.elements.play.disabled = false;
    }
  }

  pause() {
    ++this.playEpoch;
    this.game.pause();
    this.output.suspend();
    this.lastFrame = null;
    this.renderState();
  }

  frame(timestamp) {
    const elapsed = this.lastFrame === null ? 0 : (timestamp - this.lastFrame) / 1000;
    this.lastFrame = timestamp;
    if (this.game.update(elapsed)) {
      if (this.game.score > this.best) {
        this.best = this.game.score;
        this.saveBest();
      }
      // CollisionOutput contains its own asynchronous error and cleanup boundary.
      this.output.collision();
      this.renderState();
    }
    this.renderer.draw(this.game);
    this.elements.score.textContent = String(this.game.score).padStart(5, "0");
    requestAnimationFrame(next => this.frame(next));
  }

  renderState() {
    const state = this.game.state;
    const keys = { ready: "Dinosaur.Ready", paused: "Dinosaur.Paused", over: "Dinosaur.Collision", running: "Dinosaur.Title" };
    this.elements.overlay.hidden = state === "running";
    this.elements["game-state"].textContent = this.localization.text(keys[state]);
    this.elements["game-hint"].textContent = this.localization.text(state === "over" ? "Dinosaur.CollisionScore" : "Dinosaur.StartHint", this.game.score);
    this.elements.play.textContent = this.localization.text(state === "paused" ? "Dinosaur.Resume" : state === "over" ? "Dinosaur.Restart" : "Dinosaur.Start");
    this.elements.pause.disabled = state !== "running";
    this.elements.best.textContent = String(this.best).padStart(5, "0");
    this.elements["game-announcement"].textContent = state === "over" ? this.localization.text("Dinosaur.CollisionScore", this.game.score) : "";
  }

  readBest() {
    try {
      const value = Number(localStorage.getItem("yokowave.dinosaur.best"));
      return Number.isSafeInteger(value) && value >= 0 ? value : 0;
    } catch {
      // Storage can be blocked; gameplay remains available without a saved score.
      return 0;
    }
  }

  saveBest() {
    try {
      localStorage.setItem("yokowave.dinosaur.best", String(this.best));
    } catch {
      // Best score is optional; no connection settings or credentials are stored.
    }
  }
}

const localization = new Localization();
try {
  await localization.load();
  new DinosaurApp(localization);
} catch (error) {
  const element = document.getElementById("load-error");
  element.hidden = false;
  element.textContent = `${error.message} · locales/dinosaur/`;
}
