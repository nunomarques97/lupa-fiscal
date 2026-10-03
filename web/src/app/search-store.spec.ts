import { TestBed } from '@angular/core/testing';

import { SearchApi, SearchResponse, SearchResult } from './api';
import { SearchParams } from './search-params';
import { ERROR_ANNOUNCEMENT, SearchStore } from './search-store';

interface Pending {
  params: SearchParams;
  signal: AbortSignal;
  resolve: (response: SearchResponse) => void;
  reject: (error: unknown) => void;
}

/**
 * A fake API whose responses complete only when the test says so, in any order, and which ignores
 * aborts: a response that raced the abort still arrives, so the store's own ownership check is tested.
 */
class FakeApi {
  readonly calls: Pending[] = [];
  search(params: SearchParams, signal: AbortSignal): Promise<SearchResponse> {
    return new Promise((resolve, reject) => this.calls.push({ params, signal, resolve, reject }));
  }
}

// An article is only sent with its tax, as the filters do.
const params = (q: string, article = '', tax = article ? 'CIRS' : ''): SearchParams => ({ q, tax, article, year: '' });

function result(rulingId: string, sourceUrl = 'https://info.portaldasfinancas.gov.pt/x.pdf'): SearchResult {
  return {
    rulingId,
    processNumber: rulingId,
    tax: 'CIRS',
    article: '13',
    date: '2020-04-15',
    subject: 'Assunto',
    section: 'content',
    passage: 'Texto da passagem.',
    highlights: [],
    sourceUrl,
    score: 1,
  };
}

const response = (...ids: string[]): SearchResponse => ({ query: 'q', tookMs: 5, results: ids.map(id => result(id)) });

const flush = () => new Promise(resolve => setTimeout(resolve));

describe('SearchStore', () => {
  let api: FakeApi;
  let store: SearchStore;

  beforeEach(() => {
    api = new FakeApi();
    TestBed.configureTestingModule({ providers: [{ provide: SearchApi, useValue: api }] });
    store = TestBed.inject(SearchStore);
  });

  it('keeps the later success when an earlier search fails afterwards', async () => {
    void store.search(params('a'));
    void store.search(params('b'));
    expect(api.calls[0].signal.aborted).toBe(true);

    api.calls[1].resolve(response('B1', 'B2'));
    await flush();
    api.calls[0].reject(new Error('500'));
    await flush();

    const state = store.state();
    expect(state.kind).toBe('results');
    if (state.kind === 'results') {
      expect(state.params.q).toBe('b');
      expect(state.response.results.map(r => r.rulingId)).toEqual(['B1', 'B2']);
    }
    expect(store.announcement()).toBe('2 informações vinculativas encontradas.');
  });

  it('keeps the later failure when an earlier search succeeds afterwards', async () => {
    void store.search(params('a'));
    void store.search(params('b'));

    api.calls[1].reject(new Error('500'));
    await flush();
    api.calls[0].resolve(response('A1'));
    await flush();

    const state = store.state();
    expect(state.kind).toBe('error');
    if (state.kind === 'error') expect(state.params.q).toBe('b');
    expect(store.announcement()).toBe(ERROR_ANNOUNCEMENT);
  });

  it('never shows an earlier response while the later search is still loading', async () => {
    void store.search(params('a'));
    void store.search(params('b'));

    api.calls[0].resolve(response('A1'));
    await flush();

    expect(store.state()).toEqual({ kind: 'loading', params: params('b'), retrying: false });
    expect(store.announcement()).toBe('');
  });

  it('lets a filter change during loading supersede the pending request', async () => {
    void store.search(params('a'));
    void store.search(params('a', '13'));
    expect(api.calls[0].signal.aborted).toBe(true);
    expect(api.calls[1].params.article).toBe('13');

    api.calls[0].resolve(response('UNFILTERED'));
    api.calls[1].resolve(response('FILTERED'));
    await flush();

    const state = store.state();
    expect(state.kind === 'results' && state.response.results[0].rulingId).toBe('FILTERED');
  });

  for (const outcome of ['success', 'failure'] as const) {
    it(`ignores a late earlier ${outcome} when the tax changes mid-request`, async () => {
      void store.search(params('a', '', 'CIRS'));
      void store.search(params('a', '', 'CIVA'));
      expect(api.calls[0].signal.aborted).toBe(true);
      expect(api.calls[1].params.tax).toBe('CIVA');

      api.calls[1].resolve(response('IVA1'));
      await flush();
      if (outcome === 'success') api.calls[0].resolve(response('IRS1'));
      else api.calls[0].reject(new Error('500'));
      await flush();

      const state = store.state();
      expect(state.kind).toBe('results');
      if (state.kind === 'results') {
        expect(state.params.tax).toBe('CIVA');
        expect(state.response.results.map(r => r.rulingId)).toEqual(['IVA1']);
      }
      expect(store.submitted()?.tax).toBe('CIVA');
      expect(store.announcement()).toBe('1 informação vinculativa encontrada.');
    });
  }

  it('retries the last submitted search after a failure and recovers', async () => {
    void store.search(params('a', '13'));
    api.calls[0].reject(new Error('network'));
    await flush();
    expect(store.state().kind).toBe('error');

    const retry = store.retry();
    expect(store.state()).toEqual({ kind: 'loading', params: params('a', '13'), retrying: true });
    expect(store.announcement()).toBe('');
    expect(api.calls[1].params).toEqual(params('a', '13'));

    api.calls[1].resolve(response('R1'));
    await retry;
    expect(store.state().kind).toBe('results');
    expect(store.announcement()).toBe('1 informação vinculativa encontrada.');
  });

  it('refreshes from an empty result to results', async () => {
    void store.search(params('a', '99-F'));
    api.calls[0].resolve(response());
    await flush();
    expect(store.state().kind).toBe('empty');
    expect(store.announcement()).toBe('Nenhuma informação vinculativa encontrada.');

    void store.search(params('a'));
    expect(store.state().kind).toBe('loading');
    expect(store.announcement()).toBe('');
    api.calls[1].resolve(response('X'));
    await flush();
    expect(store.state().kind).toBe('results');
  });

  it('announces exactly once per completed search', async () => {
    const seen: string[] = [];
    const record = () => seen.push(store.announcement());
    void store.search(params('a'));
    record();
    api.calls[0].resolve(response('A'));
    await flush();
    record();
    expect(seen).toEqual(['', '1 informação vinculativa encontrada.']);
  });

  it('drops results without an https link to the official document', async () => {
    void store.search(params('a'));
    api.calls[0].resolve({
      query: 'a',
      tookMs: 1,
      results: [result('ok'), result('js', 'javascript:alert(1)'), result('http', 'http://example.org/x.pdf')],
    });
    await flush();
    const state = store.state();
    expect(state.kind === 'results' && state.response.results.map(r => r.rulingId)).toEqual(['ok']);
  });

  it('abandons a pending search on reset', async () => {
    void store.search(params('a'));
    store.reset();
    expect(api.calls[0].signal.aborted).toBe(true);
    api.calls[0].resolve(response('A'));
    await flush();
    expect(store.state()).toEqual({ kind: 'initial' });
    expect(store.submitted()).toBeNull();
  });
});
