// Drives the real UI against the real index and saves evidence screenshots. By default they go to
// web/test-results/evidence/ (gitignored), so a check run leaves the source tree untouched; pass
// --publish (npm run ui:evidence:publish) to write them to docs/evidence/ instead.
// Starts the built API (src/LupaFiscal.Api, Debug build) on http://localhost:4401 serving web/dist,
// then uses the Chromium already installed for Playwright (never downloads a browser) at 1440 and
// 390 px. Captures: initial, validation, loading, results, filters (plus the mobile filter sheet),
// empty and error. Asserts no horizontal overflow, keyboard-only search and filter use, URL restore,
// request ownership (a delayed earlier response, success or failure, never replaces a later one) and
// retry of the last submitted search. Exits non-zero on any failed assertion.
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
const articleLabel = value => {
  const [number, ...suffix] = value.split('-');
  return number + '.º' + (suffix.length ? '-' + suffix.join('-') : '');
};

/** Real data for the scenarios: a filter that keeps results and a filter combination with none. */
async function scenarioData() {
  const facets = await getJson('/api/facets');
  const first = await getJson(searchUrl({ q: QUERY }));
  const other = await getJson(searchUrl({ q: OTHER_QUERY }));
  if (!first.results.length || !other.results.length) throw new Error('The sample questions return no results; is the index built?');
  if (first.results[0].rulingId === other.results[0].rulingId) throw new Error('The two sample questions share their top ruling; pick different questions.');
  const top = first.results.find(r => r.article && r.date);
  const filter = { article: top.article, year: top.date.slice(0, 4) };
  const filtered = await getJson(searchUrl({ q: QUERY, ...filter }));
  const articleOnly = await getJson(searchUrl({ q: QUERY, article: filter.article }));

  // The least common article combined with years it does not appear in.
  let empty = null;
  const articles = [...facets.articles].sort((a, b) => a.count - b.count).slice(0, 10);
  outer: for (const article of articles) {
    for (const year of facets.years.slice(0, 5)) {
      const result = await getJson(searchUrl({ q: QUERY, article: article.value, year: String(year.value) }));
      if (!result.results.length) {
        empty = { article: article.value, year: String(year.value) };
        break outer;
      }
    }
  }
  if (!empty) throw new Error('No filter combination without results was found.');
  return { facets, first, other, filter, filtered, articleOnly, empty };
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
  await page.waitForFunction(() => document.querySelectorAll('#d-article option').length > 1, null, { timeout: 15_000 });
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
  const { article, year } = data.filter;
  await clearAnnouncements(page);
  if (!mobile) {
    check(label, await tabTo(page, '#d-article', { backwards: true }), 'Shift+Tab reaches the article filter');
    check(label, await chooseWithKeyboard(page, article, articleLabel(article)), 'the article is chosen with the keyboard');
    await waitForState(page, 'results');
    check(label, (await activeId(page)) === 'd-article', 'focus stays on the article select after the filter change');
    check(label, await tabTo(page, '#d-year'), 'Tab reaches the year filter');
    check(label, await chooseWithKeyboard(page, year, year), 'the year is chosen with the keyboard');
    await waitForState(page, 'results');
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
    check(label, await tabTo(page, '#m-article'), 'Tab reaches the article select in the sheet');
    check(label, await chooseWithKeyboard(page, article, articleLabel(article)), 'the article is chosen with the keyboard');
    check(label, await tabTo(page, '#m-year'), 'Tab reaches the year select in the sheet');
    check(label, await chooseWithKeyboard(page, year, year), 'the year is chosen with the keyboard');
    check(label, (await topRulingId(page)) === data.first.results[0].rulingId && new URL(page.url()).searchParams.get('article') === null, 'the sheet does not search before Aplicar filtros');
    await shot(page, 'filters-sheet', viewport);
    check(label, await tabTo(page, 'dialog.sheet .btn'), 'Tab reaches Aplicar filtros');
    await page.keyboard.press('Enter');
    await page.waitForSelector('dialog.sheet:not([open])', { state: 'attached' });
    await waitForState(page, 'results');
    check(label, await activeMatches(page, '.filters-toggle'), 'applying the sheet returns focus to Filtros');
    check(label, (await page.locator('.filters-toggle').textContent()).trim() === 'Filtros (2)', 'the Filtros button counts the active filters');
  }
  const url = new URL(page.url());
  check(label, url.searchParams.get('article') === article && url.searchParams.get('year') === year, `the URL holds the filters (article ${article}, year ${year})`);
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
  check(label, (await page.locator(`#d-article`).inputValue()) === article && (await page.locator(`#d-year`).inputValue()) === year, 'reload restores the filters');
  check(label, (await topRulingId(page)) === before, 'reload shows the same results');
  if (mobile) check(label, (await page.locator('.active-filters').textContent()).includes(`artigo ${articleLabel(article)}`), `reload restores the mobile filter summary (${prefix})`);
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
  check(label, url.searchParams.get('article') === null && url.searchParams.get('year') === null && url.searchParams.get('q') === QUERY, 'clearing the filters keeps the question and updates the URL');
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

  // A filter change while loading supersedes the pending request.
  await page.route('**/api/search?**', async route => {
    const params = new URL(route.request().url()).searchParams;
    if (params.get('article')) return route.continue();
    await new Promise(r => setTimeout(r, 1500));
    try {
      await route.fulfill({ response: await route.fetch() });
    } catch {
      // Aborted.
    }
  });
  await page.locator('#q').focus();
  await replaceQuery(page, QUERY);
  await clearAnnouncements(page);
  await page.keyboard.press('Enter');
  await page.waitForFunction(() => document.querySelector('[data-region]')?.getAttribute('aria-busy') === 'true');
  await page.locator('#d-article').selectOption(data.filter.article);
  await waitForState(page, 'results');
  await page.waitForTimeout(2000);
  check(label, (await topRulingId(page)) === data.articleOnly.results[0].rulingId, 'a filter change during loading supersedes the pending request');
  check(label, JSON.stringify(await announcements(page)) === JSON.stringify([`${data.articleOnly.results.length} informações vinculativas encontradas.`]), 'only the filtered search is announced');
  await page.unroute('**/api/search?**');
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
