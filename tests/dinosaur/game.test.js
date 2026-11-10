import test from "node:test";
import assert from "node:assert/strict";
import { DinosaurGame, Obstacle } from "../../modules/dinosaur/game.js";

test("jump returns to the ground, cannot double jump, and duck changes the hit box", () => {
  const game = new DinosaurGame(() => .5);
  game.start();
  game.jump();
  game.update(.1);
  const velocity = game.player.velocity;
  assert.ok(game.player.y < 300);
  game.jump();
  assert.equal(game.player.velocity, velocity);
  for (let i = 0; i < 10; i++) game.update(.1);
  assert.equal(game.player.y, 300);
  const standing = game.player.bounds;
  game.duck(true);
  assert.ok(game.player.bounds.height < standing.height);
  game.jump();
  assert.equal(game.player.velocity, 0);
});

test("collision is reported once, freezes score, and restart clears the world", () => {
  const game = new DinosaurGame(() => .5);
  game.start();
  game.obstacles.push(new Obstacle(85, false, () => 0));
  assert.equal(game.update(.02), true);
  assert.equal(game.state, "over");
  const distance = game.distance;
  assert.equal(game.update(.1), false);
  assert.equal(game.distance, distance);
  game.start();
  assert.equal(game.state, "running");
  assert.equal(game.obstacles.length, 0);
  assert.equal(game.score, 0);
});

test("duck avoids a low flying obstacle but not a cactus", () => {
  const game = new DinosaurGame(() => .5);
  game.start();
  const bird = new Obstacle(80, true, () => 0);
  assert.equal(bird.intersects(game.player.bounds), true);
  game.duck(true);
  assert.equal(bird.intersects(game.player.bounds), false);
  assert.equal(new Obstacle(80, false, () => 0).intersects(game.player.bounds), true);
});

test("pause freezes physics and releases duck; delayed frames do not teleport the world", () => {
  const game = new DinosaurGame(() => .5);
  game.start();
  game.duck(true);
  game.pause();
  game.update(10);
  assert.equal(game.distance, 0);
  assert.equal(game.player.ducking, false);
  game.resume();
  game.update(10);
  assert.ok(game.distance < 35);
  assert.equal(game.update(NaN), false);
});

test("fixed-step scoring does not depend on display refresh rate", () => {
  const first = new DinosaurGame(() => .5);
  const second = new DinosaurGame(() => .5);
  first.start();
  second.start();
  for (let i = 0; i < 60; i++) first.update(1 / 60);
  for (let i = 0; i < 144; i++) second.update(1 / 144);
  assert.equal(first.score, second.score);
  assert.ok(Math.abs(first.distance - second.distance) < 3);
});
