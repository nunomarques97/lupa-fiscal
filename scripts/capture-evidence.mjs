// Drives the real UI against the real index and saves evidence screenshots. By default they go to
// web/test-results/evidence/ (gitignored), so a check run leaves the source tree untouched; pass
// --publish (npm run ui:evidence:publish) to write them to docs/evidence/ instead.
// Starts the built API (src/LupaFiscal.Api, Debug build) on http://localhost:4401 serving web/dist,
// then uses the Chromium already installed for Playwright (never downloads a browser) at 1440 and
// 390 px. Captures: initial, validation, loading, results, filters (plus the mobile filter sheet),
// results from several taxes, the tax filter with its scoped article list (plus the mobile sheet),
// empty and error. Asserts no horizontal overflow, keyboard-only search and filter use, that the
// article select is disabled until a tax is chosen and then lists only that tax's articles, URL
// restore, request ownership (a delayed earlier response, success or failure, never replaces a later
// one, also when the tax changes mid-request) and retry of the last submitted search. Exits non-zero
// on any failed assertion.
// Prerequisites: `dotnet build LupaFiscal.slnx`, `npm --prefix web run build`, and the index in data/.
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, statSync, unlinkSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from '@playwright/test';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const publish = process.argv.slice(2).includes('--publish');
const evidenceDir = publish ? join(root, 'docs', 'evidence') : join(root, 'web', 'test-results', 'evidence');
const apiDll = join(root, 'src', 'LupaFiscal.Api', 'bin', 'Debug', 'net10.0', 'LupaFiscal.Api.dll');
const webRoot = join(root, 'web', 'dist');
const BASE = 'http://localhost:4401';

const VIEWPORTS = [
  { width: 1440, height: 900 },
  { width: 390, height: 844 },
];
const QUERY = 'Posso deduzir as despesas de educação dos meus filhos no IRS?';
const OTHER_QUERY = 'Vendi a casa onde morava. Tenho de pagar mais-valias?';
// A question whose closest rulings come from several taxes (IMT, IRS and Imposto do Selo).
const MULTI_QUERY = 'Doação de um imóvel a um filho: que impostos pago?';
const ARTICLE_HINT = 'Escolha primeiro um imposto.';
// Display names of the tax codes, as in web/src/app/format.ts.
const TAX_LABELS = {
  CIRS: 'IRS',
  CIRC: 'IRC',
  CIVA: 'IVA',
  CIMI: 'IMI',
  CIMT: 'IMT',
  CIUC: 'IUC',
  SELO: 'Imposto do Selo',
  EBF: 'Estatuto dos Benefícios Fiscais',
  RITI: 'RITI',
  LGT: 'LGT',
  DSRI: 'Relações internacionais',
  CESE: 'CESE',
  CSB: 'CSB',
};
const VALIDATION = 'Escreva uma pergunta para pesquisar.';
const ERROR_ANNOUNCEMENT = 'Não foi possível pesquisar.';
const EMPTY_ANNOUNCEMENT = 'Nenhuma informação vinculativa encontrada.';

const failures = [];
const passed = [];
function check(label, condition, message) {
  if (condition) passed.push(`${label}: ${message}`);
  else failures.push(`${label}: ${message}`);
  return condition;
}

function preflight() {
  const missing = [];
  if (!existsSync(join(webRoot, 'index.html'))) missing.push('web/dist/index.html (run: npm --prefix web run build)');
  if (!existsSync(apiDll)) missing.push(`${relative(root, apiDll)} (run: dotnet build LupaFiscal.slnx)`);
  if (!existsSync(join(root, 'data', 'lupa-fiscal.db'))) missing.push('data/lupa-fiscal.db (run the crawl and index commands)');
  if (!existsSync(chromium.executablePath())) missing.push(`Playwright Chromium at ${chromium.executablePath()} (no browser is downloaded)`);
  if (missing.length) {
    console.error('Missing prerequisites:');
    for (const m of missing) console.error(`- ${m}`);
    process.exit(1);
  }
}

async function getJson(path) {
  const response = await fetch(BASE + path);
  if (!response.ok) throw new Error(`${path} returned ${response.status}`);
  return response.json();
}

