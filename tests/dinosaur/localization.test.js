import test from "node:test";
import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { Localization } from "../../modules/shared/localization.js";

const locales = new URL("../../locales/", import.meta.url);

test("each app or module catalog has matching nonempty translations and placeholders", async () => {
  const groups = await readdir(locales, { withFileTypes: true });
  for (const group of groups.filter(entry => entry.isDirectory())) {
    const read = async language => JSON.parse(await readFile(new URL(`${group.name}/${language}.json`, locales), "utf8"));
    const english = await read("en");
    for (const language of Localization.languages) {
      const catalog = await read(language);
      assert.deepEqual(Object.keys(english).sort(), Object.keys(catalog).sort(), `${group.name}/${language}`);
      assert.deepEqual(Object.keys(catalog), Object.keys(catalog).sort(), `${group.name}/${language}: sort order`);
      for (const [key, value] of Object.entries(english)) {
        assert.ok(typeof catalog[key] === "string" && catalog[key].trim(), key);
        const placeholders = text => [...text.matchAll(/\{(\d+)(?:[^}]*)\}/g)].map(match => match[1]).sort();
        assert.deepEqual(placeholders(value), placeholders(catalog[key]), `${language}: ${key}`);
      }
    }
  }
});

test("module language changes preserve formatting and unknown server errors remain readable", async () => {
  const localization = new Localization("dinosaur", "Dinosaur.Title", "Dinosaur.ServerError");
  for (const language of Localization.languages) {
    localization.catalogs[language] = JSON.parse(await readFile(new URL(`dinosaur/${language}.json`, locales), "utf8"));
  }
  localization.language = "en";
  assert.equal(localization.text("Dinosaur.ChannelName", "C"), "Channel C");
  localization.language = "zh-CN";
  assert.equal(localization.text("Dinosaur.ChannelName", "C"), "通道 C");
  assert.equal(localization.text("Error.FutureProtocol"), "服务端错误：Error.FutureProtocol");
  assert.equal(localization.text("Error.RemoteAuthentication"), localization.catalogs["zh-CN"]["Error.RemoteAuthentication"]);
  for (const [requested, language, channel] of [["ja-JP", "ja", "チャンネル C"], ["ko-KR", "ko", "채널 C"]]) {
    localization.language = Localization.normalizeLanguage(requested);
    assert.equal(localization.language, language);
    assert.equal(localization.text("Dinosaur.ChannelName", "C"), channel);
  }
});
