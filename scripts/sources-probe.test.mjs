// Offline tests for sources-probe.mjs: synthetic pages and listings, a fake fetch and a fake clock.
// No test makes a network request. Run: node --test scripts/sources-probe.test.mjs
import assert from 'node:assert/strict';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import {
  ALLOWED_HOST,
  LANDING_PATH,
  LiveClient,
  MAX_REQUESTS,
  ORIGIN,
  ProbeError,
  RULINGS_ROOT,
  UNFILTERED_CAML,
  USER_AGENT,
  UsageError,
  allowedUrl,
  buildListingUrl,
  buildSearchUrl,
  chooseView,
  crossLibraryDuplicates,
  decodeEntities,
  differencesFromCirs,
  discover,
  extraViewRank,
  extractPageVariables,
  fieldFill,
  isInside,
  isUnfilteredCaml,
  libraryWebPath,
  parseArgs,
  parseLanding,
  parseListing,
  parseRobots,
  pdfFileName,
  proposeCode,
  relationToChosen,
  renderSummary,
  resolveAllowed,
  robotsAllows,
  robotsRulesFor,
  stripTags,
  summarizeListing,
  unionFields,
} from './sources-probe.mjs';

// Any real network access from a test is a bug.
globalThis.fetch = () => {
  throw new Error('network access in an offline test');
};

const CIRS_WEB = `${RULINGS_ROOT}/rendimento/cirs`;
const CIMT_WEB = `${RULINGS_ROOT}/patrimonio/cimt`;
const LGT_WEB = `${RULINGS_ROOT}/Justica_Tributaria/LGT`;
const CIRS_FIELDS = 'DocIcon,NumeroVinculativa,Disponibilizada_x0020_em,Diploma,Artigo,Assunto';

const LANDING_HTML = `
<a href="http://info.portaldasfinancas.gov.pt/_layouts/15/Authenticate.aspx">Sign In</a>
<a href="${RULINGS_ROOT}/Pages/default.aspx">Informa&#231;&#245;es Vinculativas</a>
<div class="card-header"><a class="d-block collapsed accordion-toggle" href="#collapse0">Rendimento <i></i></a></div>
<ul class="submenu-itens"></ul>
<h4 id="h_ctl00__text">CIRS</h4>
<ul id="h_ctl00__sub" class="submenu-itens">
  <li><a href="${CIRS_WEB}/Pages/Vinc_numero.aspx" title="n">Visualização por Número</a></li>
  <li><a href="${CIRS_WEB}/Pages/legislacao-complementar.aspx">Legisla&#231;&#227;o Complementar</a></li>
</ul>
<div class="card-header"><a class="accordion-toggle" href="#collapse1">Património</a></div>
<h4 id="h_ctl01__text">CIMT</h4>
<ul id="h_ctl01__sub" class="submenu-itens">
  <li><a href='${CIMT_WEB}/Pages/Vinc_numero.aspx'>Visualização por Número </a></li>
  <li><a href="${CIMT_WEB}/Pages/legislacao-complementar.aspx">Legislação Complementar</a></li>
</ul>
<div class="card-header"><a class="accordion-toggle" href="#collapse2">Justiça Tributária</a></div>
<ul class="submenu-itens"><li><a href="${LGT_WEB}/Paginas/default.aspx">LGT</a></li></ul>
<a href="http://info.portaldasfinancas.gov.pt${RULINGS_ROOT}/rendimento/circ/Pages/Vinc_numero.aspx">plain http</a>
<a href="https://other.example${RULINGS_ROOT}/rendimento/circ/Pages/Vinc_numero.aspx">other host</a>
<a href="//other.example${RULINGS_ROOT}/x/Pages/a.aspx">protocol-relative</a>
<a href="${RULINGS_ROOT}/Documents/Pedido.pdf">form</a>
<a href="${RULINGS_ROOT}/_vti_bin/spsdisco.aspx">disco</a>
<a href="/pt/outra_area/Pages/default.aspx">elsewhere</a>
`;