async function startApi() {
  try {
    await fetch(`${BASE}/api/health`);
    console.error('Something already listens on http://localhost:4401. Stop it so this run tests the current build.');
    process.exit(1);
  } catch {
    // Port is free.
  }
  const child = spawn('dotnet', [apiDll, '--web-root', webRoot], { cwd: root, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = '';
  child.stdout.on('data', d => (output += d));
  child.stderr.on('data', d => (output += d));
  let exited = false;
  child.on('exit', () => (exited = true));
  const deadline = Date.now() + 180_000;
  while (Date.now() < deadline) {
    if (exited) throw new Error(`The API exited during startup:\n${output}`);
    try {
      const health = await getJson('/api/health');
      if (health.status === 'ok') {
        console.log(`API ready: ${health.rulings} rulings, ${health.chunks} chunks.`);
        return child;
      }
    } catch {
      // Not ready yet.
    }
    await new Promise(r => setTimeout(r, 500));
  }
  child.kill();
  throw new Error(`The API did not become ready within 180 s:\n${output}`);
}

const searchUrl = params => '/api/search?' + new URLSearchParams(params).toString();
const pageUrl = params => BASE + '/?' + new URLSearchParams(params).toString();
const NUMBERED_ARTICLE = /^\d+(-[A-Za-z]+)*$/;
const articleLabel = value => {
  if (!NUMBERED_ARTICLE.test(value)) return value;
  const [number, ...suffix] = value.split('-');
  return number + '.º' + (suffix.length ? '-' + suffix.join('-') : '');
};
const articlePhrase = value => (NUMBERED_ARTICLE.test(value) ? `artigo ${articleLabel(value)}` : value);
const articleCitation = value => (NUMBERED_ARTICLE.test(value) ? `Art. ${articleLabel(value)}` : value);
const taxLabel = code => TAX_LABELS[code] ?? code;
const articlesOf = (facets, tax) => facets.articles.filter(a => a.tax === tax).map(a => a.value);

/**
 * Real data for the scenarios: a tax and article filter that keeps results, a filter combination
 * with none, and a question answered from several taxes with a scoped tax and article filter.
 */
async function scenarioData() {
  const facets = await getJson('/api/facets');
  const first = await getJson(searchUrl({ q: QUERY }));
  const other = await getJson(searchUrl({ q: OTHER_QUERY }));
  if (!first.results.length || !other.results.length) throw new Error('The sample questions return no results; is the index built?');
  if (first.results[0].rulingId === other.results[0].rulingId) throw new Error('The two sample questions share their top ruling; pick different questions.');
  const top = first.results.find(r => r.article && r.date);
  const filter = { tax: top.tax, article: top.article, year: top.date.slice(0, 4) };
  const filtered = await getJson(searchUrl({ q: QUERY, ...filter }));
  const taxOnly = await getJson(searchUrl({ q: QUERY, tax: filter.tax }));

  // The least common article of the tax combined with years it does not appear in.
  let empty = null;
  const articles = facets.articles.filter(a => a.tax === filter.tax).sort((a, b) => a.count - b.count).slice(0, 10);
  outer: for (const article of articles) {
    for (const year of facets.years.slice(0, 5)) {
      const result = await getJson(searchUrl({ q: QUERY, tax: filter.tax, article: article.value, year: String(year.value) }));
      if (!result.results.length) {
        empty = { tax: filter.tax, article: article.value, year: String(year.value) };
        break outer;
      }
    }
  }
  if (!empty) throw new Error('No filter combination without results was found.');

  // Results from several taxes, and a tax (other than the first scenario's) with one of its articles.
  const multi = await getJson(searchUrl({ q: MULTI_QUERY }));
  const multiTaxes = new Set(multi.results.map(r => r.tax));
  if (multiTaxes.size < 3) throw new Error(`The multi-tax question returns ${multiTaxes.size} taxes; pick another question.`);
  const scoped = multi.results.find(r => r.tax !== filter.tax && r.article && articlesOf(facets, r.tax).includes(r.article));
  if (!scoped) throw new Error('No result of the multi-tax question has a tax and article to filter on.');
  const taxFilter = { tax: scoped.tax, article: scoped.article };
  if (articlesOf(facets, taxFilter.tax).length === facets.articles.length) throw new Error('Only one tax has articles; the scoped list cannot be checked.');
  const multiTax = await getJson(searchUrl({ q: MULTI_QUERY, tax: taxFilter.tax }));
  const multiTaxArticle = await getJson(searchUrl({ q: MULTI_QUERY, ...taxFilter }));
  return { facets, first, other, filter, filtered, taxOnly, empty, multi, taxFilter, multiTax, multiTaxArticle };
}

// ---- Page helpers ----

async function openPage(browser, viewport, label) {
  const context = await browser.newContext({ viewport, deviceScaleFactor: 1, reducedMotion: 'reduce' });
  const page = await context.newPage();
  page.on('request', request => {
    const url = request.url();
    if (!url.startsWith(BASE) && !url.startsWith('data:')) check(label, false, `requested a non-local URL: ${url}`);
  });
  page.on('pageerror', error => check(label, false, `page error: ${error.message}`));
  page.on('console', message => {
    if (message.type() === 'error' && !/status of 500|Failed to load resource/.test(message.text())) {
      check(label, false, `console error: ${message.text()}`);
    }
  });
  // Records every value the polite live region takes, to check one announcement per completed search.
  await page.addInitScript(() => {
    window.__live = [];
    const start = () => {
      const live = document.querySelector('[data-live]');
      if (!live) return requestAnimationFrame(start);
      new MutationObserver(() => window.__live.push(live.textContent.trim())).observe(live, { childList: true, characterData: true, subtree: true });
    };
    document.addEventListener('DOMContentLoaded', start);
  });
  return { context, page };
}

async function waitForFacets(page) {
  await page.waitForFunction(() => document.querySelectorAll('#d-tax option').length > 1, null, { timeout: 15_000 });
}

const announcements = async page => page.evaluate(() => window.__live.filter(Boolean));
const clearAnnouncements = async page => page.evaluate(() => (window.__live.length = 0));
const activeId = page => page.evaluate(() => document.activeElement?.id || document.activeElement?.tagName || '');
const activeMatches = (page, selector) => page.evaluate(s => !!document.activeElement?.matches(s), selector);
const topRulingId = page => page.locator('[data-result]').first().getAttribute('data-ruling-id');
const liveText = page => page.locator('[data-live]').textContent().then(t => t.trim());

async function waitForState(page, state) {
  const selector = { results: '[data-result]', empty: '[data-empty]', error: '[data-error]', loading: '[data-skeleton]' }[state];
  await page.waitForSelector(selector, { timeout: 15_000 });
  if (state !== 'loading') await page.waitForFunction(() => !document.querySelector('[data-region]')?.hasAttribute('aria-busy'), null, { timeout: 15_000 });
}

/**
 * Runs an action that starts a search with the given filters ('' for none) and waits until that search
 * has answered and its results are shown, so the stale results of the previous search never pass for it.
 */
async function searchSettled(page, filters, action) {
  const answered = page.waitForResponse(
    response => {
      const url = new URL(response.url());
      return url.pathname === '/api/search' && Object.entries(filters).every(([key, value]) => (url.searchParams.get(key) ?? '') === value);
    },
    { timeout: 15_000 },
  );
  const result = await action();
  await answered;
  await waitForState(page, 'results');
  return result;
}

/** The article select of the rail ("d") or the sheet ("m") is disabled and described by its hint. */
async function articleDisabled(page, prefix) {
  return page.evaluate(
    ({ prefix, hint }) => {
      const select = document.getElementById(`${prefix}-article`);
      const text = document.getElementById(`${prefix}-article-hint`)?.textContent.trim();
      return !!select?.disabled && select.getAttribute('aria-describedby') === `${prefix}-article-hint` && text === hint && select.value === '';
    },
    { prefix, hint: ARTICLE_HINT },
  );
}

/** The values of the article select (without "Todos"), enabled and without a hint. */
async function articleOptions(page, prefix) {
  return page.evaluate(prefix => {
    const select = document.getElementById(`${prefix}-article`);
    if (!select || select.disabled || select.hasAttribute('aria-describedby')) return null;
    return [...select.options].map(o => o.value).filter(Boolean);
  }, prefix);
}

const sameSet = (a, b) => !!a && !!b && a.length === b.length && [...a].sort().join('\n') === [...b].sort().join('\n');

async function tabTo(page, selector, { backwards = false, max = 40 } = {}) {
  for (let i = 0; i < max; i++) {
    if (await activeMatches(page, selector)) return true;
    await page.keyboard.press(backwards ? 'Shift+Tab' : 'Tab');
  }
  return activeMatches(page, selector);
}

/**
 * Chooses an option of the focused select with the keyboard only: type-ahead on the ASCII start of
 * the label (Playwright sends other characters as text input, which Chromium routes to the last text
 * field), then arrow keys.
 */
async function chooseWithKeyboard(page, value, label) {
  const valueOf = () => page.evaluate(() => (document.activeElement instanceof HTMLSelectElement ? document.activeElement.value : null));
  await page.keyboard.type(label.replace(/[^\x20-\x7e].*$/s, ''));
  await page.waitForTimeout(1100);
  let current = await valueOf();
  for (const key of ['ArrowDown', 'Home']) {
    if (key === 'Home') await page.keyboard.press('Home');
    for (let i = 0; i < 200 && current !== value && current !== null; i++) {
      await page.keyboard.press('ArrowDown');
      const next = await valueOf();
      if (next === current) break;
      current = next;
    }
    if (current === value || current === null) break;
    current = await valueOf();
  }
  return current === value;
}

async function replaceQuery(page, text) {
  await page.keyboard.press('Control+A');
  await page.keyboard.type(text);
}

/** Holds matching search requests until released. */
function gate() {
  let release;
  const opened = new Promise(r => (release = r));
  return { opened, release };
}

async function shot(page, name, viewport, { needsResults = false } = {}) {
  const label = `${name} @ ${viewport.width}px`;
  const report = await page.evaluate(({ needsResults }) => {
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const inFirstScreen = el => {
      if (!el) return false;
      const r = el.getBoundingClientRect();
      return r.width > 0 && r.height > 0 && r.top >= 0 && r.bottom <= vh && r.left >= 0 && r.right <= vw;
    };
    const problems = [];
    const scrollWidth = Math.max(document.documentElement.scrollWidth, document.body.scrollWidth);
    if (scrollWidth > vw) problems.push(`horizontal overflow: content is ${scrollWidth}px wide in a ${vw}px viewport`);
    const notice = document.querySelector('[data-notice]');
    if (!inFirstScreen(notice) || !/Não é aconselhamento fiscal/.test(notice.textContent)) problems.push('the not-tax-advice notice is not visible in the first screen');
    if (document.body.innerText.includes(String.fromCharCode(0x2014))) problems.push('visible text contains an em-dash');
    if (needsResults && ![...document.querySelectorAll('[data-result] mark')].some(inFirstScreen)) problems.push('no highlighted passage text in the first screen');
    return problems;
  }, { needsResults });
  check(label, report.length === 0, report.length ? report.join('; ') : 'no horizontal overflow, notice visible, no em-dash');
  const out = join(evidenceDir, `${name}-${viewport.width}.png`);
  if (existsSync(out)) unlinkSync(out);
  await page.screenshot({ path: out });
  check(label, existsSync(out) && statSync(out).size > 0, `saved ${relative(root, out)}`);
}

// ---- Scenarios ----

async function initialAndValidation(browser, viewport) {
  const label = `initial/validation @ ${viewport.width}px`;
  const { context, page } = await openPage(browser, viewport, label);
  await page.goto(BASE + '/');
  await waitForFacets(page);
  check(label, (await page.locator('.examples a').count()) === 3, 'initial state shows three example questions');
  await shot(page, 'initial', viewport);

  check(label, await tabTo(page, '#q'), 'the question box is reachable with Tab');
  await page.keyboard.press('Enter');
  await page.waitForSelector('#q-error');
  const input = page.locator('#q');
  check(label, (await input.getAttribute('aria-invalid')) === 'true' && (await input.getAttribute('aria-describedby')) === 'q-error', 'blank query marks the input invalid and describes the error');
  check(label, (await page.locator('#q-error').textContent()).trim() === VALIDATION, 'blank query shows the validation message');
  check(label, (await activeId(page)) === 'q', 'focus stays on the question box after a blank submit');
  check(label, (await liveText(page)) === VALIDATION, 'the validation message is announced');
  check(label, (await page.locator('[data-summary]').count()) === 0, 'a blank query does not start a search');
  await shot(page, 'validation', viewport);
  await context.close();
}

async function searchAndFilters(browser, viewport, data) {
  const label = `search/filters @ ${viewport.width}px`;
  const mobile = viewport.width < 720;
  const { context, page } = await openPage(browser, viewport, label);
  await page.goto(BASE + '/');
  await waitForFacets(page);

  // Keyboard-only search, with the response held to capture the loading state.
  const hold = gate();
  await page.route('**/api/search?**', async route => {
    await hold.opened;
    await route.continue();
  });
  check(label, await tabTo(page, '#q'), 'Tab reaches the question box');
  await page.keyboard.type(QUERY);
  await clearAnnouncements(page);
  await page.keyboard.press('Enter');
  await waitForState(page, 'loading');
  check(label, (await page.locator('[data-region]').getAttribute('aria-busy')) === 'true', 'loading sets aria-busy on the results region');
  check(label, (await page.locator('[data-summary]').textContent()).trim() === 'A pesquisar…', 'loading summary reads "A pesquisar…"');
  check(label, (await page.locator('[data-result]').count()) === 0, 'no results are shown while loading');
  check(label, (await activeId(page)) === 'q', 'focus stays on the question box after Enter');
  await shot(page, 'loading', viewport);
  hold.release();
  await waitForState(page, 'results');
  await page.unroute('**/api/search?**');

  const count = await page.locator('[data-result]').count();
  check(label, count === data.first.results.length, `results list shows ${count} passages`);
  check(label, (await topRulingId(page)) === data.first.results[0].rulingId, 'results are ranked as the API returns them');
  check(label, (await activeId(page)) === 'q', 'focus stays on the question box when results arrive');
  check(label, JSON.stringify(await announcements(page)) === JSON.stringify([`${count} informações vinculativas encontradas.`]), 'the result count is announced once');
  check(label, new URL(page.url()).searchParams.get('q') === QUERY, 'the URL holds the submitted question');
  const links = await page.locator('[data-result] .actions a').evaluateAll(as => as.map(a => ({ href: a.href, target: a.target, rel: a.rel })));
  check(label, links.length === count && links.every(l => l.href.startsWith('https://') && l.target === '_blank' && l.rel === 'noopener noreferrer'), 'each passage links to its official PDF in a new tab with rel noopener noreferrer');
  await shot(page, 'results', viewport, { needsResults: true });

  // Keyboard-only filters: the rail selects on desktop, the sheet (modal dialog) on mobile.
  const { tax, article, year } = data.filter;
  await clearAnnouncements(page);
  if (!mobile) {
    check(label, await articleDisabled(page, 'd'), 'the article select is disabled with its hint until a tax is chosen');
    check(label, await tabTo(page, '#d-tax', { backwards: true }), 'Shift+Tab reaches the tax filter (past the disabled article select)');
    check(label, await searchSettled(page, { tax }, () => chooseWithKeyboard(page, tax, taxLabel(tax))), 'the tax is chosen with the keyboard');
    check(label, (await activeId(page)) === 'd-tax', 'focus stays on the tax select after the filter change');
    check(label, await tabTo(page, '#d-article'), 'Tab reaches the article filter once a tax is chosen');
    check(label, await searchSettled(page, { tax, article }, () => chooseWithKeyboard(page, article, articleLabel(article))), 'the article is chosen with the keyboard');
    check(label, (await activeId(page)) === 'd-article', 'focus stays on the article select after the filter change');
    check(label, await tabTo(page, '#d-year'), 'Tab reaches the year filter');
    check(label, await searchSettled(page, { tax, article, year }, () => chooseWithKeyboard(page, year, year)), 'the year is chosen with the keyboard');
    check(label, (await activeId(page)) === 'd-year', 'focus stays on the year select after the filter change');
  } else {
    check(label, await tabTo(page, '.filters-toggle'), 'Tab reaches the Filtros button');
    await page.keyboard.press('Enter');
    await page.waitForSelector('dialog.sheet[open]');
    check(label, (await activeId(page)) === 'm-tax', 'opening the sheet focuses its first select');
    await page.keyboard.press('Escape');
    await page.waitForSelector('dialog.sheet:not([open])', { state: 'attached' });
    check(label, await activeMatches(page, '.filters-toggle'), 'Escape closes the sheet and returns focus to Filtros');

    await page.keyboard.press('Enter');
    await page.waitForSelector('dialog.sheet[open]');
    check(label, await articleDisabled(page, 'm'), 'the article select in the sheet is disabled with its hint until a tax is chosen');
    check(label, await chooseWithKeyboard(page, tax, taxLabel(tax)), 'the tax is chosen with the keyboard in the sheet');
    check(label, await tabTo(page, '#m-article'), 'Tab reaches the article select in the sheet');
    check(label, await chooseWithKeyboard(page, article, articleLabel(article)), 'the article is chosen with the keyboard');
    check(label, await tabTo(page, '#m-year'), 'Tab reaches the year select in the sheet');
    check(label, await chooseWithKeyboard(page, year, year), 'the year is chosen with the keyboard');
    check(label, (await topRulingId(page)) === data.first.results[0].rulingId && new URL(page.url()).searchParams.get('article') === null, 'the sheet does not search before Aplicar filtros');
    await shot(page, 'filters-sheet', viewport);
    check(label, await tabTo(page, 'dialog.sheet .btn'), 'Tab reaches Aplicar filtros');
    await searchSettled(page, { tax, article, year }, async () => {
      await page.keyboard.press('Enter');
      await page.waitForSelector('dialog.sheet:not([open])', { state: 'attached' });
    });
    check(label, await activeMatches(page, '.filters-toggle'), 'applying the sheet returns focus to Filtros');
    check(label, (await page.locator('.filters-toggle').textContent()).trim() === 'Filtros (3)', 'the Filtros button counts the active filters');
  }
  const url = new URL(page.url());
  check(
    label,
    url.searchParams.get('tax') === tax && url.searchParams.get('article') === article && url.searchParams.get('year') === year,
    `the URL holds the filters (tax ${tax}, article ${article}, year ${year})`,
  );
  check(label, (await topRulingId(page)) === data.filtered.results[0].rulingId, 'filtered results match the API for the same filters');
  const finalAnnouncements = await announcements(page);
  check(label, finalAnnouncements.at(-1) === `${data.filtered.results.length} informações vinculativas encontradas.`, 'the filtered result count is announced');
  await shot(page, 'filters', viewport, { needsResults: true });

  // Reload restores the same search from the URL.
  const before = await topRulingId(page);
  await page.reload();
  await waitForState(page, 'results');
  const prefix = mobile ? 'm' : 'd';
  check(label, (await page.locator('#q').inputValue()) === QUERY, 'reload restores the question');
  await waitForFacets(page);
  check(
    label,
    (await page.locator('#d-tax').inputValue()) === tax && (await page.locator('#d-article').inputValue()) === article && (await page.locator('#d-year').inputValue()) === year,
    'reload restores the filters',
  );
  check(label, (await topRulingId(page)) === before, 'reload shows the same results');
  if (mobile) {
    const summary = (await page.locator('.active-filters').textContent()).trim();
    check(label, summary === `${taxLabel(tax)} · ${articlePhrase(article)} · ${year}`, `reload restores the mobile filter summary (${prefix}: ${summary})`);
  }
  await context.close();
}

async function emptyAndRecovery(browser, viewport, data) {
  const label = `empty @ ${viewport.width}px`;
  const { context, page } = await openPage(browser, viewport, label);
  await page.goto(pageUrl({ q: QUERY, ...data.empty }));
  await waitForState(page, 'empty');
  check(label, (await liveText(page)) === EMPTY_ANNOUNCEMENT, 'the empty result is announced');
  check(label, (await page.locator('[data-result]').count()) === 0, 'no passages are shown for an empty result');
  await waitForFacets(page);
  await shot(page, 'empty', viewport);

  await clearAnnouncements(page);
  await page.locator('[data-empty] button').focus();
  await page.keyboard.press('Enter');
  await waitForState(page, 'results');
  check(label, (await activeId(page)) === 'q', 'after "Limpar filtros" in the empty state focus moves to the question box');
  const url = new URL(page.url());
  check(
    label,
    ['tax', 'article', 'year'].every(name => url.searchParams.get(name) === null) && url.searchParams.get('q') === QUERY,
    'clearing the filters keeps the question and updates the URL',
  );
  check(label, await articleDisabled(page, 'd'), 'clearing the filters disables the article select again');
  check(label, JSON.stringify(await announcements(page)) === JSON.stringify([`${data.first.results.length} informações vinculativas encontradas.`]), 'the refreshed result is announced once');
  await context.close();
}

async function errorAndRetry(browser, viewport, data) {
  const label = `error @ ${viewport.width}px`;
  const { context, page } = await openPage(browser, viewport, label);
  await page.goto(BASE + '/');
  await waitForFacets(page);

  const seen = [];
  let failing = true;
  await page.route('**/api/search?**', async route => {
    seen.push(new URL(route.request().url()).searchParams.get('q'));
    if (!failing) return route.continue();
    await new Promise(r => setTimeout(r, 300));
    await route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ status: 500, title: 'An unexpected error occurred.' }) });
  });
  await page.locator('#q').focus();
  await page.keyboard.type(QUERY);
  await page.keyboard.press('Enter');
  await waitForState(page, 'error');
  check(label, (await liveText(page)) === ERROR_ANNOUNCEMENT, 'the error is announced');
  check(label, (await page.locator('[data-error]').getAttribute('role')) === null, 'the error block is not role=alert (announced once)');
  check(label, (await page.locator('[data-result]').count()) === 0, 'no results are shown with the error');
  await shot(page, 'error', viewport);

  // An unsubmitted edit must not be used by retry.
  await page.keyboard.type(' (editado)');
  const retry = page.locator('[data-error] button');
  await retry.focus();
  await clearAnnouncements(page);
  await page.keyboard.press('Enter');
  await page.waitForFunction(() => document.querySelector('[data-error] button')?.textContent.includes('A tentar de novo'));
  check(label, await activeMatches(page, '[data-error] button'), 'focus stays on the retry button while retrying');
  await page.waitForFunction(() => document.querySelector('[data-error] button')?.textContent.trim() === 'Tentar de novo');
  check(label, await activeMatches(page, '[data-error] button'), 'focus stays on "Tentar de novo" after a failed retry');
  check(label, JSON.stringify(await announcements(page)) === JSON.stringify([ERROR_ANNOUNCEMENT]), 'a failed retry is announced once');

  failing = false;
  await clearAnnouncements(page);
  await page.keyboard.press('Enter');
  await waitForState(page, 'results');
  check(label, seen.length === 3 && seen.every(q => q === QUERY), 'retry re-runs the last submitted question, not the unsubmitted edit');
  check(label, (await page.locator('#q').inputValue()) === QUERY + ' (editado)', 'the unsubmitted edit stays in the question box');
  check(label, (await topRulingId(page)) === data.first.results[0].rulingId, 'retry after an error recovers the results');
  check(label, await activeMatches(page, '[data-summary]'), 'after a successful retry focus moves to the results summary, not the page');
  check(label, JSON.stringify(await announcements(page)) === JSON.stringify([`${data.first.results.length} informações vinculativas encontradas.`]), 'the recovered result is announced once');
  await context.close();
}

