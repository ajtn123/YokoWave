import { Localization } from "../modules/shared/localization.js";

/** Discovers the built module catalog; each module owns its title and summary translations. */
export class ModuleDirectory {
  constructor(container, template) {
    this.container = container;
    this.template = template;
    this.entries = [];
  }

  async load() {
    const response = await fetch(new URL("modules.json", import.meta.url));
    if (!response.ok) throw new Error(`Module catalog: HTTP ${response.status}`);
    const entries = await response.json();
    this.entries = await Promise.all(entries.map(async entry => {
      const localization = new Localization(entry.id);
      await localization.load();
      const card = this.template.content.firstElementChild.cloneNode(true);
      return { ...entry, localization, card };
    }));
    this.container.replaceChildren(...this.entries.map(entry => entry.card));
  }

  localize(site) {
    for (const entry of this.entries) {
      entry.localization.language = site.language;
      entry.card.href = `${entry.url}?lang=${encodeURIComponent(site.language)}`;
      entry.card.querySelector(".module-name").textContent = entry.localization.text(entry.titleKey);
      entry.card.querySelector(".module-description").textContent = entry.localization.text(entry.descriptionKey);
      entry.card.querySelector(".module-open").textContent = `${site.text("Site.Open")} ↗`;
    }
  }
}
