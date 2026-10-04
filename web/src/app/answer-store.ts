import { Injectable, computed, inject, signal } from '@angular/core';

import { AnswerCitation, AnswerResponse, AnswerSentence, SearchApi, isUnavailable } from './api';
import { isHttpsUrl } from './format';
import { SearchParams } from './search-params';

/** Shown instead of an answer when no sentence is supported by a cited passage (the API's fixed refusal). */
export const REFUSAL_TEXT = 'Não encontrei nas informações vinculativas pesquisadas uma resposta suportada por fontes.';

export const ANSWERED_ANNOUNCEMENT = 'Resposta preparada a partir das passagens citadas.';
export const REFUSED_ANNOUNCEMENT = 'Sem resposta suportada pelas passagens encontradas.';
export const UNAVAILABLE_ANNOUNCEMENT = 'A resposta não está disponível neste momento.';
export const ANSWER_ERROR_ANNOUNCEMENT = 'Não foi possível preparar a resposta.';

/** An answer that passed the client-side checks: every sentence cites at least one shown citation. */
export interface ShownAnswer {
  sentences: AnswerSentence[];
  citations: AnswerCitation[];
}

/** What the answer block shows. Every state except idle belongs to one search with results. */
export type AnswerState =
  | { kind: 'idle' }
  | { kind: 'loading'; params: SearchParams; retrying: boolean }
  | { kind: 'answered'; params: SearchParams; answer: ShownAnswer }
  | { kind: 'refused'; params: SearchParams }
  | { kind: 'unavailable'; params: SearchParams }
  | { kind: 'error'; params: SearchParams };

/**
 * Owns the answer requests. The search store starts one when a search returns results and clears it
 * when another search starts or the page resets. As for searches, each run takes a new sequence number
 * and aborts the previous request, and a completion whose number is no longer current is dropped,
 * whether it succeeded or failed. The results are never touched from here.
 */
@Injectable({ providedIn: 'root' })
export class AnswerStore {
  private readonly api = inject(SearchApi);
  private sequence = 0;
  private controller: AbortController | null = null;

  private readonly current = signal<AnswerState>({ kind: 'idle' });
  private readonly liveText = signal('');

  readonly state = this.current.asReadonly();
  /** Text for the answer's own polite live region: set once when an answer completes, refuses or fails. */
  readonly announcement = this.liveText.asReadonly();
  readonly busy = computed(() => this.current().kind === 'loading');

  /** Requests the answer for a search that returned results. */
  start(params: SearchParams): Promise<void> {
    return this.run(params, false);
  }

  /** Requests the answer of the same search again (after an error or while it is unavailable). */
  retry(): Promise<void> {
    const state = this.current();
    return state.kind === 'idle' ? Promise.resolve() : this.run(state.params, true);
  }

  /** No answer; any pending request is abandoned. */
  clear(): void {
    this.sequence++;
    this.abortPending();
    this.liveText.set('');
    this.current.set({ kind: 'idle' });
  }

  private async run(params: SearchParams, retrying: boolean): Promise<void> {
    const id = ++this.sequence;
    this.abortPending();
    const controller = new AbortController();
    this.controller = controller;

    this.liveText.set('');
    this.current.set({ kind: 'loading', params, retrying });

    try {
      const response = await this.api.answer(params, controller.signal);
      if (id !== this.sequence) return;
      const answer = shownAnswer(response);
      if (answer) {
        this.current.set({ kind: 'answered', params, answer });
        this.liveText.set(ANSWERED_ANNOUNCEMENT);
      } else {
        this.current.set({ kind: 'refused', params });
        this.liveText.set(REFUSED_ANNOUNCEMENT);
      }
    } catch (error) {
      if (id !== this.sequence) return;
      const unavailable = isUnavailable(error);
      this.current.set(unavailable ? { kind: 'unavailable', params } : { kind: 'error', params });
      this.liveText.set(unavailable ? UNAVAILABLE_ANNOUNCEMENT : ANSWER_ERROR_ANNOUNCEMENT);
    } finally {
      if (this.controller === controller) this.controller = null;
    }
  }

  private abortPending(): void {
    this.controller?.abort();
    this.controller = null;
  }
}

/**
 * The part of an answer that may be shown, or null for a refusal. A citation without an https link to
 * its official document is dropped; a sentence keeps only the ids of shown citations and is dropped when
 * none is left; a citation no kept sentence cites is dropped. Anything other than "answered" is a refusal.
 */
export function shownAnswer(response: AnswerResponse | null | undefined): ShownAnswer | null {
  if (response?.status !== 'answered') return null;
  const citations = (response.citations ?? []).filter(c => Number.isInteger(c.id) && isHttpsUrl(c.sourceUrl));
  const ids = new Set(citations.map(c => c.id));
  const sentences = (response.sentences ?? [])
    .map(s => ({ text: (s.text ?? '').trim(), citations: [...new Set((s.citations ?? []).filter(id => ids.has(id)))] }))
    .filter(s => s.text && s.citations.length);
  if (!sentences.length) return null;
  const cited = new Set(sentences.flatMap(s => s.citations));
  return { sentences, citations: citations.filter(c => cited.has(c.id)) };
}
