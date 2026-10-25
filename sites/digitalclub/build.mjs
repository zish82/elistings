// Builds the static Digital Club site into ./dist (one folder per language).
// Usage:  node build.mjs
// Optional env overrides: SITE_URL, ELISTINGS_URL, CONTACT_ENDPOINT, CONTACT_EMAIL
import { readFile, writeFile, mkdir, rm, cp } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(fileURLToPath(import.meta.url));
const dist = join(root, 'dist');
const readJson = async (p) => JSON.parse(await readFile(join(root, p), 'utf8'));

const config = await readJson('config.json');
const settings = {
  siteUrl: (process.env.SITE_URL || config.siteUrl).replace(/\/+$/, ''),
  elistingsUrl: (process.env.ELISTINGS_URL || config.elistingsUrl).replace(/\/+$/, ''),
  contactEndpoint: process.env.CONTACT_ENDPOINT || config.contactEndpoint,
  contactEmail: process.env.CONTACT_EMAIL || config.contactEmail,
};

const escapeHtml = (s) => String(s)
  .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
  .replace(/"/g, '&quot;').replace(/'/g, '&#39;');

const base = await readJson(`i18n/${config.defaultLanguage}.json`);
const strings = {};
for (const lang of config.languages) {
  const own = lang === config.defaultLanguage ? base : await readJson(`i18n/${lang}.json`);
  // en-us only lists differences; other languages must be complete.
  const missing = Object.keys(base).filter((k) => !k.startsWith('_') && !(k in own));
  if (missing.length && !lang.startsWith('en')) {
    throw new Error(`${lang}.json is missing ${missing.length} key(s): ${missing.join(', ')}`);
  }
  strings[lang] = { ...base, ...own };
}

const template = await readFile(join(root, 'src/index.html'), 'utf8');
await rm(dist, { recursive: true, force: true });
await mkdir(dist, { recursive: true });
await cp(join(root, 'src/assets'), join(dist, 'assets'), { recursive: true });

const year = String(new Date().getFullYear());
for (const lang of config.languages) {
  const t = strings[lang];
  const alternates = config.languages
    .map((l) => `  <link rel="alternate" hreflang="${strings[l].htmlLang}" href="${settings.siteUrl}/${l}/">`)
    .concat(`  <link rel="alternate" hreflang="x-default" href="${settings.siteUrl}/">`)
    .join('\n');
  const langOptions = config.languages
    .map((l) => `            <option value="${l}"${l === lang ? ' selected' : ''}>${escapeHtml(strings[l].langName)}</option>`)
    .join('\n');
  const langLinks = config.languages
    .map((l) => `        <a href="/${l}/" hreflang="${strings[l].htmlLang}" data-lang-link="${l}"${l === lang ? ' aria-current="page"' : ''}>${escapeHtml(strings[l].langName)}</a>`)
    .join('\n');
  const generated = { alternates, langOptions, langLinks }; // already-safe HTML
  const values = { ...settings, lang, year };

  const unknown = new Set();
  const html = template.replace(/\{\{(@?)([\w.-]+)\}\}/g, (_, at, key) => {
    if (at) {
      if (key in generated) return generated[key];
      if (key in values) return escapeHtml(values[key]);
    } else if (key in t) {
      return escapeHtml(t[key]);
    }
    unknown.add(at + key);
    return '';
  });
  if (unknown.size) throw new Error(`Unknown template key(s): ${[...unknown].join(', ')}`);

  await mkdir(join(dist, lang), { recursive: true });
  await writeFile(join(dist, lang, 'index.html'), html);
}

// Root page: send visitors to their saved or browser language (falls back to the default).
const map = JSON.stringify(Object.fromEntries(config.languages.map((l) => [l, l])));
const rootPage = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Digital Club</title>
<meta name="robots" content="noindex">
<link rel="canonical" href="${settings.siteUrl}/${config.defaultLanguage}/">
<style>body{background:#07070b;color:#eceaf4;font:16px system-ui,sans-serif;display:grid;place-items:center;min-height:100vh;margin:0}a{color:#22d3ee}</style>
<script>
(function () {
  var langs = ${map}, pick = null;
  try { pick = localStorage.getItem('dc-lang'); } catch (e) {}
  if (!langs[pick]) {
    var prefs = navigator.languages || [navigator.language || ''];
    for (var i = 0; i < prefs.length && !pick; i++) {
      var p = String(prefs[i]).toLowerCase();
      if (langs[p]) pick = p;
      else if (p.indexOf('en-us') === 0) pick = 'en-us';
      else if (p.indexOf('en') === 0) pick = 'en-gb';
      else if (langs[p.slice(0, 2)]) pick = p.slice(0, 2);
    }
  }
  location.replace('/' + (langs[pick] ? pick : '${config.defaultLanguage}') + '/' + location.hash);
})();
</script>
</head>
<body>
<noscript><p>${config.languages.map((l) => `<a href="/${l}/">${escapeHtml(strings[l].langName)}</a>`).join(' · ')}</p></noscript>
</body>
</html>
`;
await writeFile(join(dist, 'index.html'), rootPage);
await writeFile(join(dist, 'robots.txt'), `User-agent: *\nAllow: /\nSitemap: ${settings.siteUrl}/sitemap.xml\n`);
await writeFile(join(dist, 'sitemap.xml'),
  `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n` +
  config.languages.map((l) => `  <url><loc>${settings.siteUrl}/${l}/</loc></url>`).join('\n') + '\n</urlset>\n');

console.log(`Built ${config.languages.length} languages into ${dist}`);
