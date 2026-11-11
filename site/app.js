import { Localization } from "../modules/shared/localization.js";
import { ModuleDirectory } from "./module-directory.js";

class HomePage {
  constructor(localization, modules) {
    this.localization = localization;
    this.modules = modules;
    const language = document.getElementById("language");
    language.value = localization.language;
    language.addEventListener("change", () => {
      localization.language = language.value;
      this.localize();
    });
    this.localize();
  }

  localize() {
    this.localization.apply();
    this.modules.localize(this.localization);
    for (const anchor of document.querySelectorAll("a.brand")) anchor.href = `./?lang=${encodeURIComponent(this.localization.language)}`;
  }
}

const localization = new Localization("site", "Site.LocalName");
try {
  await localization.load();
  localization.apply();
  const modules = new ModuleDirectory(document.getElementById("module-list"), document.getElementById("module-template"));
  try {
    await modules.load();
  } catch (error) {
    document.getElementById("module-error").hidden = false;
    console.error(error);
  }
  new HomePage(localization, modules);
} catch (error) {
  document.getElementById("load-error").hidden = false;
  console.error(error);
}
