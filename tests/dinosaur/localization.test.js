import test from "node:test";
import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { Localization } from "../../modules/dinosaur/localization.js";

const locales = new URL("../../locales/", import.meta.url);

test("each app or module catalog has matching nonempty translations and placeholders", async () => {
  const groups = await readdir(locales, { withFileTypes: true });
  for (const group of groups.filter(entry => entry.isDirectory())) {
    const read = async language => JSON.parse(await readFile(new URL(`${group.name}/${language}.json`, locales), "utf8"));
    const english = await read("en");
    const chinese = await read("zh-CN");
    assert.deepEqual(Object.keys(english).sort(), Object.keys(chinese).sort(), group.name);
    for (const [key, value] of Object.entries(english)) {
      assert.ok(typeof value === "string" && value.trim(), key);
      assert.ok(typeof chinese[key] === "string" && chinese[key].trim(), key);
      const placeholders = text => [...text.matchAll(/\{(\d+)(?:[^}]*)\}/g)].map(match => match[1]).sort();
      assert.deepEqual(placeholders(value), placeholders(chinese[key]), key);
    }
  }
});

test("module language changes preserve formatting and unknown server errors remain readable", async () => {
  const localization = new Localization();
  for (const language of ["en", "zh-CN"]) {
    localization.catalogs[language] = JSON.parse(await readFile(new URL(`dinosaur/${language}.json`, locales), "utf8"));
  }
  localization.language = "en";
  assert.equal(localization.text("Dinosaur.ChannelName", "C"), "Channel C");
  localization.language = "zh-CN";
  assert.equal(localization.text("Dinosaur.ChannelName", "C"), "通道 C");
  assert.equal(localization.text("Error.FutureProtocol"), "服务端错误：Error.FutureProtocol");
  assert.equal(localization.text("Error.RemoteAuthentication"), localization.catalogs["zh-CN"]["Error.RemoteAuthentication"]);
});
