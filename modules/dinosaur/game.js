const GROUND = 300;
const STEP = 1 / 120;

export class Dinosaur {
  constructor() {
    this.reset();
  }

  reset() {
    this.x = 72;
    this.y = GROUND;
    this.velocity = 0;
    this.ducking = false;
  }

  jump() {
    if (this.y === GROUND && !this.ducking) this.velocity = -640;
  }

  update(seconds) {
    this.velocity += (this.ducking ? 3200 : 1800) * seconds;
    this.y = Math.min(GROUND, this.y + this.velocity * seconds);
    if (this.y === GROUND) this.velocity = 0;
  }

  get bounds() {
    const low = this.ducking && this.y === GROUND;
    return { x: this.x + 5, y: this.y - (low ? 24 : 50) + 4, width: low ? 44 : 28, height: low ? 18 : 42 };
  }
}

export class Obstacle {
  constructor(x, flying, random) {
    this.x = x;
    this.flying = flying;
    this.width = flying ? 42 : 20 + Math.floor(random() * 3) * 12;
    this.height = flying ? 28 : 38 + Math.floor(random() * 3) * 12;
    this.y = flying ? GROUND - 68 : GROUND - this.height;
  }

  get bounds() {
    return { x: this.x + 3, y: this.y + 3, width: this.width - 6, height: this.height - 6 };
  }

  intersects(other) {
    const box = this.bounds;
    return box.x < other.x + other.width && box.x + box.width > other.x && box.y < other.y + other.height && box.y + box.height > other.y;
  }
}

/** Fixed-step world; rendering, keyboard input and device I/O stay outside. */
export class DinosaurGame {
  constructor(random = Math.random) {
    this.random = random;
    this.player = new Dinosaur();
    this.state = "ready";
    this.distance = 0;
    this.elapsed = 0;
    this.obstacles = [];
    this.accumulator = 0;
    this.nextObstacle = 1.2;
  }

  get score() { return Math.floor(this.distance / 10); }
  get speed() { return Math.min(620, 320 + this.elapsed * 3); }

  start() {
    this.player.reset();
    this.obstacles = [];
    this.distance = 0;
    this.elapsed = 0;
    this.accumulator = 0;
    this.nextObstacle = 1.2;
    this.state = "running";
  }

  pause() {
    if (this.state === "running") this.state = "paused";
    this.player.ducking = false;
  }

  resume() {
    if (this.state === "paused") this.state = "running";
  }

  jump() {
    if (this.state === "running") this.player.jump();
  }

  duck(value) {
    this.player.ducking = this.state === "running" && value;
  }

  /** Returns true only on the transition to a collision. Long frame gaps are discarded. */
  update(seconds) {
    if (this.state !== "running" || !Number.isFinite(seconds) || seconds <= 0) return false;
    this.accumulator += Math.min(seconds, .1);
    while (this.accumulator + 1e-9 >= STEP) {
      this.accumulator -= STEP;
      this.elapsed += STEP;
      this.distance += this.speed * STEP;
      this.player.update(STEP);
      for (const obstacle of this.obstacles) obstacle.x -= this.speed * STEP;
      this.obstacles = this.obstacles.filter(obstacle => obstacle.x + obstacle.width > 0);
      this.nextObstacle -= STEP;
      if (this.nextObstacle <= 0) {
        this.obstacles.push(new Obstacle(980, this.score > 250 && this.random() < .25, this.random));
        this.nextObstacle = 1.15 + this.random() * .65;
      }
      if (this.obstacles.some(obstacle => obstacle.intersects(this.player.bounds))) {
        this.state = "over";
        this.player.ducking = false;
        return true;
      }
    }
    return false;
  }
}
