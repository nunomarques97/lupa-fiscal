import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { FacetsResponse, SearchResponse, SearchResult } from './api';
import { App, VALIDATION_MESSAGE } from './app';
import { routes } from './app.routes';

const FACETS: FacetsResponse = {
  taxes: [{ value: 'CIRS', count: 1189 }],
  articles: [
    { value: '13', count: 17 },
    { value: '78-D', count: 40 },
  ],
  years: [
    { value: 2024, count: 300 },
    { value: 2020, count: 42 },
  ],
};

function result(rulingId: string, passage = 'Despesas de educação dos filhos.', highlights = [{ start: 12, length: 8 }]): SearchResult {
  return {
    rulingId,
    processNumber: rulingId.replace('piv_', ''),
    tax: 'CIRS',
    article: '13',
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
  const lastSearch = (): TestRequest => {
    const pending = searches();
    expect(pending.length).toBeGreaterThan(0);
    return pending[pending.length - 1];
  };
  const input = () => el.querySelector<HTMLInputElement>('#q')!;
  const live = () => el.querySelector('[data-live]')!.textContent!.trim();
  const rulingIds = () => [...el.querySelectorAll('[data-result]')].map(li => li.getAttribute('data-ruling-id'));
  const queryParams = () => router.parseUrl(router.url).queryParams;

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
    await start('/?q=despesas%20de%20educa%C3%A7%C3%A3o&article=78-D&year=2020');

    const request = lastSearch();
    expect(request.request.params.get('q')).toBe('despesas de educação');
    expect(request.request.params.get('article')).toBe('78-D');
    expect(request.request.params.get('year')).toBe('2020');
    expect(request.request.params.has('tax')).toBe(false);
    expect(input().value).toBe('despesas de educação');
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
    expect(el.querySelector('[data-result]')!.textContent).toContain('Art. 13.º CIRS');
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

  it('lets a filter change during loading supersede the pending request', async () => {
    await start();
    await type('educação');
    await submit();
    const unfiltered = lastSearch();
    await chooseFilter('#d-article', '13');
    expect(unfiltered.cancelled).toBe(true);
    const filtered = lastSearch();
    expect(filtered.request.params.get('article')).toBe('13');
    expect(document.activeElement).toBe(el.querySelector('#d-article'));
    filtered.flush(response(result('piv_13')));
    await settle();
    expect(rulingIds()).toEqual(['piv_13']);
    expect(queryParams()['article']).toBe('13');
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
    await start('/?q=educa%C3%A7%C3%A3o&article=78-D&year=2024');
    lastSearch().flush(response());
    await settle();
    expect(el.querySelector('[data-empty] h2')!.textContent).toBe('Nenhuma informação vinculativa encontrada');
    expect(live()).toBe('Nenhuma informação vinculativa encontrada.');

    el.querySelector<HTMLButtonElement>('[data-empty] button')!.click();
    await settle();
    const request = lastSearch();
    expect(request.request.params.get('q')).toBe('educação');
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
});