async function requestOwnership(browser, viewport, data) {
  const label = `ownership @ ${viewport.width}px`;
  const { context, page } = await openPage(browser, viewport, label);
  await page.goto(BASE + '/');
  await waitForFacets(page);

  for (const outcome of ['success', 'failure']) {
    let slowStarted = false;
    await page.route('**/api/search?**', async route => {
      const q = new URL(route.request().url()).searchParams.get('q');
      if (q !== QUERY) return route.continue();
      slowStarted = true;
      await new Promise(r => setTimeout(r, 1500));
      try {
        if (outcome === 'success') await route.fulfill({ response: await route.fetch() });
        else await route.fulfill({ status: 500, contentType: 'application/problem+json', body: '{"status":500}' });
      } catch {
        // The page aborted the superseded request.
      }
    });
    await page.locator('#q').focus();
    await replaceQuery(page, QUERY);
    await clearAnnouncements(page);
    await page.keyboard.press('Enter');
    await page.waitForFunction(() => document.querySelector('[data-region]')?.getAttribute('aria-busy') === 'true');
    await replaceQuery(page, OTHER_QUERY);
    await page.keyboard.press('Enter');
    await waitForState(page, 'results');
    await page.waitForTimeout(2200);
    check(label, slowStarted, `the earlier request (${outcome}) was sent and delayed`);
    check(label, (await topRulingId(page)) === data.other.results[0].rulingId && (await page.locator('[data-error]').count()) === 0, `a delayed earlier ${outcome} does not replace the later results`);
    check(label, JSON.stringify(await announcements(page)) === JSON.stringify([`${data.other.results.length} informações vinculativas encontradas.`]), `only the later search is announced (earlier ${outcome})`);
    check(label, new URL(page.url()).searchParams.get('q') === OTHER_QUERY, 'the URL holds the later question');
    await page.unroute('**/api/search?**');
  }

  // A tax change while loading supersedes the pending request, whether the earlier one later
  // succeeds or fails.
  for (const outcome of ['success', 'failure']) {
    let slowStarted = false;
    await page.route('**/api/search?**', async route => {
      const params = new URL(route.request().url()).searchParams;
      if (params.get('tax')) return route.continue();
      slowStarted = true;
      await new Promise(r => setTimeout(r, 1500));
      try {
        if (outcome === 'success') await route.fulfill({ response: await route.fetch() });
        else await route.fulfill({ status: 500, contentType: 'application/problem+json', body: '{"status":500}' });
      } catch {
        // The page aborted the superseded request.
      }
    });
    await page.locator('#d-tax').selectOption('');
    await page.locator('#q').focus();
    await replaceQuery(page, QUERY);
    await clearAnnouncements(page);
    await page.keyboard.press('Enter');
    await page.waitForFunction(() => document.querySelector('[data-region]')?.getAttribute('aria-busy') === 'true');
    await page.locator('#d-tax').selectOption(data.filter.tax);
    await waitForState(page, 'results');
    await page.waitForTimeout(2000);
    check(label, slowStarted, `the unfiltered request (${outcome}) was sent and delayed`);
    check(
      label,
      (await topRulingId(page)) === data.taxOnly.results[0].rulingId && (await page.locator('[data-error]').count()) === 0,
      `a tax change during loading supersedes the pending request (earlier ${outcome})`,
    );
    const taxes = await page.locator('[data-result]').evaluateAll(items => items.map(item => item.getAttribute('data-tax')));
    check(label, taxes.length > 0 && taxes.every(t => t === data.filter.tax), `every shown result belongs to the chosen tax ${data.filter.tax}`);
    check(label, JSON.stringify(await announcements(page)) === JSON.stringify([`${data.taxOnly.results.length} informações vinculativas encontradas.`]), `only the filtered search is announced (earlier ${outcome})`);
    check(label, new URL(page.url()).searchParams.get('tax') === data.filter.tax, 'the URL holds the later tax');
    await page.unroute('**/api/search?**');
  }
  await context.close();
}

