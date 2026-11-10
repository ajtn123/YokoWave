/** Draws original pixel shapes in a fixed logical viewport. */
export class GameRenderer {
  constructor(canvas) {
    this.canvas = canvas;
    this.context = canvas.getContext("2d");
    this.theme = matchMedia("(prefers-color-scheme: dark)");
    this.theme.addEventListener("change", () => this.readColors());
    this.readColors();
    this.observer = new ResizeObserver(() => this.resize());
    this.observer.observe(canvas);
    this.resize();
  }

  readColors() {
    const style = getComputedStyle(this.canvas);
    this.colors = Object.fromEntries(["surface", "ink", "accent", "terrain", "cloud"].map(name => [name, style.getPropertyValue(`--${name}`).trim()]));
  }

  resize() {
    const scale = (this.canvas.clientWidth || 960) * Math.min(devicePixelRatio || 1, 3) / 960;
    this.canvas.width = Math.round(960 * scale);
    this.canvas.height = Math.round(360 * scale);
  }

  draw(game) {
    const ctx = this.context;
    ctx.setTransform(this.canvas.width / 960, 0, 0, this.canvas.height / 360, 0, 0);
    ctx.fillStyle = this.colors.surface;
    ctx.fillRect(0, 0, 960, 360);
    ctx.fillStyle = this.colors.cloud;
    for (let i = 0; i < 4; i++) {
      const x = ((i * 290 + 140 - game.distance * .12) % 1160 + 1160) % 1160 - 100;
      const y = 72 + (i % 3) * 34;
      ctx.fillRect(x, y, 68, 8);
      ctx.fillRect(x + 15, y - 8, 34, 8);
    }
    ctx.fillStyle = this.colors.terrain;
    ctx.fillRect(0, 300, 960, 2);
    for (let i = 0; i < 30; i++) {
      const x = ((i * 53 - game.distance) % 1590 + 1590) % 1590;
      ctx.fillRect(x, 310 + i % 3 * 9, 3 + i % 4 * 3, 2);
    }
    for (const obstacle of game.obstacles) this.drawObstacle(obstacle, game.elapsed);
    this.drawDinosaur(game);
  }

  drawDinosaur(game) {
    const ctx = this.context;
    const player = game.player;
    const low = player.ducking && player.y === 300;
    ctx.save();
    ctx.translate(player.x, player.y);
    ctx.fillStyle = game.state === "over" ? this.colors.accent : this.colors.ink;
    if (low) {
      ctx.fillRect(0, -23, 40, 15);
      ctx.fillRect(29, -26, 24, 14);
      ctx.fillRect(-9, -29, 9, 13);
    } else {
      ctx.fillRect(16, -50, 25, 21);
      ctx.fillRect(10, -32, 21, 25);
      ctx.fillRect(1, -24, 18, 13);
      ctx.fillRect(-8, -31, 9, 17);
      ctx.fillRect(28, -24, 9, 5);
      ctx.fillStyle = this.colors.surface;
      ctx.fillRect(32, -46, 4, 4);
      ctx.fillRect(28, -34, 13, 3);
    }
    ctx.fillStyle = game.state === "over" ? this.colors.accent : this.colors.ink;
    const stride = game.state === "running" && player.y === 300 ? Math.floor(game.elapsed * 12) % 2 : 0;
    ctx.fillRect(6, -10, 7, stride ? 4 : 10);
    ctx.fillRect(low ? 29 : 23, -10, 7, stride ? 10 : 4);
    ctx.fillRect(6, stride ? -6 : -3, 12, 3);
    ctx.fillRect(low ? 29 : 23, stride ? -3 : -6, 12, 3);
    ctx.restore();
  }

  drawObstacle(obstacle, elapsed) {
    const ctx = this.context;
    ctx.fillStyle = this.colors.ink;
    const { x, y, width, height } = obstacle;
    if (obstacle.flying) {
      ctx.fillRect(x, y + 10, width, 9);
      ctx.fillRect(x + 7, y + 4, 9, 10);
      ctx.fillRect(x + 22, y + (Math.floor(elapsed * 7) % 2 ? 0 : 16), 10, 12);
      return;
    }
    for (let offset = 0; offset < width; offset += 14) {
      const top = y + (offset % 28 ? 10 : 0);
      ctx.fillRect(x + offset + 4, top, 8, y + height - top);
      ctx.fillRect(x + offset - 1, top + 12, 5, 14);
      ctx.fillRect(x + offset, top + 22, 15, 5);
    }
  }
}
