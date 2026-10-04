import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import {
  ANSWERED_ANNOUNCEMENT,
  ANSWER_ERROR_ANNOUNCEMENT,
  REFUSAL_TEXT,
  REFUSED_ANNOUNCEMENT,
  UNAVAILABLE_ANNOUNCEMENT,
} from './answer-store';
import { AnswerCitation, AnswerResponse, FacetsResponse, SearchResponse, SearchResult } from './api';
import { App, VALIDATION_MESSAGE } from './app';
import { routes } from './app.routes';

// Article 13 exists in two codes with different counts: they must never be merged.
const FACETS: FacetsResponse = {
  taxes: [
    { value: 'CIEC', count: 1 },
    { value: 'CIRS', count: 1189 },
    { value: 'CIVA', count: 2993 },
    { value: 'SELO', count: 200 },
  ],
  articles: [
    { tax: 'CIRS', value: '13', count: 17 },
    { tax: 'CIRS', value: '78-D', count: 40 },
    { tax: 'CIVA', value: '13', count: 5 },
    { tax: 'CIVA', value: 'Verba 1.12', count: 3 },
  ],
  years: [
    { value: 2024, count: 300 },
    { value: 2020, count: 42 },
  ],
};

function result(
  rulingId: string,
  passage = 'Despesas de educação dos filhos.',
  highlights = [{ start: 12, length: 8 }],
  tax = 'CIRS',
  article = '13',
): SearchResult {
  return {
    rulingId,
    processNumber: rulingId.replace('piv_', ''),
    tax,
    article,
    date: '2020-04-15',
    subject: 'Dependentes',
    section: 'content',
    passage,
    highlights,
    sourceUrl: `https://info.portaldasfinancas.gov.pt/${rulingId}.pdf`,
    score: 1,
  };
}

const response = (...results: SearchResult[]): SearchResponse => ({ query: 'q', tookMs: 12.4, results });

/** The answer cites results by their ruling: citation id n is the ruling piv_n. */
function citation(id: number, sourceUrl = `https://info.portaldasfinancas.gov.pt/piv_${id}.pdf`): AnswerCitation {
  return { ...result(`piv_${id}`), id, sourceUrl };
}

function answer(
  sentences: [string, number[]][],
  citations = [...new Set(sentences.flatMap(([, ids]) => ids))].map(id => citation(id)),
): AnswerResponse {
  return {
    query: 'q',
    status: 'answered',
    sentences: sentences.map(([text, ids]) => ({ text, citations: ids })),
    citations,
    model: 'qwen3-4b',
    firstTokenMs: 900,
    tookMs: 4000,
  };
}

