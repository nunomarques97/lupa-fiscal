import { TestBed } from '@angular/core/testing';

import { AnswerStore } from './answer-store';
import { AnswerResponse, SearchApi, SearchResponse, SearchResult } from './api';
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
  readonly answers: { params: SearchParams; signal: AbortSignal; resolve: (r: AnswerResponse) => void; reject: (e: unknown) => void }[] = [];
  search(params: SearchParams, signal: AbortSignal): Promise<SearchResponse> {
    return new Promise((resolve, reject) => this.calls.push({ params, signal, resolve, reject }));
  }
  answer(params: SearchParams, signal: AbortSignal): Promise<AnswerResponse> {
    return new Promise((resolve, reject) => this.answers.push({ params, signal, resolve, reject }));
  }
}

const answer = (text: string, rulingId: string): AnswerResponse => ({
  query: 'q',
  status: 'answered',
  sentences: [{ text, citations: [1] }],
  citations: [{ ...result(rulingId), id: 1 }],
  model: 'm',
  firstTokenMs: 1,
  tookMs: 2,
});

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
  let answers: AnswerStore;

  beforeEach(() => {
    api = new FakeApi();
    TestBed.configureTestingModule({ providers: [{ provide: SearchApi, useValue: api }] });
    store = TestBed.inject(SearchStore);
    answers = TestBed.inject(AnswerStore);
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

  describe('answer', () => {
    it('is requested only after the search returns results, with the same parameters', async () => {
      void store.search(params('a', '13'));
      expect(api.answers).toHaveLength(0);
      expect(answers.state().kind).toBe('idle');
      api.calls[0].resolve(response('A1'));
      await flush();
      expect(store.state().kind).toBe('results');
      expect(api.answers.map(a => a.params)).toEqual([params('a', '13')]);
      expect(answers.state().kind).toBe('loading');

      api.answers[0].resolve(answer('Pode deduzir.', 'A1'));
      await flush();
      expect(answers.state().kind).toBe('answered');
      // The answer has its own announcement; the results announcement is not repeated or replaced.
      expect(store.announcement()).toBe('1 informação vinculativa encontrada.');
      expect(answers.announcement()).not.toBe('');
    });

    it('is not requested for an empty result, an error or a blank reset', async () => {
      void store.search(params('a', '99-F'));
      api.calls[0].resolve(response());
      await flush();
      expect(store.state().kind).toBe('empty');

      void store.search(params('b'));
      api.calls[1].reject(new Error('500'));
      await flush();
      expect(store.state().kind).toBe('error');

      store.reset();
      expect(api.answers).toHaveLength(0);
      expect(answers.state()).toEqual({ kind: 'idle' });
    });

    it('is aborted by a new search, a filter change, a retry and a reset', async () => {
      const answerFor = async (q: string, filter = '') => {
        void store.search(params(q, filter));
        api.calls.at(-1)!.resolve(response(q));
        await flush();
        return api.answers.at(-1)!;
      };

      let pending = await answerFor('a');
      void store.search(params('b'));
      expect(pending.signal.aborted).toBe(true);
      expect(answers.state()).toEqual({ kind: 'idle' });
      api.calls.at(-1)!.reject(new Error('500'));
      await flush();

      pending = await answerFor('c');
      void store.search(params('c', '13'));
      expect(pending.signal.aborted).toBe(true);
      expect(answers.busy()).toBe(false);
      api.calls.at(-1)!.reject(new Error('500'));
      await flush();

      pending = await answerFor('d');
      void store.retry();
      expect(pending.signal.aborted).toBe(true);
      api.calls.at(-1)!.reject(new Error('500'));
      await flush();

      pending = await answerFor('e');
      store.reset();
      expect(pending.signal.aborted).toBe(true);
      expect(answers.state()).toEqual({ kind: 'idle' });
    });

    for (const outcome of ['success', 'failure'] as const) {
      it(`never shows a late earlier answer (${outcome}) with the results of a later search`, async () => {
        void store.search(params('a'));
        api.calls[0].resolve(response('A1'));
        await flush();
        const earlier = api.answers[0];

        void store.search(params('b'));
        api.calls[1].resolve(response('B1'));
        await flush();
        expect(api.answers).toHaveLength(2);
        api.answers[1].resolve(answer('Resposta B.', 'B1'));
        await flush();

        if (outcome === 'success') earlier.resolve(answer('Resposta A.', 'A1'));
        else earlier.reject(new Error('500'));
        await flush();

        const state = answers.state();
        expect(state.kind === 'answered' && state.params.q).toBe('b');
        expect(state.kind === 'answered' && state.answer.citations.map(c => c.rulingId)).toEqual(['B1']);
        const results = store.state();
        expect(results.kind === 'results' && results.response.results.map(r => r.rulingId)).toEqual(['B1']);
      });
    }

    it('never changes the results when it fails, and its retry works', async () => {
      void store.search(params('a'));
      api.calls[0].resolve(response('A1', 'A2'));
      await flush();
      const before = store.state();

      api.answers[0].reject(new Error('500'));
      await flush();
      expect(answers.state().kind).toBe('error');
      expect(store.state()).toBe(before);
      expect(store.announcement()).toBe('2 informações vinculativas encontradas.');

      const retry = answers.retry();
      expect(api.answers[1].params).toEqual(params('a'));
      expect(api.calls).toHaveLength(1);
      api.answers[1].resolve(answer('Pode deduzir.', 'A1'));
      await retry;
      expect(answers.state().kind).toBe('answered');
      expect(store.state()).toBe(before);
    });
  });
});
