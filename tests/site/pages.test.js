import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFile, readdir, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { Localization } from "../../modules/shared/localization.js";

const root = fileURLToPath(new URL("../../", import.meta.url));
const output = path.join(root, "artifacts/pages-tests/YokoWave");
execFileSync("pwsh", ["-NoProfile", "-File", "scripts/build-pages.ps1", "-OutputDirectory", "artifacts/pages-tests/YokoWave"], { cwd: root });

test("Pages discovers every public module and publishes only static sources", async () => {
  const catalog = JSON.parse(await readFile(path.join(output, "site/modules.json"), "utf8"));
  const modules = [];
  for (const directory of await readdir(path.join(root, "modules"), { withFileTypes: true })) {
    if (!directory.isDirectory()) continue;
    try {
      await stat(path.join(root, "modules", directory.name, "index.html"));
      modules.push(directory.name);
    } catch (error) {
      if (error.code !== "ENOENT") throw error;
    }
  }
  assert.deepEqual(catalog.map(module => module.id).sort(), modules.sort());
  const payload = await readdir(output);
  assert.deepEqual(payload.sort(), [".nojekyll", "assets", "index.html", "locales", "modules", "site"]);
  assert.deepEqual((await readdir(path.join(output, "locales"))).sort(), ["site", ...modules].sort());
  for (const module of catalog) {
    assert.equal(module.url, `modules/${module.id}/`);
    for (const language of Localization.languages) {
      const locale = JSON.parse(await readFile(path.join(output, "locales", module.id, `${language}.json`), "utf8"));
      assert.ok(locale[module.titleKey]?.trim());
      assert.ok(locale[module.descriptionKey]?.trim());
    }
  }
});

test("Pages works under a repository prefix with pre-rendered navigation and evergreen downloads", async () => {
  const origin = new URL("https://example.test/YokoWave/");
  const homepage = await readFile(path.join(output, "index.html"), "utf8");
  assert.match(homepage, /跨平台设备控制/);
  assert.match(homepage, /href="modules\/dinosaur\/\?lang=zh-CN"/);
  assert.match(homepage, /https:\/\/github.com\/ajtn123\/YokoWave\/releases\/latest/);
  assert.doesNotMatch(homepage, /releases\/download\/|v\d+\.\d+\.\d+|data-i18n="[^"]+"><\//);
  for (const file of ["index.html", "modules/dinosaur/index.html"]) {
    const source = await readFile(path.join(output, file), "utf8");
    const base = new URL(file, origin);
    for (const match of source.matchAll(/(?:src|href)="([^"#]+)"/g)) {
      const url = new URL(match[1], base);
      if (url.origin !== origin.origin) continue;
      assert.ok(url.pathname.startsWith(origin.pathname), url.href);
      const relative = decodeURIComponent(url.pathname.slice(origin.pathname.length));
      await stat(path.join(output, relative.endsWith("/") ? `${relative}index.html` : relative));
    }
  }
});

test("shared localization loads module-owned catalogs and normalizes region variants", async t => {
  const requests = [];
  t.mock.method(globalThis, "fetch", async url => {
    requests.push(url.href);
    return { ok: true, json: async () => ({ "Site.LocalName": "波枢" }) };
  });
  const localization = new Localization("site", "Site.LocalName");
  await localization.load();
  assert.equal(requests.length, Localization.languages.length);
  for (const language of Localization.languages) {
    assert.ok(requests.some(url => url.endsWith(`/locales/site/${language}.json`)));
  }
  assert.equal(Localization.normalizeLanguage("JA-jp"), "ja");
  assert.equal(Localization.normalizeLanguage("ko-KR"), "ko");
  assert.equal(localization.text("Site.LocalName"), "波枢");
});