function listPage({ web, fields = CIRS_FIELDS, sort = 'NumeroVinculativa:DESC', filter = UNFILTERED_CAML, itemId = '42', libraries = '', enableSearch = 0, extra = '' }) {
  const escapedWeb = web.replace(/\//g, '\\u002f');
  return `<html><script>var _spPageContextInfo = {webServerRelativeUrl: "${escapedWeb}", siteId: "x"};</script>
<script type="text/javascript">
var __fieldnames = '${fields}';
var __listsort = '${sort}';
var __filtervalue = '${filter}';
var __enableSearch = ${enableSearch};
var __filterstoshow = null;
var __groupBy = '';
var __libraries = '${libraries}';
var __itemid = '${itemId}';
var __aoColumnDefs = [{ 'sTitle': '<span style="white-space:nowrap;">Vinc. n.º<span>', 'aTargets': [1] }];
</script>${extra}</html>`;
}

function docIcon(path) {
  const name = path.split('/').at(-1);
  return `<a href='${path}'><img src='/_layouts/15/images/icpdf.png' alt='${name}' /></a>`;
}

// Synthetic library contents: [file, number, date, diploma, article, subject].
const CIRS_ROWS = [
  ['PIV_100.pdf', '100', '2026-09-23', 'CIRS', '010', 'Mais-valias'],
  ['PIV_101.pdf', '', '2006-08-24', 'Estatuto dos Benefícios Fiscais (EBF)', '021', 'Ficha'],
  ['shared.pdf', '102', '2010-01-01', 'CIRS', '078', 'Dedução'],
];
const CIMT_ROWS = [
  ['PIV_200.pdf', '200', '2020-02-02', 'CIMT', '009', 'Isenção HPP'],
  ['PIV_201.pdf', '201', '2021-03-03', 'CIMT', '011', 'Revenda'],
  ['shared.pdf', '202', '2019-04-04', 'Estatuto dos Benefícios Fiscais (EBF)', '071', 'Reabilitação'],
];
const LGT_ROWS = [['PIV_300.pdf', '300', '2021-04-20', 'LGT', '068', 'Prazo']];

function cellFor(field, row, web) {
  const [file, number, date, diploma, article, subject] = row;
  switch (field) {
    case 'DocIcon':
      return docIcon(`${web}/Documents/${file}`);
    case 'NumeroVinculativa':
      return number;
    case 'Disponibilizada_x0020_em':
      return `<span style='white-space: nowrap;'>${date}</span>`;
    case 'Diploma':
      return diploma;
    case 'Artigo':
      return article;
    case 'Assunto':
      return subject;
    default:
      return '';
  }
}

function listingJson(rows, fields, web) {
  return JSON.stringify({ data: rows.map((row) => fields.map((field) => cellFor(field, row, web))), total: 0 });
}

/** A fake portal: fetch(url, init) answering from synthetic pages; records every call. */
function fakePortal({ robots = { status: 404, body: 'not found' }, landing = { status: 200, body: LANDING_HTML } } = {}) {
  const calls = [];
  const pages = new Map([
    [`${CIRS_WEB}/Pages/Vinc_numero.aspx`, listPage({ web: CIRS_WEB, itemId: '42' })],
    [`${CIRS_WEB}/Pages/legislacao-complementar.aspx`, listPage({ web: CIRS_WEB, itemId: '39', fields: 'DocIcon,Artigo,Assunto', filter: '<Neq><FieldRef Name="Diploma" /><Value Type="Text">CIRS</Value></Neq>' })],
    [`${CIMT_WEB}/Pages/Vinc_numero.aspx`, listPage({ web: CIMT_WEB, itemId: '40', filter: '<Eq><FieldRef Name="Diploma" /><Value Type="Text">CIMT</Value></Eq>' })],
    [`${CIMT_WEB}/Pages/legislacao-complementar.aspx`, listPage({ web: CIMT_WEB, itemId: '36', fields: 'DocIcon,Artigo,Assunto', sort: 'Diploma:ASC,Artigo:ASC', filter: '<Gt><FieldRef Name="Diploma" /><Value Type="Text">CIMT</Value></Gt>' })],
    [`${LGT_WEB}/Paginas/default.aspx`, listPage({ web: LGT_WEB, itemId: '1', fields: 'DocIcon,Artigo,Assunto,Disponibilizada_x0020_em,NumeroVinculativa', sort: 'Diploma:ASC,Artigo:ASC' })],
  ]);
  const libraryRows = { [CIRS_WEB]: CIRS_ROWS, [CIMT_WEB]: CIMT_ROWS, [LGT_WEB]: LGT_ROWS };

  async function fetchImpl(url, init) {
    calls.push({ url, init });
    const u = new URL(url);
    const respond = (status, body, type, headers = {}) => new Response(status === 204 ? null : body, { status, headers: { 'content-type': type, ...headers } });
    if (u.pathname === '/robots.txt') return respond(robots.status, robots.body, 'text/plain');
    if (u.pathname === LANDING_PATH) return respond(landing.status, landing.body, 'text/html', landing.headers);
    if (pages.has(u.pathname)) return respond(200, pages.get(u.pathname), 'text/html');
    const listing = /^(.*)\/_vti_bin\/portalat\/docs\.svc\/listdocs$/.exec(u.pathname);
    if (listing && libraryRows[listing[1]]) {
      const web = listing[1];
      const fields = u.searchParams.get('fields').split(',');
      const filter = u.searchParams.get('filter');
      let rows = libraryRows[web];
      if (/^<Eq>/.test(filter)) rows = rows.filter((r) => filter.includes(`>${r[3]}<`));
      if (/^<(Neq|Gt)>/.test(filter)) rows = rows.filter((r) => !filter.includes(`>${r[3]}<`));
      return respond(200, listingJson(rows, fields, web), 'application/json; charset=utf-8');
    }
    if (/\/Documents\/[^/]+\.pdf$/.test(u.pathname)) return respond(200, '%PDF-1.5 rest of the document', 'application/pdf', { 'content-length': '29' });
    return respond(404, 'not found', 'text/html');
  }
  return { calls, fetchImpl };
}

function fakeClock() {
  let time = Date.parse('2026-10-02T12:00:00Z');
  const sleeps = [];
  return {
    sleeps,
    now: () => time,
    sleep: async (ms) => {
      sleeps.push(ms);
      time += ms;
    },
    advance: (ms) => {
      time += ms;
    },
  };
}

function client(portal, clock, options = {}) {
  const fetchImpl = async (url, init) => {
    clock.advance(25);
    return portal.fetchImpl(url, init);
  };
  return new LiveClient({ intervalMs: 1000, maxRequests: MAX_REQUESTS, ...options, fetchImpl, now: clock.now, sleep: clock.sleep });
}

// --- URL policy ---------------------------------------------------------------------------------

test('only https URLs on the official host with the default port and no credentials are allowed', () => {
  assert.ok(allowedUrl(`${ORIGIN}${LANDING_PATH}`));
  assert.equal(allowedUrl(`http://${ALLOWED_HOST}/`), null);
  assert.equal(allowedUrl('https://other.example/'), null);
  assert.equal(allowedUrl(`https://${ALLOWED_HOST}.other.example/`), null);
  assert.equal(allowedUrl(`https://${ALLOWED_HOST}:8443/`), null);
  assert.equal(allowedUrl(`https://user:pw@${ALLOWED_HOST}/`), null);
  assert.equal(allowedUrl('not a url'), null);
  assert.equal(resolveAllowed('/pt/x.pdf', `${ORIGIN}/pt/a/b.aspx`).href, `${ORIGIN}/pt/x.pdf`);
  assert.equal(resolveAllowed('//other.example/pt/x.pdf', ORIGIN), null);
  assert.equal(resolveAllowed('javascript:alert(1)', ORIGIN), null);
  assert.equal(resolveAllowed('', ORIGIN), null);
});

test('entities and tags are decoded and stripped', () => {
  assert.equal(decodeEntities('Informa&#231;&#245;es &amp; &#x41; &quot;x&quot; &unknown;'), 'Informações & A "x" &unknown;');
  assert.equal(stripTags("<span style='white-space: nowrap;'>2026-09-23</span>"), '2026-09-23');
  assert.equal(stripTags('  a <b>b</b>\n c '), 'a b c');
});

test('library web paths come only from list pages under the rulings root', () => {
  assert.equal(libraryWebPath(`${CIRS_WEB}/Pages/Vinc_numero.aspx`), CIRS_WEB);
  assert.equal(libraryWebPath(`${LGT_WEB}/Paginas/default.aspx`), LGT_WEB);
  assert.equal(libraryWebPath(`${RULINGS_ROOT}/Pages/default.aspx`), null);
  assert.equal(libraryWebPath(`${RULINGS_ROOT}/Documents/Pedido.pdf`), null);
  assert.equal(libraryWebPath('/pt/outra_area/Pages/default.aspx'), null);
  assert.equal(libraryWebPath(`${RULINGS_ROOT}/_vti_bin/x/Pages/a.aspx`), null);
});

// --- Landing and page parsing -------------------------------------------------------------------

test('the landing page yields every library with its category, name and views', () => {
  const libraries = parseLanding(LANDING_HTML, `${ORIGIN}${LANDING_PATH}`);
  assert.deepEqual(
    libraries.map((l) => [l.category, l.name, l.webPath, l.views.length]),
    [
      ['Rendimento', 'CIRS', CIRS_WEB, 2],
      ['Património', 'CIMT', CIMT_WEB, 2],
      ['Justiça Tributária', 'LGT', LGT_WEB, 1],
    ],
  );
  assert.deepEqual(libraries[0].views[1], { label: 'Legislação Complementar', path: `${CIRS_WEB}/Pages/legislacao-complementar.aspx` });
  assert.equal(libraries[1].views[0].label, 'Visualização por Número');
});

test('page variables are extracted, including the escaped web path and null values', () => {
  const vars = extractPageVariables(listPage({ web: CIMT_WEB, itemId: '40', filter: '<Eq><FieldRef Name="Diploma" /><Value Type="Text">CIMT</Value></Eq>' }));
  assert.deepEqual(vars, {
    fieldNames: CIRS_FIELDS.split(','),
    listSort: 'NumeroVinculativa:DESC',
    filterValue: '<Eq><FieldRef Name="Diploma" /><Value Type="Text">CIMT</Value></Eq>',
    enableSearch: 0,
    libraries: '',
    groupBy: '',
    itemId: '40',
    webServerRelativeUrl: CIMT_WEB,
  });
  const empty = extractPageVariables('<html><p>no list here</p></html>');
  assert.equal(empty.fieldNames, null);
  assert.equal(empty.itemId, null);
  assert.equal(empty.webServerRelativeUrl, null);
  assert.equal(extractPageVariables(listPage({ web: CIRS_WEB, enableSearch: 1 })).enableSearch, 1);
  assert.equal(extractPageVariables(`var __filtervalue = '&lt;IsNotNull&gt;&lt;/IsNotNull&gt;';`).filterValue, '<IsNotNull></IsNotNull>');
});

test('the listing URL matches the one listdocs.js builds and stays on the host', () => {
  const vars = extractPageVariables(listPage({ web: CIRS_WEB, itemId: '42' }));
  assert.equal(
    buildListingUrl(vars),
    `${ORIGIN}${CIRS_WEB}/_vti_bin/portalat/docs.svc/listdocs?fields=${CIRS_FIELDS}&sort=NumeroVinculativa:DESC` +
      `&filter=${encodeURIComponent(UNFILTERED_CAML)}&id=42`,
  );
  assert.match(buildListingUrl({ ...vars, libraries: 'Documentos Vinc,Outra' }), /&library=Documentos%20Vinc$/);
  assert.match(buildListingUrl({ ...vars, fieldNames: ['DocIcon', 'a&b=c'] }), /fields=DocIcon%2Ca%26b%3Dc&/);
  assert.throws(() => buildListingUrl({ ...vars, itemId: '42&x=1' }), ProbeError);
  assert.throws(() => buildListingUrl({ ...vars, webServerRelativeUrl: null }), ProbeError);
  assert.throws(() => buildListingUrl({ ...vars, webServerRelativeUrl: '@other.example/x' }), ProbeError);
  assert.equal(
    buildSearchUrl(vars, 1000, 2),
    `${ORIGIN}${CIRS_WEB}/_vti_bin/portalat/docs.svc/searchdocs?fields=${CIRS_FIELDS}&sort=1:desc&searchBy=*&libraries=&pageSize=1000&pageNumber=2`,
  );
});

test('the unfiltered CAML is recognised regardless of whitespace', () => {
  assert.ok(isUnfilteredCaml(UNFILTERED_CAML));
  assert.ok(isUnfilteredCaml('<IsNotNull> <FieldRef Name="ID"></FieldRef> </IsNotNull>'));
  assert.ok(!isUnfilteredCaml('<Eq><FieldRef Name="Diploma" /><Value Type="Text">CIMT</Value></Eq>'));
  assert.ok(!isUnfilteredCaml(null));
});

// --- Listing parsing ----------------------------------------------------------------------------

test('listing rows map to fields; off-host, http and non-PDF links are rejected', () => {
  const fields = CIRS_FIELDS.split(',');
  const json = JSON.parse(listingJson(CIRS_ROWS, fields, CIRS_WEB));
  json.data.push(['<a href="https://other.example/x.pdf">x</a>', '1', '2020-01-01', 'CIRS', '1', 'off host']);
  json.data.push(['<a href="http://info.portaldasfinancas.gov.pt/x.pdf">x</a>', '2', '2020-01-01', 'CIRS', '1', 'http']);
  json.data.push([`<a href="${CIRS_WEB}/Documents/nota.docx">x</a>`, '3', '2020-01-01', 'CIRS', '1', 'docx']);
  json.data.push(['', '4', '', 'CIRS', '1', 'no link']);
  const entries = parseListing(json, fields);
  assert.equal(entries.length, 7);
  assert.equal(entries[0].pdfUrl, `${ORIGIN}${CIRS_WEB}/Documents/PIV_100.pdf`);
  assert.deepEqual(entries[0].values, { DocIcon: '', NumeroVinculativa: '100', Disponibilizada_x0020_em: '2026-09-23', Diploma: 'CIRS', Artigo: '010', Assunto: 'Mais-valias' });
  assert.deepEqual(entries.slice(3).map((e) => [e.pdfUrl, e.rejected !== null]), [[null, true], [null, true], [null, true], [null, false]]);

  const summary = summarizeListing(entries, fields);
  assert.equal(summary.entries, 7);
  assert.equal(summary.withPdf, 3);
  assert.equal(summary.distinctPdfs, 3);
  assert.equal(summary.rejectedLinks, 3);
  assert.equal(summary.noLink, 1);
  assert.equal(summary.firstDate, '2006-08-24');
  assert.equal(summary.lastDate, '2026-09-23');
  assert.equal(summary.undatedEntries, 1);
  assert.equal(summary.emptyNumbers, 1);
  assert.equal(summary.diplomas.CIRS, 6);
  assert.deepEqual(summary.pdfFolders, { [`${CIRS_WEB}/Documents/`]: 3 });
  assert.deepEqual(fieldFill(entries, ['NumeroVinculativa', 'Disponibilizada_x0020_em']), { NumeroVinculativa: 6, Disponibilizada_x0020_em: 6 });
});

test('malformed listings are refused', () => {
  assert.throws(() => parseListing({ total: 0 }, ['DocIcon']), ProbeError);
  assert.throws(() => parseListing(null, ['DocIcon']), ProbeError);
  assert.throws(() => parseListing({ data: [['a', 'b']] }, ['DocIcon']), ProbeError);
  assert.throws(() => parseListing({ data: ['not a row'] }, ['DocIcon']), ProbeError);
});

test('file names are compared decoded and case-insensitively across libraries', () => {
  assert.equal(pdfFileName(`${ORIGIN}/a/Documents/Ficha%20Doutrin%C3%A1ria%20-%20Proc%201.PDF`), 'ficha doutrinária - proc 1.pdf');
  assert.equal(pdfFileName(`${ORIGIN}/a/Documents/bad%E0.pdf`), 'bad%e0.pdf');
  const dupes = crossLibraryDuplicates({
    CIRS: [`${ORIGIN}/cirs/Documents/Shared.pdf`, `${ORIGIN}/cirs/Documents/PIV_1.pdf`],
    EBF: [`${ORIGIN}/ebf/Documents/shared.pdf`],
    CIMT: [`${ORIGIN}/cimt/Documents/PIV_2.pdf`],
  });
  assert.deepEqual(dupes, [{ name: 'shared.pdf', libraries: ['CIRS', 'EBF'] }]);
});

// --- View choice, fields and comparisons --------------------------------------------------------

test('the numbered view is chosen, else the single page, else the date view', () => {
  const numbered = { label: 'Visualização por Número ', path: '/n' };
  const byDate = { label: 'Visualização por data de disponibilização', path: '/d' };
  const bySubject = { label: 'Visualização por Assunto', path: '/s' };
  assert.equal(chooseView([byDate, numbered]).view, numbered);
  assert.equal(chooseView([{ label: 'LGT', path: '/l' }]).reason, 'single list page');
  assert.equal(chooseView([bySubject, byDate]).view, byDate);
  assert.equal(chooseView([bySubject]).view, bySubject);
  assert.ok(extraViewRank({ label: 'Legislação Complementar' }) < extraViewRank({ label: 'Visualização por verbas das Listas I e II' }));
  assert.ok(extraViewRank({ label: 'Visualização por Artigo' }) < extraViewRank({ label: 'Visualização por Assunto' }));
});

test('the superset fields join displayed and sort fields of every view, once and only safe names', () => {
  const fields = unionFields([
    { fieldNames: ['DocIcon', 'NumeroVinculativa', 'Artigo'], listSort: 'NumeroVinculativa:DESC' },
    { fieldNames: ['DocIcon', 'N_x002e__x00ba__x002f_Al_x00ed_nea'], listSort: 'Artigo:ASC,Assunto_Resumo:ASC,' },
    { fieldNames: ['bad name', 'x&y'], listSort: null },
    null,
  ]);
  assert.deepEqual(fields, ['DocIcon', 'NumeroVinculativa', 'Artigo', 'N_x002e__x00ba__x002f_Al_x00ed_nea', 'Assunto_Resumo']);
});

test('views are related to the chosen view by web, library and endpoint', () => {
  const chosen = extractPageVariables(listPage({ web: CIRS_WEB, itemId: '42' }));
  assert.equal(relationToChosen(chosen, extractPageVariables(listPage({ web: CIRS_WEB, itemId: '39' }))), 'same list');
  assert.equal(relationToChosen(chosen, extractPageVariables(listPage({ web: CIRS_WEB, itemId: '39', libraries: 'Outra' }))), 'different list');
  assert.equal(relationToChosen(chosen, extractPageVariables(listPage({ web: CIMT_WEB, itemId: '39' }))), 'different list');
  assert.equal(relationToChosen(chosen, extractPageVariables('<p>none</p>')), 'no listing on page');
});

test('codes and differences from the CIRS listing are derived from the page', () => {
  assert.equal(proposeCode('CIRS', CIRS_WEB), 'CIRS');
  assert.equal(proposeCode('Estatuto dos Benefícios Fiscais', `${RULINGS_ROOT}/beneficios_fiscais`), 'EBF');
  assert.equal(proposeCode('Relações Internacionais', `${RULINGS_ROOT}/rendimento/DSRI`), 'DSRI');
  const lgt = {
    chosen: { path: `${LGT_WEB}/Paginas/default.aspx` },
    vars: extractPageVariables(listPage({ web: LGT_WEB, itemId: '1', fields: 'DocIcon,Artigo,Assunto,Disponibilizada_x0020_em,NumeroVinculativa', sort: 'Diploma:ASC' })),
  };
  assert.deepEqual(differencesFromCirs(lgt), ['pages folder "Paginas"', 'page "default.aspx"', 'no Diploma', 'sort Diploma:ASC']);
  const cirs = { chosen: { path: `${CIRS_WEB}/Pages/Vinc_numero.aspx` }, vars: extractPageVariables(listPage({ web: CIRS_WEB })) };
  assert.deepEqual(differencesFromCirs(cirs), []);
});

// --- Options, scratch and robots ----------------------------------------------------------------

test('options enforce the interval floor and the request budget ceiling', () => {
  assert.deepEqual(parseArgs([]), { scratch: null, intervalMs: 1500, maxRequests: 60, replay: null, samples: true });
  assert.equal(parseArgs(['--interval-ms', '1000', '--max-requests', '5', '--skip-samples']).samples, false);
  assert.throws(() => parseArgs(['--interval-ms', '999']), UsageError);
  assert.throws(() => parseArgs(['--max-requests', '61']), UsageError);
  assert.throws(() => parseArgs(['--max-requests', '0']), UsageError);
  assert.throws(() => parseArgs(['--scratch']), UsageError);
  assert.throws(() => parseArgs(['--follow-redirects']), UsageError);
  assert.throws(() => new LiveClient({ intervalMs: 500 }), ProbeError);
  assert.throws(() => new LiveClient({ maxRequests: 61 }), ProbeError);
});

test('a scratch directory inside the repository is detected', () => {
  const root = join(tmpdir(), 'repo');
  assert.ok(isInside(root, root));
  assert.ok(isInside(join(root, 'data', 'probe'), root));
  assert.ok(!isInside(join(tmpdir(), 'repo-probe'), root));
  assert.ok(!isInside(tmpdir(), root));
});

test('robots.txt rules: own group wins, longest match wins, Allow wins ties', () => {
  const rules = robotsRulesFor(parseRobots('User-agent: *\nDisallow: /\n\nUser-agent: LupaFiscal\nDisallow: /pt/\nAllow: /pt/informacao_fiscal/\n'));
  assert.ok(robotsAllows(rules, `${RULINGS_ROOT}/Pages/default.aspx`));
  assert.ok(!robotsAllows(rules, '/pt/outra/x.aspx'));
  assert.ok(robotsAllows(rules, '/en/x'));
  const star = robotsRulesFor(parseRobots('User-agent: *\nDisallow: /*.pdf$\n'));
  assert.ok(!robotsAllows(star, '/a/b.pdf'));
  assert.ok(robotsAllows(star, '/a/b.pdf.html'));
});

// --- Polite client ------------------------------------------------------------------------------

test('the client sends the User-Agent, never follows redirects, spaces requests and keeps the budget', async () => {
  const portal = fakePortal();
  const clock = fakeClock();
  const http = client(portal, clock, { maxRequests: 3 });
  await http.get(`${ORIGIN}/robots.txt`, { accept: 'text/plain', purpose: 't' });
  await http.get(`${ORIGIN}${LANDING_PATH}`, { accept: 'text/html', purpose: 't' });
  await http.get(`${ORIGIN}${LANDING_PATH}`, { accept: 'text/html', purpose: 't' });
  await assert.rejects(http.get(`${ORIGIN}${LANDING_PATH}`, { accept: 'text/html', purpose: 't' }), /budget of 3 exhausted/);
  assert.equal(portal.calls.length, 3);
  for (const call of portal.calls) {
    assert.equal(call.init.headers['User-Agent'], USER_AGENT);
    assert.match(USER_AGENT, /^LupaFiscal\/0\.2 /);
    assert.equal(call.init.redirect, 'manual');
  }
  assert.deepEqual(clock.sleeps, [1000, 1000]);
  await assert.rejects(http.get('https://other.example/', { accept: 'text/html', purpose: 't' }), ProbeError);
  assert.equal(portal.calls.length, 3);
});

test('the client refuses URLs disallowed by robots.txt before fetching them', async () => {
  const portal = fakePortal();
  const http = client(portal, fakeClock());
  http.rules = [{ allow: false, path: '/pt/' }];
  await assert.rejects(http.get(`${ORIGIN}${LANDING_PATH}`, { accept: 'text/html', purpose: 't' }), /robots\.txt disallows/);
  assert.equal(portal.calls.length, 0);
});

test('a PDF sample reads only the first bytes', async () => {
  const http = client(fakePortal(), fakeClock());
  const response = await http.get(`${ORIGIN}${CIRS_WEB}/Documents/PIV_100.pdf`, { accept: 'application/pdf', sampleBytes: 5, purpose: 't' });
  assert.equal(response.body.toString('latin1'), '%PDF-');
  assert.equal(http.log[0].bytes, 5);
});

// --- Discovery end to end (fake portal) ---------------------------------------------------------

test('discovery lists every library, uses unfiltered supersets and stays polite', async () => {
  const portal = fakePortal();
  const clock = fakeClock();
  const http = client(portal, clock);
  const result = await discover(http);

  assert.deepEqual(result.libraries.map((l) => [l.code, l.supported]), [['CIRS', true], ['CIMT', true], ['LGT', true]]);
  const [cirs, cimt, lgt] = result.libraries;
  assert.equal(cirs.summary.entries, 3);
  assert.equal(cirs.listingVars.filterValue, UNFILTERED_CAML);
  assert.deepEqual(cirs.listingVars.fieldNames, [...CIRS_FIELDS.split(',')]);
  // CIMT's numbered page is filtered to Diploma = CIMT; the superset adds the complementary entry.
  assert.equal(cimt.summary.entries, 3);
  assert.deepEqual(cimt.browserListing, { entries: 2, distinctPdfs: 2, notInSuperset: 0, notInSupersetSamples: [] });
  const cimtComplement = cimt.extraViews.find((v) => /legislacao-complementar/.test(v.path));
  assert.equal(cimtComplement.relation, 'same list');
  assert.equal(cimtComplement.listing.notInSuperset, 0);
  assert.deepEqual(lgt.listingVars.fieldNames, ['DocIcon', 'Artigo', 'Assunto', 'Disponibilizada_x0020_em', 'NumeroVinculativa', 'Diploma']);
  assert.deepEqual(result.duplicates, [{ name: 'shared.pdf', libraries: ['CIMT', 'CIRS'] }]);
  for (const library of result.libraries) assert.equal(library.sample.magic, '%PDF-');

  const urls = portal.calls.map((c) => new URL(c.url));
  assert.equal(urls[0].pathname, '/robots.txt');
  assert.equal(urls[1].pathname, LANDING_PATH);
  assert.ok(urls.every((u) => u.protocol === 'https:' && u.hostname === ALLOWED_HOST));
  assert.ok(portal.calls.length <= MAX_REQUESTS);
  assert.equal(urls.filter((u) => u.pathname.endsWith('.pdf')).length, 3, 'one PDF sample per library');
  assert.ok(clock.sleeps.every((ms) => ms >= 1000 - 25));
  assert.equal(result.requests.length, portal.calls.length);
  assert.match(renderSummary(result), /Total requests: \d+\./);
});

test('discovery without samples requests no PDF at all', async () => {
  const portal = fakePortal();
  await discover(client(portal, fakeClock()), { samples: false });
  assert.equal(portal.calls.filter((c) => c.url.endsWith('.pdf')).length, 0);
});

test('discovery keeps within a small budget and reports what it could not reach', async () => {
  const portal = fakePortal();
  const result = await discover(client(portal, fakeClock(), { maxRequests: 5 }));
  assert.equal(portal.calls.length, 5);
  assert.ok(result.libraries.some((l) => !l.supported && /budget/.test(l.unsupportedReason)));
});

test('discovery stops when robots.txt is unreachable or disallows the landing page', async () => {
  for (const robots of [
    { status: 503, body: 'busy' },
    { status: 200, body: 'User-agent: *\nDisallow: /pt/informacao_fiscal/\n' },
  ]) {
    const portal = fakePortal({ robots });
    await assert.rejects(discover(client(portal, fakeClock())), ProbeError);
    assert.equal(portal.calls.length, 1, 'only robots.txt was requested');
  }
});

test('discovery stops on a redirect off the host and on a landing page without libraries', async () => {
  const offHost = fakePortal({ landing: { status: 302, body: '', headers: { location: 'https://other.example/landing' } } });
  await assert.rejects(discover(client(offHost, fakeClock())), /leaves the allowlist/);
  assert.ok(offHost.calls.every((c) => new URL(c.url).hostname === ALLOWED_HOST));

  const empty = fakePortal({ landing: { status: 200, body: '<html><p>Página em manutenção</p></html>' } });
  await assert.rejects(discover(client(empty, fakeClock())), /lists no rulings library/);
  await assert.rejects(discover(client(fakePortal({ landing: { status: 404, body: '' } }), fakeClock())), /HTTP 404/);
});