describe('App', () => {
  let fixture: ComponentFixture<App>;
  let http: HttpTestingController;
  let router: Router;
  let el: HTMLElement;

  const settle = async () => {
    await new Promise(resolve => setTimeout(resolve));
    await fixture.whenStable();
  };
  const searches = () => http.match(r => r.url === '/api/search');
  const answers = () => http.match(r => r.url === '/api/answer');
  const lastAnswer = (): TestRequest => {
    const pending = answers();
    expect(pending.length).toBeGreaterThan(0);
    return pending[pending.length - 1];
  };
  const answerLive = () => el.querySelector('[data-answer-live]')!.textContent!.trim();
  const answerBlock = () => el.querySelector<HTMLElement>('[data-answer]');
  const lastSearch = (): TestRequest => {
    const pending = searches();
    expect(pending.length).toBeGreaterThan(0);
    return pending[pending.length - 1];
  };
  const input = () => el.querySelector<HTMLInputElement>('#q')!;
  const live = () => el.querySelector('[data-live]')!.textContent!.trim();
  const rulingIds = () => [...el.querySelectorAll('[data-result]')].map(li => li.getAttribute('data-ruling-id'));
  const queryParams = () => router.parseUrl(router.url).queryParams;
  const select = (id: string) => el.querySelector<HTMLSelectElement>(id)!;
  const options = (id: string) => [...select(id).options].map(o => [o.value, o.textContent!.trim()]);

  async function type(text: string) {
    input().value = text;
    input().dispatchEvent(new Event('input'));
    await settle();
  }

  async function submit() {
    input().focus();
    el.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    await settle();
  }

  async function chooseFilter(id: string, value: string) {
    const select = el.querySelector<HTMLSelectElement>(id)!;
    select.focus();
    select.value = value;
    select.dispatchEvent(new Event('change'));
    await settle();
  }

  async function start(url = '/') {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    await router.navigateByUrl(url);
    fixture = TestBed.createComponent(App);
    el = fixture.nativeElement;
    document.body.appendChild(el);
    await settle();
    http.expectOne('/api/facets').flush(FACETS);
    await settle();
  }

  afterEach(() => {
    // The answer requested after results is not part of every test; a pending one is dropped here.
    answers();
    http.verify({ ignoreCancelled: true });
    el?.remove();
  });

  it('shows the notice and the initial state, with no search', async () => {
    await start();
    expect(el.querySelector('[data-notice]')!.textContent).toContain('Não é aconselhamento fiscal.');
    expect(el.textContent).toContain('Escreva a sua pergunta como a faria a um contabilista');
    expect(el.textContent).not.toContain(String.fromCharCode(0x2014));
    expect(searches()).toHaveLength(0);
  });

  it('restores the question and filters from the URL and searches with them', async () => {
    await start('/?q=despesas%20de%20educa%C3%A7%C3%A3o&tax=CIRS&article=78-D&year=2020');

    const request = lastSearch();
    expect(request.request.params.get('q')).toBe('despesas de educação');
    expect(request.request.params.get('tax')).toBe('CIRS');
    expect(request.request.params.get('article')).toBe('78-D');
    expect(request.request.params.get('year')).toBe('2020');
    expect(input().value).toBe('despesas de educação');
    expect(el.querySelector<HTMLSelectElement>('#d-tax')!.value).toBe('CIRS');
    expect(el.querySelector<HTMLSelectElement>('#d-article')!.value).toBe('78-D');
    expect(el.querySelector<HTMLSelectElement>('#d-year')!.value).toBe('2020');
    expect(el.querySelector('[data-region]')!.getAttribute('aria-busy')).toBe('true');

    request.flush(response(result('piv_1')));
    await settle();
    expect(rulingIds()).toEqual(['piv_1']);
    expect(el.querySelector('[data-region]')!.hasAttribute('aria-busy')).toBe(false);
  });

  it('writes a submitted search to the URL', async () => {
    await start();
    await type('  mais-valias  ');
    await chooseFilter('#d-year', '2024');
    const pending = searches();
    expect(pending).toHaveLength(1);
    pending[0].flush(response(result('piv_1')));
    await settle();
    expect(queryParams()).toEqual({ q: 'mais-valias', year: '2024' });
  });

  it('renders passages and highlights as text, never as HTML', async () => {
    await start('/?q=x');
    const passage = 'Antes <img src=x onerror="window.hacked=1"> depois';
    lastSearch().flush(response(result('piv_1', passage, [{ start: 6, length: 37 }])));
    await settle();

    const quote = el.querySelector('[data-result] blockquote')!;
    expect(quote.querySelector('img')).toBeNull();
    expect(quote.querySelector('mark')!.textContent).toBe('<img src=x onerror="window.hacked=1">');
    expect(quote.textContent).toContain('Antes <img');
    const link = el.querySelector<HTMLAnchorElement>('[data-result] .actions a')!;
    expect(link.getAttribute('href')).toBe('https://info.portaldasfinancas.gov.pt/piv_1.pdf');
    expect(link.target).toBe('_blank');
    expect(link.rel).toBe('noopener noreferrer');
    expect(el.querySelector('[data-result]')!.textContent).toContain('Processo 1');
    expect(el.querySelector('[data-result] time')!.getAttribute('datetime')).toBe('2020-04-15');
    const citation = [...el.querySelectorAll('[data-result] .cite dd')].map(dd => dd.textContent!.trim());
    expect(citation).toEqual(['IRS', 'Art. 13.º', '15/04/2020', 'Secção: Texto']);
    expect(el.querySelector('[data-result]')!.getAttribute('data-tax')).toBe('CIRS');
    expect(el.querySelector('[data-result]')!.textContent).toContain('Secção: Texto');
  });

  it('validates a blank question without searching and keeps focus on the input', async () => {
    await start();
    await type('   ');
    await submit();
    expect(searches()).toHaveLength(0);
    expect(el.querySelector('#q-error')!.textContent).toBe(VALIDATION_MESSAGE);
    expect(input().getAttribute('aria-invalid')).toBe('true');
    expect(input().getAttribute('aria-describedby')).toBe('q-error');
    expect(document.activeElement).toBe(input());
    expect(live()).toBe(VALIDATION_MESSAGE);
  });

  it('lets only the latest search update the results when requests overlap', async () => {
    await start();
    await type('primeira');
    await submit();
    const first = lastSearch();
    await type('segunda');
    await submit();
    const second = lastSearch();
    expect(first.cancelled).toBe(true);
    expect(second.request.params.get('q')).toBe('segunda');
    expect(el.querySelectorAll('[data-result]')).toHaveLength(0);

    second.flush(response(result('piv_2')));
    await settle();
    expect(rulingIds()).toEqual(['piv_2']);
    expect(live()).toBe('1 informação vinculativa encontrada.');
    expect(document.activeElement).toBe(input());
  });

  it('shows the error of a later search even though an earlier one was pending', async () => {
    await start();
    await type('primeira');
    await submit();
    const first = lastSearch();
    await type('segunda');
    await submit();
    expect(first.cancelled).toBe(true);
    lastSearch().flush({ title: 'error' }, { status: 500, statusText: 'Server Error' });
    await settle();
    expect(el.querySelector('[data-error]')).not.toBeNull();
    expect(el.querySelectorAll('[data-result]')).toHaveLength(0);
    expect(live()).toBe('Não foi possível pesquisar.');
  });

  it('lets a tax change during loading supersede the pending request', async () => {
    await start();
    await type('educação');
    await submit();
    const unfiltered = lastSearch();
    await chooseFilter('#d-tax', 'CIVA');
    expect(unfiltered.cancelled).toBe(true);
    const filtered = lastSearch();
    expect(filtered.request.params.get('tax')).toBe('CIVA');
    expect(filtered.request.params.has('article')).toBe(false);
    expect(document.activeElement).toBe(el.querySelector('#d-tax'));
    expect(el.querySelectorAll('[data-result]')).toHaveLength(0);
    filtered.flush(response(result('piv_9', undefined, undefined, 'CIVA', '9')));
    await settle();
    expect(rulingIds()).toEqual(['piv_9']);
    expect(queryParams()['tax']).toBe('CIVA');
    expect(live()).toBe('1 informação vinculativa encontrada.');
  });

  it('keeps the newer tax when its search fails after an earlier one, and retries the newer one', async () => {
    await start('/?q=educa%C3%A7%C3%A3o&tax=CIRS');
    const first = lastSearch();
    await chooseFilter('#d-tax', 'CIVA');
    expect(first.cancelled).toBe(true);
    lastSearch().flush({ title: 'error' }, { status: 500, statusText: 'Server Error' });
    await settle();
    expect(el.querySelector('[data-error]')).not.toBeNull();

    el.querySelector<HTMLButtonElement>('[data-error] button')!.click();
    await settle();
    const retried = lastSearch();
    expect(retried.request.params.get('tax')).toBe('CIVA');
    retried.flush(response(result('piv_9', undefined, undefined, 'CIVA', '9')));
    await settle();
    expect(rulingIds()).toEqual(['piv_9']);
    expect(el.querySelector('[data-error]')).toBeNull();
  });

  it('lists every tax with its count and label, sorted by label, with unknown codes as they are', async () => {
    await start();
    expect(options('#d-tax')).toEqual([
      ['', 'Todos'],
      ['CIEC', 'CIEC (1)'],
      ['SELO', 'Imposto do Selo (200)'],
      ['CIRS', 'IRS (1189)'],
      ['CIVA', 'IVA (2993)'],
    ]);
  });

  it('disables the article select with a hint until a tax is chosen, then lists only its articles', async () => {
    await start();
    const article = select('#d-article');
    expect(article.disabled).toBe(true);
    expect(options('#d-article')).toEqual([['', 'Todos']]);
    expect(article.getAttribute('aria-describedby')).toBe('d-article-hint');
    expect(el.querySelector('#d-article-hint')!.textContent!.trim()).toBe('Escolha primeiro um imposto.');

    await chooseFilter('#d-tax', 'CIRS');
    expect(select('#d-article').disabled).toBe(false);
    expect(select('#d-article').hasAttribute('aria-describedby')).toBe(false);
    expect(el.querySelector('#d-article-hint')).toBeNull();
    expect(options('#d-article')).toEqual([
      ['', 'Todos'],
      ['13', '13.º (17)'],
      ['78-D', '78.º-D (40)'],
    ]);

    await chooseFilter('#d-tax', 'CIVA');
    expect(options('#d-article')).toEqual([
      ['', 'Todos'],
      ['13', '13.º (5)'],
      ['Verba 1.12', 'Verba 1.12 (3)'],
    ]);
    // Without a question, a filter change does not search.
    expect(searches()).toHaveLength(0);
  });

  it('clears the article when the tax changes or is cleared', async () => {
    await start('/?q=educa%C3%A7%C3%A3o&tax=CIRS&article=13');
    lastSearch().flush(response(result('piv_1')));
    await settle();
    expect(select('#d-article').value).toBe('13');

    await chooseFilter('#d-tax', 'CIVA');
    let request = lastSearch();
    expect(request.request.params.get('tax')).toBe('CIVA');
    expect(request.request.params.has('article')).toBe(false);
    expect(select('#d-article').value).toBe('');
    request.flush(response(result('piv_9', undefined, undefined, 'CIVA', '9')));
    await settle();
    expect(queryParams()).toEqual({ q: 'educação', tax: 'CIVA' });

    await chooseFilter('#d-article', 'Verba 1.12');
    request = lastSearch();
    expect(request.request.params.get('article')).toBe('Verba 1.12');
    request.flush(response(result('piv_8', undefined, undefined, 'CIVA', 'Verba 1.12')));
    await settle();
    expect(el.querySelector('.active-filters')!.textContent!.trim()).toBe('IVA · Verba 1.12');

    await chooseFilter('#d-tax', '');
    request = lastSearch();
    expect(request.request.params.has('tax')).toBe(false);
    expect(request.request.params.has('article')).toBe(false);
    expect(select('#d-article').disabled).toBe(true);
    request.flush(response(result('piv_1')));
    await settle();
    expect(queryParams()).toEqual({ q: 'educação' });
  });

  it('drops an article without a tax from the URL, with no error state', async () => {
    await start('/?q=educa%C3%A7%C3%A3o&article=13&year=2020');
    // Replacing the URL does not search a second time.
    const pending = searches();
    expect(pending).toHaveLength(1);
    const request = pending[0];
    expect(request.request.params.has('article')).toBe(false);
    expect(request.request.params.get('year')).toBe('2020');
    expect(queryParams()).toEqual({ q: 'educação', year: '2020' });
    expect(select('#d-article').value).toBe('');
    expect(select('#d-article').disabled).toBe(true);

    request.flush(response(result('piv_1')));
    await settle();
    expect(el.querySelector('[data-error]')).toBeNull();
    expect(rulingIds()).toEqual(['piv_1']);
  });

  it('shows the tax and article of every result', async () => {
    await start('/?q=x');
    lastSearch().flush(
      response(
        result('piv_1'),
        result('piv_2', undefined, undefined, 'CIVA', 'Verba 1.12'),
        result('piv_3', undefined, undefined, 'SELO', ''),
        result('piv_4', undefined, undefined, 'CIEC', '5'),
      ),
    );
    await settle();
    const cites = [...el.querySelectorAll('[data-result] .cite dl')].map(dl =>
      [...dl.querySelectorAll('dd')].slice(0, 2).map(dd => dd.textContent!.trim()),
    );
    expect(cites).toEqual([
      ['IRS', 'Art. 13.º'],
      ['IVA', 'Verba 1.12'],
      ['Imposto do Selo', '15/04/2020'],
      ['CIEC', 'Art. 5.º'],
    ]);
  });

  it('scopes the article select in the mobile sheet the same way, applying only on "Aplicar filtros"', async () => {
    // jsdom has no modal dialog; these stand-ins open and close it like the browser does.
    const proto = HTMLDialogElement.prototype as unknown as Record<string, unknown>;
    const saved = { showModal: proto['showModal'], close: proto['close'] };
    proto['showModal'] = function (this: HTMLDialogElement) {
      this.setAttribute('open', '');
    };
    proto['close'] = function (this: HTMLDialogElement) {
      this.removeAttribute('open');
      this.dispatchEvent(new Event('close'));
    };
    try {
      await start('/?q=educa%C3%A7%C3%A3o&tax=CIRS&article=13');
      lastSearch().flush(response(result('piv_1')));
      await settle();

      el.querySelector<HTMLButtonElement>('.filters-toggle')!.click();
      await settle();
      expect(document.activeElement).toBe(select('#m-tax'));
      expect(select('#m-article').value).toBe('13');

      await chooseFilter('#m-tax', 'CIVA');
      expect(select('#m-article').value).toBe('');
      expect(options('#m-article').map(([value]) => value)).toEqual(['', '13', 'Verba 1.12']);
      await chooseFilter('#m-article', 'Verba 1.12');

      await chooseFilter('#m-tax', '');
      expect(select('#m-article').disabled).toBe(true);
      expect(el.querySelector('#m-article-hint')!.textContent!.trim()).toBe('Escolha primeiro um imposto.');
      await chooseFilter('#m-tax', 'CIVA');
      await chooseFilter('#m-article', 'Verba 1.12');
      // The sheet does not search before it is applied, and the rail is unchanged.
      expect(searches()).toHaveLength(0);
      expect(select('#d-tax').value).toBe('CIRS');

      el.querySelector<HTMLButtonElement>('dialog .btn')!.click();
      await settle();
      const request = lastSearch();
      expect(request.request.params.get('tax')).toBe('CIVA');
      expect(request.request.params.get('article')).toBe('Verba 1.12');
      expect(document.activeElement).toBe(el.querySelector('.filters-toggle'));
      request.flush(response(result('piv_8', undefined, undefined, 'CIVA', 'Verba 1.12')));
      await settle();
      expect(select('#d-tax').value).toBe('CIVA');
      expect(select('#d-article').value).toBe('Verba 1.12');
      expect(el.querySelector('.filters-toggle')!.textContent!.trim()).toBe('Filtros (2)');
    } finally {
      proto['showModal'] = saved.showModal;
      proto['close'] = saved.close;
    }
  });

  it('retries the last submitted search, not unsubmitted edits, and recovers', async () => {
    await start();
    await type('educação');
    await chooseFilter('#d-year', '2020');
    lastSearch().flush({ title: 'error' }, { status: 500, statusText: 'Server Error' });
    await settle();
    expect(el.querySelector('[data-error] h2')!.textContent).toBe('Não foi possível pesquisar');
    expect(el.querySelector('[data-error]')!.getAttribute('role')).toBeNull();

    await type('outra pergunta ainda por submeter');
    const retry = el.querySelector<HTMLButtonElement>('[data-error] button')!;
    retry.focus();
    retry.click();
    await settle();
    const request = lastSearch();
    expect(request.request.params.get('q')).toBe('educação');
    expect(request.request.params.get('year')).toBe('2020');
    // While retrying, the error block and its (busy) button stay, so focus is not lost.
    expect(el.querySelector('[data-error] button')).toBe(retry);
    expect(retry.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(retry);
    expect(live()).toBe('');

    request.flush(response(result('piv_1'), result('piv_2')));
    await settle();
    expect(rulingIds()).toEqual(['piv_1', 'piv_2']);
    expect(el.querySelector('[data-error]')).toBeNull();
    expect(live()).toBe('2 informações vinculativas encontradas.');
    expect(document.activeElement).toBe(el.querySelector('[data-summary]'));
    expect(input().value).toBe('outra pergunta ainda por submeter');
  });

  it('keeps focus on "Tentar de novo" when a retry fails again', async () => {
    await start('/?q=x');
    lastSearch().flush(null, { status: 0, statusText: 'Unknown Error' });
    await settle();
    const retry = el.querySelector<HTMLButtonElement>('[data-error] button')!;
    retry.focus();
    retry.click();
    await settle();
    lastSearch().flush(null, { status: 503, statusText: 'Unavailable' });
    await settle();
    expect(el.querySelector('[data-error] button')).toBe(retry);
    expect(retry.textContent!.trim()).toBe('Tentar de novo');
    expect(document.activeElement).toBe(retry);
    expect(live()).toBe('Não foi possível pesquisar.');
  });

  it('refreshes from an empty result when the filters are cleared', async () => {
    await start('/?q=educa%C3%A7%C3%A3o&tax=CIRS&article=78-D&year=2024');
    lastSearch().flush(response());
    await settle();
    expect(el.querySelector('[data-empty] h2')!.textContent).toBe('Nenhuma informação vinculativa encontrada');
    expect(live()).toBe('Nenhuma informação vinculativa encontrada.');

    el.querySelector<HTMLButtonElement>('[data-empty] button')!.click();
    await settle();
    const request = lastSearch();
    expect(request.request.params.get('q')).toBe('educação');
    expect(request.request.params.has('tax')).toBe(false);
    expect(request.request.params.has('article')).toBe(false);
    expect(request.request.params.has('year')).toBe(false);
    expect(document.activeElement).toBe(input());
    request.flush(response(result('piv_1')));
    await settle();
    expect(rulingIds()).toEqual(['piv_1']);
    expect(queryParams()).toEqual({ q: 'educação' });
  });

  it('follows back and forward navigation between searches', async () => {
    await start('/?q=primeira');
    lastSearch().flush(response(result('piv_1')));
    await settle();
    await router.navigateByUrl('/?q=segunda&year=2024');
    await settle();
    expect(input().value).toBe('segunda');
    expect(el.querySelector<HTMLSelectElement>('#d-year')!.value).toBe('2024');
    lastSearch().flush(response(result('piv_2')));
    await settle();
    expect(rulingIds()).toEqual(['piv_2']);

    await router.navigateByUrl('/');
    await settle();
    expect(input().value).toBe('');
    expect(el.textContent).toContain('Escreva a sua pergunta como a faria a um contabilista');
  });

  describe('answer', () => {
    async function resultsFor(url: string, ...results: SearchResult[]) {
      await start(url);
      lastSearch().flush(response(...results));
      await settle();
    }

    it('shows the results first, then requests the answer of the same search and shows it above them', async () => {
      await start('/?q=educa%C3%A7%C3%A3o&tax=CIRS&year=2020');
      const search = lastSearch();
      expect(answers()).toHaveLength(0);
      search.flush(response(result('piv_1'), result('piv_2')));
      await settle();
      expect(rulingIds()).toEqual(['piv_1', 'piv_2']);

      const request = lastAnswer();
      expect(request.request.params.get('q')).toBe('educação');
      expect(request.request.params.get('tax')).toBe('CIRS');
      expect(request.request.params.get('year')).toBe('2020');
      expect(answerBlock()!.getAttribute('aria-busy')).toBe('true');
      expect(answerBlock()!.textContent).toContain('A preparar a resposta…');
      expect(el.querySelector('[data-region]')!.hasAttribute('aria-busy')).toBe(false);
      // The notice comes first, then the answer block, then the summary line and the results.
      const follows = (a: Element, b: Element) => !!(a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING);
      expect(follows(el.querySelector('[data-notice]')!, answerBlock()!)).toBe(true);
      expect(follows(answerBlock()!, el.querySelector('[data-summary]')!)).toBe(true);

      request.flush(answer([['Pode deduzir as despesas.', [1, 2]], ['O limite aplica-se.', [2]]]));
      await settle();
      expect(answerBlock()!.hasAttribute('aria-busy')).toBe(false);
      expect(answerBlock()!.getAttribute('data-state')).toBe('answered');
      expect(answerBlock()!.textContent).toContain('Resposta gerada a partir das passagens citadas');
      expect(answerBlock()!.textContent).toContain('Confirme sempre na informação vinculativa oficial.');
      expect([...el.querySelectorAll('[data-answer-sentence]')].map(s => s.textContent)).toEqual(['Pode deduzir as despesas.', 'O limite aplica-se.']);
      const marks = [...el.querySelectorAll<HTMLAnchorElement>('.answer-mark')];
      expect(marks.map(m => m.getAttribute('href'))).toEqual(['#result-1', '#result-2', '#result-2']);
      expect(marks[0].textContent).toBe('Processo 1, resultado 1 da lista');
      expect(marks.every(m => !m.hasAttribute('target'))).toBe(true);
      expect(answerBlock()!.querySelector('.answer-text')!.textContent!.replace(/\s+/g, ' ').trim()).toBe(
        'Pode deduzir as despesas. (Processo 1, resultado 1 da lista; Processo 2, resultado 2 da lista) O limite aplica-se. (Processo 2, resultado 2 da lista)',
      );
      expect(answerLive()).toBe(ANSWERED_ANNOUNCEMENT);
      expect(live()).toBe('2 informações vinculativas encontradas.');
      expect(el.textContent).not.toContain(String.fromCharCode(0x2014));
    });

    it('moves focus to the cited result when a citation marker is activated', async () => {
      await resultsFor('/?q=x', result('piv_1'), result('piv_2'));
      lastAnswer().flush(answer([['Pode deduzir as despesas.', [2]]]));
      await settle();
      const mark = el.querySelector<HTMLAnchorElement>('.answer-mark')!;
      mark.focus();
      const click = new MouseEvent('click', { bubbles: true, cancelable: true });
      mark.dispatchEvent(click);
      await settle();
      expect(click.defaultPrevented).toBe(true);
      const target = el.querySelector('#result-2')!;
      expect(target.getAttribute('data-ruling-id')).toBe('piv_2');
      expect(document.activeElement).toBe(target);
      expect(router.url).not.toContain('#');
    });

    it('links a cited ruling that is not in the list to its official PDF instead', async () => {
      await resultsFor('/?q=x', result('piv_1'));
      lastAnswer().flush(answer([['Frase com fonte.', [7]]]));
      await settle();
      const mark = el.querySelector<HTMLAnchorElement>('.answer-mark')!;
      expect(mark.getAttribute('href')).toBe('https://info.portaldasfinancas.gov.pt/piv_7.pdf');
      expect(mark.target).toBe('_blank');
      expect(mark.rel).toBe('noopener noreferrer');
      expect(mark.textContent).toBe('Processo 7, abrir PDF oficial (abre num novo separador)');
    });

    it('renders answer text as text and hides sentences that fail the client-side checks', async () => {
      await resultsFor('/?q=x', result('piv_1'), result('piv_2'));
      lastAnswer().flush(
        answer(
          [
            ['Antes <img src=x onerror="window.hacked=1"> depois.', [1]],
            ['Cita um id desconhecido.', [9]],
            ['Cita uma ligação http.', [2]],
          ],
          [citation(1), citation(2, 'http://info.portaldasfinancas.gov.pt/piv_2.pdf')],
        ),
      );
      await settle();
      expect(answerBlock()!.querySelector('img')).toBeNull();
      expect([...el.querySelectorAll('[data-answer-sentence]')].map(s => s.textContent)).toEqual(['Antes <img src=x onerror="window.hacked=1"> depois.']);
      expect([...el.querySelectorAll('.answer-mark')].map(m => m.getAttribute('href'))).toEqual(['#result-1']);
    });

    it('shows the fixed refusal when the answer is refused or nothing valid is left', async () => {
      await resultsFor('/?q=x', result('piv_1'));
      lastAnswer().flush({ ...answer([]), status: 'refused', sentences: [{ text: 'Outro texto qualquer', citations: [] }] });
      await settle();
      expect(answerBlock()!.getAttribute('data-state')).toBe('refused');
      expect(answerBlock()!.querySelector('.answer-refused')!.textContent).toBe(REFUSAL_TEXT);
      expect(answerBlock()!.textContent).not.toContain('Outro texto qualquer');
      expect(el.querySelectorAll('.answer-mark')).toHaveLength(0);
      expect(answerLive()).toBe(REFUSED_ANNOUNCEMENT);

      await type('y');
      await submit();
      lastSearch().flush(response(result('piv_1')));
      await settle();
      lastAnswer().flush(answer([['Só com id desconhecido.', [9]]], [citation(1)]));
      await settle();
      expect(answerBlock()!.getAttribute('data-state')).toBe('refused');
    });

    it('shows a quiet note when the answer is unavailable, leaving the results untouched', async () => {
      await resultsFor('/?q=x', result('piv_1'), result('piv_2'));
      const region = el.querySelector('[data-region]')!.innerHTML;
      lastAnswer().flush({ title: 'The answer model is not available.' }, { status: 503, statusText: 'Service Unavailable' });
      await settle();
      expect(answerBlock()!.getAttribute('data-state')).toBe('unavailable');
      expect(answerBlock()!.textContent).toContain('A resposta escrita não está disponível neste momento.');
      expect(el.querySelector('[data-answer-error]')).toBeNull();
      expect(el.querySelector('[data-region]')!.innerHTML).toBe(region);
      expect(answerLive()).toBe(UNAVAILABLE_ANNOUNCEMENT);
      expect(live()).toBe('2 informações vinculativas encontradas.');
    });

    it('shows an answer-only error, keeps focus while retrying and recovers', async () => {
      await resultsFor('/?q=x', result('piv_1'), result('piv_2'));
      const region = el.querySelector('[data-region]')!.innerHTML;
      lastAnswer().flush({ title: 'error' }, { status: 500, statusText: 'Server Error' });
      await settle();
      expect(answerBlock()!.getAttribute('data-state')).toBe('error');
      expect(el.querySelector('[data-answer-error]')!.textContent).toContain('Não foi possível preparar a resposta.');
      expect(el.querySelector('[data-region]')!.innerHTML).toBe(region);
      expect(el.querySelector('[data-error]')).toBeNull();
      expect(answerLive()).toBe(ANSWER_ERROR_ANNOUNCEMENT);
      expect(live()).toBe('2 informações vinculativas encontradas.');

      const retry = el.querySelector<HTMLButtonElement>('[data-answer-error] button')!;
      retry.focus();
      retry.click();
      await settle();
      expect(searches()).toHaveLength(0);
      const request = lastAnswer();
      expect(request.request.params.get('q')).toBe('x');
      expect(el.querySelector('[data-answer-error] button')).toBe(retry);
      expect(retry.getAttribute('aria-disabled')).toBe('true');
      expect(answerBlock()!.getAttribute('aria-busy')).toBe('true');
      expect(document.activeElement).toBe(retry);
      expect(answerLive()).toBe('');

      request.flush(answer([['Pode deduzir as despesas.', [1]]]));
      await settle();
      expect(answerBlock()!.getAttribute('data-state')).toBe('answered');
      expect(document.activeElement).toBe(el.querySelector('#answer-title'));
      expect(answerLive()).toBe(ANSWERED_ANNOUNCEMENT);
      expect(el.querySelector('[data-region]')!.innerHTML).toBe(region);
    });

    it('requests no answer for an empty result, a failed search or a blank question', async () => {
      await start('/?q=x&tax=CIRS&article=78-D');
      lastSearch().flush(response());
      await settle();
      expect(answers()).toHaveLength(0);
      expect(answerBlock()).toBeNull();

      await type('y');
      await submit();
      lastSearch().flush({ title: 'error' }, { status: 500, statusText: 'Server Error' });
      await settle();
      expect(answers()).toHaveLength(0);

      await type('  ');
      await submit();
      expect(answers()).toHaveLength(0);
      expect(answerBlock()).toBeNull();
    });

    it('aborts the pending answer on a new search, a filter change and a reset, and drops it from view', async () => {
      await resultsFor('/?q=x', result('piv_1'));
      let pending = lastAnswer();
      await type('y');
      await submit();
      expect(pending.cancelled).toBe(true);
      expect(answerBlock()).toBeNull();
      lastSearch().flush(response(result('piv_2')));
      await settle();

      pending = lastAnswer();
      await chooseFilter('#d-tax', 'CIVA');
      expect(pending.cancelled).toBe(true);
      expect(answerBlock()).toBeNull();
      lastSearch().flush(response(result('piv_3')));
      await settle();

      pending = lastAnswer();
      await router.navigateByUrl('/');
      await settle();
      expect(pending.cancelled).toBe(true);
      expect(answerBlock()).toBeNull();
      expect(answerLive()).toBe('');
    });
  });
});
