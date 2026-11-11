/** Module-owned JSON catalogs live alongside the app catalogs in the root locales directory. */
export class Localization {
  static languages = ["en", "zh-CN", "ja", "ko"];

  static normalizeLanguage(language) {
    const code = language?.split(/[-_]/)[0].toLowerCase();
    return code === "zh" ? "zh-CN" : ["ja", "ko"].includes(code) ? code : "en";
  }

  constructor(group, titleKey, errorKey) {
    this.group = group;
    this.titleKey = titleKey;
    this.errorKey = errorKey;
    const requested = new URLSearchParams(globalThis.location?.search ?? "").get("lang");
    this.language = Localization.normalizeLanguage(requested ?? globalThis.navigator?.language);
    this.catalogs = {};
  }

  async load() {
    const entries = await Promise.all(Localization.languages.map(async language => {
      const response = await fetch(new URL(`../../locales/${this.group}/${language}.json`, import.meta.url));
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      return [language, await response.json()];
    }));
    this.catalogs = Object.fromEntries(entries);
  }

  text(key, ...values) {
    let text = this.catalogs[this.language]?.[key] ?? this.catalogs.en?.[key];
    if (!text && this.errorKey && key.startsWith("Error.")) return this.text(this.errorKey, key);
    text ??= key;
    return text.replace(/\{(\d+)\}/g, (_, index) => String(values[Number(index)] ?? ""));
  }

  apply() {
    document.documentElement.lang = this.language;
    if (this.titleKey) document.title = `YokoWave · ${this.text(this.titleKey)}`;
    for (const element of document.querySelectorAll("[data-i18n]")) element.textContent = this.text(element.dataset.i18n);
    for (const element of document.querySelectorAll("[data-i18n-aria]")) element.setAttribute("aria-label", this.text(element.dataset.i18nAria));
  }
}
