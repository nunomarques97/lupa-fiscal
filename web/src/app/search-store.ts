import { Injectable, computed, inject, signal } from '@angular/core';

import { SearchApi, SearchResponse } from './api';
import { foundAnnouncement, isHttpsUrl } from './format';
import { SearchParams } from './search-params';

export const ERROR_ANNOUNCEMENT = 'Não foi possível pesquisar.';

/** What the results region shows. Every state except initial belongs to one submitted search. */
export type SearchState =
  | { kind: 'initial' }
  | { kind: 'loading'; params: SearchParams; retrying: boolean }
  | { kind: 'results'; params: SearchParams; response: SearchResponse }
  | { kind: 'empty'; params: SearchParams; response: SearchResponse }
  | { kind: 'error'; params: SearchParams };

/**
 * Owns the search requests. Only the latest submitted search may change the state: each run takes
 * a new sequence number and aborts the previous request, and a completion whose number is no longer
 * current is dropped, whether it succeeded or failed.
 */
@Injectable({ providedIn: 'root' })
export class SearchStore {
  private readonly api = inject(SearchApi);
  private sequence = 0;
  private controller: AbortController | null = null;

  private readonly current = signal<SearchState>({ kind: 'initial' });
  private readonly lastSubmitted = signal<SearchParams | null>(null);
  private readonly liveText = signal('');

  readonly state = this.current.asReadonly();
  /** The last submitted search, also while it is still loading. Retry re-runs exactly this. */
  readonly submitted = this.lastSubmitted.asReadonly();
  /** Text for the polite live region: set once when a search completes, cleared when one starts. */
  readonly announcement = this.liveText.asReadonly();
  readonly busy = computed(() => this.current().kind === 'loading');

  search(params: SearchParams): Promise<void> {
    return this.run(params, false);
  }

  /** Re-runs the last submitted search (not unsubmitted edits of the form). */
  retry(): Promise<void> {
    const params = this.lastSubmitted();
    return params ? this.run(params, true) : Promise.resolve();
  }

  /** Back to the initial state; any pending request is abandoned. */
  reset(): void {
    this.sequence++;
    this.abortPending();
    this.lastSubmitted.set(null);
    this.liveText.set('');
    this.current.set({ kind: 'initial' });
  }

  /** Announces a message that is not the result of a search, such as a validation error. */
  announce(text: string): void {
    this.liveText.set(text);
  }

  private async run(params: SearchParams, retrying: boolean): Promise<void> {
    const id = ++this.sequence;
    this.abortPending();
    const controller = new AbortController();
    this.controller = controller;

    this.lastSubmitted.set(params);
    this.liveText.set('');
    this.current.set({ kind: 'loading', params, retrying });

    try {
      const response = await this.api.search(params, controller.signal);
      if (id !== this.sequence) return;
      // A result without an https link to its official document is never shown.
      const results = (response.results ?? []).filter(r => isHttpsUrl(r.sourceUrl));
      const shown = { ...response, results };
      this.current.set(results.length ? { kind: 'results', params, response: shown } : { kind: 'empty', params, response: shown });
      this.liveText.set(foundAnnouncement(results.length));
    } catch {
      if (id !== this.sequence) return;
      this.current.set({ kind: 'error', params });
      this.liveText.set(ERROR_ANNOUNCEMENT);
    } finally {
      if (this.controller === controller) this.controller = null;
    }
  }

  private abortPending(): void {
    this.controller?.abort();
    this.controller = null;
  }
}
