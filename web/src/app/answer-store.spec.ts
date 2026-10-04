import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';

import {
  ANSWERED_ANNOUNCEMENT,
  ANSWER_ERROR_ANNOUNCEMENT,
  AnswerStore,
  REFUSED_ANNOUNCEMENT,
  UNAVAILABLE_ANNOUNCEMENT,
  shownAnswer,
} from './answer-store';
import { AnswerCitation, AnswerResponse, SearchApi } from './api';
import { SearchParams } from './search-params';

interface Pending {
  params: SearchParams;
  signal: AbortSignal;
  resolve: (response: AnswerResponse) => void;
  reject: (error: unknown) => void;
}

/** Answers complete only when the test says so, in any order, and aborts are ignored (as in a race). */
class FakeApi {
  readonly calls: Pending[] = [];
  answer(params: SearchParams, signal: AbortSignal): Promise<AnswerResponse> {
    return new Promise((resolve, reject) => this.calls.push({ params, signal, resolve, reject }));
  }
}

const params = (q: string): SearchParams => ({ q, tax: '', article: '', year: '' });

function citation(id: number, sourceUrl = `https://info.portaldasfinancas.gov.pt/${id}.pdf`): AnswerCitation {
  return {
    id,
    rulingId: `piv_${id}`,
    processNumber: String(1000 + id),
    tax: 'CIRS',
    article: '78-D',
    date: '2020-04-15',
    subject: 'Assunto',
    section: 'conclusion',
    passage: 'Passagem citada.',
    sourceUrl,
  };
}

function answered(text: string, ...ids: number[]): AnswerResponse {
  return {
    query: 'q',
    status: 'answered',
    sentences: [{ text, citations: ids }],
    citations: ids.map(id => citation(id)),
    model: 'qwen3-4b',
    firstTokenMs: 1,
    tookMs: 2,
  };
}

const refused = (): AnswerResponse => ({
  query: 'q',
  status: 'refused',
  sentences: [{ text: 'Recusa', citations: [] }],
  citations: [],
  model: 'qwen3-4b',
  firstTokenMs: 1,
  tookMs: 2,
});

const httpError = (status: number) => new HttpErrorResponse({ status, statusText: 'x', url: '/api/answer' });
const flush = () => new Promise(resolve => setTimeout(resolve));

