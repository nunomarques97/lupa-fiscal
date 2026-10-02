// Captures the three design direction mocks in docs/ui/ at 1440 and 390 px with the Chromium
// that Playwright already installed (never downloads a browser). Exits non-zero when a capture is
// missing, a page overflows horizontally, the notice, query, filters or highlights are not visible
// in the first screen, the page throws, or it requests anything other than local files.
import { existsSync, statSync, unlinkSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { chromium } from '@playwright/test';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const uiDir = join(root, 'docs', 'ui');

const WIDTHS = [
  { width: 1440, height: 900 },
  { width: 390, height: 844 },
];

const DIRECTIONS = [
  { file: 'direction-a.html', name: 'direction-a-citation-register' },
  { file: 'direction-b.html', name: 'direction-b-accountant-console' },
  { file: 'direction-c.html', name: 'direction-c-question-evidence' },
];

// Extra states of the chosen direction (A), used by DESIGN.md. They need the notice but not results.
const STATES = [
  { file: 'direction-a.html', name: 'direction-a-citation-register', state: 'inicial' },
  { file: 'direction-a.html', name: 'direction-a-citation-register', state: 'carregar' },
  { file: 'direction-a.html', name: 'direction-a-citation-register', state: 'vazio' },
  { file: 'direction-a.html', name: 'direction-a-citation-register', state: 'erro' },
  { file: 'direction-a.html', name: 'direction-a-citation-register', state: 'filtros', widths: [390] },
];

const failures = [];
const fail = (label, message) => failures.push(`${label}: ${message}`);

const executable = chromium.executablePath();
if (!existsSync(executable)) {
  console.error(`Playwright Chromium not found at ${executable}.`);
  console.error('This script never downloads a browser; use the Chromium already installed for @playwright/test 1.62.1.');
  process.exit(1);
}

const browser = await chromium.launch();
const jobs = [
  ...DIRECTIONS.flatMap(d => WIDTHS.map(v => ({ ...d, viewport: v, state: null }))),
  ...STATES.flatMap(s => WIDTHS.filter(v => !s.widths || s.widths.includes(v.width)).map(v => ({ ...s, viewport: v }))),
];

try {
  for (const job of jobs) {
    const stateSuffix = job.state ? `-${job.state}` : '';
    const out = join(uiDir, `${job.name}${stateSuffix}-${job.viewport.width}.png`);
    const label = `${job.file}${job.state ? '#' + job.state : ''} @ ${job.viewport.width}px`;
    if (existsSync(out)) unlinkSync(out);

    const source = join(uiDir, job.file);
    if (!existsSync(source)) {
      fail(label, 'mock file is missing');
      continue;
    }

    const context = await browser.newContext({ viewport: job.viewport, deviceScaleFactor: 1, reducedMotion: 'reduce' });
    const page = await context.newPage();
    page.on('request', request => {
      const url = request.url();
      if (!url.startsWith('file:') && !url.startsWith('data:')) fail(label, `requested a non-local URL: ${url}`);
    });
    page.on('pageerror', error => fail(label, `page error: ${error.message}`));

    const url = pathToFileURL(source).href + (job.state ? `#${job.state}` : '');
    await page.goto(url, { waitUntil: 'load' });
    await page.evaluate(() => document.fonts.ready);

    const report = await page.evaluate(({ needsResults }) => {
      const vw = window.innerWidth, vh = window.innerHeight;
      const inFirstScreen = el => {
        if (!el) return false;
        const r = el.getBoundingClientRect();
        const style = getComputedStyle(el);
        return r.width > 0 && r.height > 0 && style.visibility !== 'hidden' && r.top >= 0 && r.bottom <= vh && r.left >= 0 && r.right <= vw;
      };
      const firstVisible = selector => [...document.querySelectorAll(selector)].find(inFirstScreen);

      const problems = [];
      const scrollWidth = Math.max(document.documentElement.scrollWidth, document.body.scrollWidth);
      if (scrollWidth > vw) problems.push(`horizontal overflow: content is ${scrollWidth}px wide in a ${vw}px viewport`);

      const notice = firstVisible('[data-notice]');
      if (!notice) problems.push('the not-tax-advice notice is not fully visible in the first screen');
      else if (!/aconselhamento fiscal/i.test(notice.textContent) || !/informação vinculativa oficial/i.test(notice.textContent)) {
        problems.push('the notice must say it is not tax advice and to read the official ruling');
      }

      const text = document.body.innerText;
      if (text.includes(String.fromCharCode(0x2014))) problems.push('visible text contains an em-dash');

      if (needsResults) {
        const query = firstVisible('input[type=search]');
        if (!query || !query.value.trim()) problems.push('the query box with the question is not visible in the first screen');
        if (!firstVisible('[data-filters]')) problems.push('the filters are not visible in the first screen');
        if (!firstVisible('[data-result]')) problems.push('no result is visible in the first screen');
        if (!firstVisible('[data-result] mark')) problems.push('no highlighted passage text is visible in the first screen');
      }
      return problems;
    }, { needsResults: !job.state });
    for (const problem of report) fail(label, problem);

    await page.screenshot({ path: out });
    await context.close();

    if (!existsSync(out) || statSync(out).size === 0) fail(label, `capture ${out} was not written`);
    else console.log(`captured ${job.viewport.width}px ${out.slice(root.length + 1)}`);
  }
} finally {
  await browser.close();
}

if (failures.length) {
  console.error(`\n${failures.length} problem(s):`);
  for (const f of failures) console.error(`- ${f}`);
  process.exit(1);
}
console.log(`\nAll ${jobs.length} captures written to docs/ui/ with no overflow and the notice in view.`);