async function shownResults(page) {
  return page.locator('[data-result]').evaluateAll(items =>
    items.map(item => ({
      id: item.getAttribute('data-ruling-id'),
      tax: item.getAttribute('data-tax'),
      cite: [...item.querySelectorAll('.cite dd')].map(dd => dd.textContent.trim()),
    })),
  );
}

async function multiTax(browser, viewport, data) {
  const label = `taxes @ ${viewport.width}px`;
  const mobile = viewport.width < 720;
  const { tax, article } = data.taxFilter;
  const { context, page } = await openPage(browser, viewport, label);
  await page.goto(BASE + '/');
  await waitForFacets(page);

  const taxOptions = await page.locator('#d-tax option').evaluateAll(os => os.map(o => `${o.value}=${o.textContent.trim()}`));
  const expectedTaxes = data.facets.taxes.map(t => `${t.value}=${taxLabel(t.value)} (${t.count})`);
  check(label, taxOptions[0] === '=Todos' && sameSet(taxOptions.slice(1), expectedTaxes), `the tax select lists every indexed tax (${expectedTaxes.length}) with its label and count`);

  // A keyboard search answered from several taxes.
  check(label, await tabTo(page, '#q'), 'Tab reaches the question box');
  await page.keyboard.type(MULTI_QUERY);
  await clearAnnouncements(page);
  await page.keyboard.press('Enter');
  await waitForState(page, 'results');
  const shown = await shownResults(page);
  const taxes = [...new Set(shown.map(s => s.tax))];
  check(label, taxes.length >= 3, `results come from ${taxes.length} taxes (${taxes.join(', ')})`);
  check(label, shown.length === data.multi.results.length && shown.every((s, i) => s.id === data.multi.results[i].rulingId && s.tax === data.multi.results[i].tax), 'results and their taxes match the API');
  check(
    label,
    shown.every((s, i) => s.cite[0] === taxLabel(s.tax) && (!data.multi.results[i].article || s.cite[1] === articleCitation(data.multi.results[i].article))),
    'every result shows its tax and article in the citation',
  );
  check(label, JSON.stringify(await announcements(page)) === JSON.stringify([`${shown.length} informações vinculativas encontradas.`]), 'the result count is announced once');
  await shot(page, 'taxes', viewport, { needsResults: true });

  // The first results up to the third distinct tax, captured as one image (below the first screen on a phone).
  const third = shown.findIndex(s => s.tax === taxes[2]);
  const box = await page.evaluate(last => {
    const items = [...document.querySelectorAll('[data-result]')].slice(0, last + 1).map(item => item.getBoundingClientRect());
    const top = items[0].top + window.scrollY;
    return { x: 0, y: Math.max(0, top - 8), width: document.documentElement.clientWidth, height: items.at(-1).bottom + window.scrollY - top + 16 };
  }, third);
  const listOut = join(evidenceDir, `taxes-results-${viewport.width}.png`);
  if (existsSync(listOut)) unlinkSync(listOut);
  await page.screenshot({ path: listOut, clip: box, fullPage: true });
  check(label, existsSync(listOut) && statSync(listOut).size > 0, `saved ${relative(root, listOut)} (first ${third + 1} results, ${taxes.slice(0, 3).join(', ')})`);

  await clearAnnouncements(page);
  if (!mobile) {
    check(label, await articleDisabled(page, 'd'), 'the article select is disabled with its hint until a tax is chosen');
    check(label, await tabTo(page, '#d-tax', { backwards: true }), 'Shift+Tab reaches the tax filter');
    check(label, await searchSettled(page, { tax }, () => chooseWithKeyboard(page, tax, taxLabel(tax))), `the tax ${tax} is chosen with the keyboard`);
    check(label, (await activeId(page)) === 'd-tax', 'focus stays on the tax select after the filter change');
    const scoped = await articleOptions(page, 'd');
    check(label, sameSet(scoped, articlesOf(data.facets, tax)) && scoped.length < data.facets.articles.length, `the article select lists only the ${scoped?.length} articles of ${tax}`);
    check(label, await tabTo(page, '#d-article'), 'Tab reaches the article select');
    check(label, await searchSettled(page, { tax, article }, () => chooseWithKeyboard(page, article, articleLabel(article))), `the article ${article} is chosen with the keyboard`);
    check(label, (await activeId(page)) === 'd-article', 'focus stays on the article select after the filter change');
  } else {
    check(label, await tabTo(page, '.filters-toggle'), 'Tab reaches the Filtros button');
    await page.keyboard.press('Enter');
    await page.waitForSelector('dialog.sheet[open]');
    check(label, (await activeId(page)) === 'm-tax', 'opening the sheet focuses the tax select');
    check(label, await articleDisabled(page, 'm'), 'the article select in the sheet is disabled with its hint until a tax is chosen');
    check(label, await chooseWithKeyboard(page, tax, taxLabel(tax)), `the tax ${tax} is chosen with the keyboard in the sheet`);
    const scoped = await articleOptions(page, 'm');
    check(label, sameSet(scoped, articlesOf(data.facets, tax)) && scoped.length < data.facets.articles.length, `the sheet's article select lists only the ${scoped?.length} articles of ${tax}`);
    check(label, await tabTo(page, '#m-article'), 'Tab reaches the article select in the sheet');
    check(label, await chooseWithKeyboard(page, article, articleLabel(article)), `the article ${article} is chosen with the keyboard`);
    check(label, new URL(page.url()).searchParams.get('tax') === null, 'the sheet does not search before Aplicar filtros');
    await shot(page, 'tax-filter-sheet', viewport);

    // Clearing the tax in the sheet clears and disables the article; then choose both again.
    check(label, await tabTo(page, '#m-tax', { backwards: true }), 'Shift+Tab returns to the tax select in the sheet');
    check(label, await chooseWithKeyboard(page, '', 'Todos'), 'the tax is cleared with the keyboard');
    check(label, await articleDisabled(page, 'm'), 'clearing the tax clears and disables the article select in the sheet');
    check(label, await chooseWithKeyboard(page, tax, taxLabel(tax)), 'the tax is chosen again');
    check(label, await tabTo(page, '#m-article'), 'Tab reaches the article select again');
    check(label, await chooseWithKeyboard(page, article, articleLabel(article)), 'the article is chosen again');
    check(label, await tabTo(page, 'dialog.sheet .btn'), 'Tab reaches Aplicar filtros');
    await searchSettled(page, { tax, article }, async () => {
      await page.keyboard.press('Enter');
      await page.waitForSelector('dialog.sheet:not([open])', { state: 'attached' });
    });
    check(label, await activeMatches(page, '.filters-toggle'), 'applying the sheet returns focus to Filtros');
    const summary = (await page.locator('.active-filters').textContent()).trim();
    check(label, summary === `${taxLabel(tax)} · ${articlePhrase(article)}`, `the mobile filter summary names the tax and article (${summary})`);
  }
  const filtered = await shownResults(page);
  const url = new URL(page.url());
  check(label, url.searchParams.get('tax') === tax && url.searchParams.get('article') === article, `the URL holds the tax ${tax} and article ${article}`);
  check(label, filtered.length > 0 && filtered.every(s => s.tax === tax && s.cite[0] === taxLabel(tax)), `every result belongs to ${tax} and shows it`);
  check(label, filtered[0]?.id === data.multiTaxArticle.results[0].rulingId, 'filtered results match the API for the same tax and article');
  check(label, (await announcements(page)).at(-1) === `${data.multiTaxArticle.results.length} informações vinculativas encontradas.`, 'the filtered result count is announced');
  await shot(page, 'tax-filter', viewport, { needsResults: true });

  if (!mobile) {
    // Clearing the tax clears the article, searches again and disables the article select.
    await page.locator('#d-tax').focus();
    check(label, await searchSettled(page, { tax: '', article: '' }, () => chooseWithKeyboard(page, '', 'Todos')), 'the tax is cleared with the keyboard');
    const cleared = new URL(page.url());
    check(label, cleared.searchParams.get('tax') === null && cleared.searchParams.get('article') === null, 'clearing the tax removes the tax and article from the URL');
    check(label, await articleDisabled(page, 'd'), 'clearing the tax clears and disables the article select');
    check(label, (await topRulingId(page)) === data.multi.results[0].rulingId, 'clearing the tax shows the unfiltered results again');
  }

  // A shared link with an article but no tax drops the article without an error.
  await page.goto(pageUrl({ q: MULTI_QUERY, article }));
  await waitForState(page, 'results');
  await waitForFacets(page);
  check(label, (await page.locator('[data-error]').count()) === 0 && (await topRulingId(page)) === data.multi.results[0].rulingId, 'a URL with an article but no tax loads unfiltered results, no error');
  check(label, new URL(page.url()).searchParams.get('article') === null, 'the article without a tax is removed from the URL');
  check(label, await articleDisabled(page, 'd'), 'the article select stays disabled for a URL article without a tax');
  await context.close();
}

// ---- Main ----

preflight();
mkdirSync(evidenceDir, { recursive: true });
const api = await startApi();
let browser;
try {
  const data = await scenarioData();
  browser = await chromium.launch();
  for (const viewport of VIEWPORTS) {
    await initialAndValidation(browser, viewport);
    await searchAndFilters(browser, viewport, data);
    await multiTax(browser, viewport, data);
    await emptyAndRecovery(browser, viewport, data);
    await errorAndRetry(browser, viewport, data);
  }
  await requestOwnership(browser, VIEWPORTS[0], data);
} catch (error) {
  failures.push(`run aborted: ${error.stack ?? error}`);
} finally {
  await browser?.close();
  api.kill();
}

for (const p of passed) console.log(`ok   ${p}`);
if (failures.length) {
  console.error(`\n${failures.length} failed assertion(s):`);
  for (const f of failures) console.error(`FAIL ${f}`);
  process.exit(1);
}
console.log(`\nAll ${passed.length} assertions passed. Screenshots in ${relative(root, evidenceDir)}.`);