describe('AnswerStore', () => {
  let api: FakeApi;
  let store: AnswerStore;

  beforeEach(() => {
    api = new FakeApi();
    TestBed.configureTestingModule({ providers: [{ provide: SearchApi, useValue: api }] });
    store = TestBed.inject(AnswerStore);
  });

  it('loads, then shows the answer and announces it once', async () => {
    const seen: string[] = [];
    void store.start(params('a'));
    expect(store.state()).toEqual({ kind: 'loading', params: params('a'), retrying: false });
    expect(store.busy()).toBe(true);
    seen.push(store.announcement());

    api.calls[0].resolve(answered('Pode deduzir.', 1));
    await flush();
    seen.push(store.announcement());
    const state = store.state();
    expect(state.kind).toBe('answered');
    if (state.kind === 'answered') expect(state.answer.sentences).toEqual([{ text: 'Pode deduzir.', citations: [1] }]);
    expect(store.busy()).toBe(false);
    expect(seen).toEqual(['', ANSWERED_ANNOUNCEMENT]);
  });

  for (const outcome of ['success', 'failure'] as const) {
    it(`drops a late earlier ${outcome} once a later answer has arrived`, async () => {
      void store.start(params('a'));
      void store.start(params('b'));
      expect(api.calls[0].signal.aborted).toBe(true);

      api.calls[1].resolve(answered('Resposta B.', 2));
      await flush();
      if (outcome === 'success') api.calls[0].resolve(answered('Resposta A.', 1));
      else api.calls[0].reject(httpError(500));
      await flush();

      const state = store.state();
      expect(state.kind === 'answered' && state.params.q).toBe('b');
      expect(state.kind === 'answered' && state.answer.sentences[0].text).toBe('Resposta B.');
      expect(store.announcement()).toBe(ANSWERED_ANNOUNCEMENT);
    });

    it(`drops a late earlier ${outcome} while the later answer is still loading`, async () => {
      void store.start(params('a'));
      void store.start(params('b'));
      if (outcome === 'success') api.calls[0].resolve(answered('Resposta A.', 1));
      else api.calls[0].reject(httpError(500));
      await flush();
      expect(store.state()).toEqual({ kind: 'loading', params: params('b'), retrying: false });
      expect(store.announcement()).toBe('');
    });

    it(`ignores a ${outcome} that arrives after clear`, async () => {
      void store.start(params('a'));
      store.clear();
      expect(api.calls[0].signal.aborted).toBe(true);
      expect(store.busy()).toBe(false);
      if (outcome === 'success') api.calls[0].resolve(answered('Resposta A.', 1));
      else api.calls[0].reject(httpError(500));
      await flush();
      expect(store.state()).toEqual({ kind: 'idle' });
      expect(store.announcement()).toBe('');
    });
  }

  it('shows the refusal for a refused answer', async () => {
    void store.start(params('a'));
    api.calls[0].resolve(refused());
    await flush();
    expect(store.state()).toEqual({ kind: 'refused', params: params('a') });
    expect(store.announcement()).toBe(REFUSED_ANNOUNCEMENT);
  });

  it('treats 503 as unavailable and other failures as errors', async () => {
    void store.start(params('a'));
    api.calls[0].reject(httpError(503));
    await flush();
    expect(store.state()).toEqual({ kind: 'unavailable', params: params('a') });
    expect(store.announcement()).toBe(UNAVAILABLE_ANNOUNCEMENT);

    void store.start(params('b'));
    api.calls[1].reject(httpError(0));
    await flush();
    expect(store.state()).toEqual({ kind: 'error', params: params('b') });
    expect(store.announcement()).toBe(ANSWER_ERROR_ANNOUNCEMENT);
  });

  it('retries the same search after a failure and recovers', async () => {
    void store.start(params('a'));
    api.calls[0].reject(httpError(500));
    await flush();

    const retry = store.retry();
    expect(store.state()).toEqual({ kind: 'loading', params: params('a'), retrying: true });
    expect(store.announcement()).toBe('');
    expect(api.calls[1].params).toEqual(params('a'));
    api.calls[1].resolve(answered('Pode deduzir.', 1));
    await retry;
    expect(store.state().kind).toBe('answered');
    expect(store.announcement()).toBe(ANSWERED_ANNOUNCEMENT);
  });

  it('keeps the error when the retry fails again, announcing it again', async () => {
    void store.start(params('a'));
    api.calls[0].reject(httpError(500));
    await flush();
    const retry = store.retry();
    api.calls[1].reject(httpError(500));
    await retry;
    expect(store.state()).toEqual({ kind: 'error', params: params('a') });
    expect(store.announcement()).toBe(ANSWER_ERROR_ANNOUNCEMENT);
  });

  it('does nothing on retry when idle', async () => {
    await store.retry();
    expect(api.calls).toHaveLength(0);
    expect(store.state()).toEqual({ kind: 'idle' });
  });
});

describe('shownAnswer', () => {
  it('drops sentences citing unknown ids or citations without an https link, and refuses when none is left', () => {
    const response: AnswerResponse = {
      ...answered('x'),
      sentences: [
        { text: 'Válida.', citations: [1] },
        { text: 'Id desconhecido.', citations: [9] },
        { text: 'Ligação http.', citations: [2] },
        { text: 'Ligação javascript.', citations: [3] },
        { text: 'Mista.', citations: [9, 1, 1] },
        { text: 'Sem fontes.', citations: [] },
        { text: '   ', citations: [1] },
      ],
      citations: [citation(1), citation(2, 'http://info.portaldasfinancas.gov.pt/2.pdf'), citation(3, 'javascript:alert(1)'), citation(4)],
    };
    const shown = shownAnswer(response)!;
    expect(shown.sentences).toEqual([
      { text: 'Válida.', citations: [1] },
      { text: 'Mista.', citations: [1] },
    ]);
    expect(shown.citations.map(c => c.id)).toEqual([1]);

    expect(shownAnswer({ ...response, sentences: [{ text: 'Ligação http.', citations: [2] }] })).toBeNull();
    expect(shownAnswer(refused())).toBeNull();
    expect(shownAnswer({ ...answered('Pode.', 1), status: 'other' as 'answered' })).toBeNull();
    expect(shownAnswer(null)).toBeNull();
  });
});
