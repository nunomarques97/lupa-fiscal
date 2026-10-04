import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { SearchParams } from './search-params';

// JSON shapes of the local API (see src/LupaFiscal.Api/ApiContracts.cs). Text is plain: highlights
// are UTF-16 offsets into the passage, never markup.

export interface Highlight {
  start: number;
  length: number;
}

export interface SearchResult {
  rulingId: string;
  processNumber: string;
  tax: string;
  article: string;
  date: string | null;
  subject: string;
  section: string;
  passage: string;
  highlights: Highlight[];
  sourceUrl: string;
  score: number;
}

export interface SearchResponse {
  query: string;
  tookMs: number;
  results: SearchResult[];
}

export interface FacetValue<T = string> {
  value: T;
  count: number;
}

/** An article within one tax: article numbers of different codes are never merged. */
export interface ArticleFacetValue extends FacetValue {
  tax: string;
}

export interface FacetsResponse {
  taxes: FacetValue[];
  articles: ArticleFacetValue[];
  years: FacetValue<number>[];
}

/** One sentence of an answer, without its markers, and the ids of the citations it rests on. */
export interface AnswerSentence {
  text: string;
  citations: number[];
}

/** A cited passage: the ruling passage the answer was written from, with its official PDF. */
export interface AnswerCitation {
  id: number;
  rulingId: string;
  processNumber: string;
  tax: string;
  article: string;
  date: string | null;
  subject: string;
  section: string;
  passage: string;
  sourceUrl: string;
}

/** GET /api/answer: "answered" with cited sentences, or "refused" with the fixed refusal and no citations. */
export interface AnswerResponse {
  query: string;
  status: 'answered' | 'refused';
  sentences: AnswerSentence[];
  citations: AnswerCitation[];
  model: string;
  firstTokenMs: number;
  tookMs: number;
}

/** Error raised when a request is abandoned because a newer one replaced it. */
export class AbortedError extends Error {
  constructor() {
    super('The request was superseded.');
    this.name = 'AbortedError';
  }
}

@Injectable({ providedIn: 'root' })
export class SearchApi {
  private readonly http = inject(HttpClient);

  /** GET /api/search. Aborting the signal cancels the HTTP request and rejects with AbortedError. */
  search(params: SearchParams, signal: AbortSignal): Promise<SearchResponse> {
    return toPromise(this.http.get<SearchResponse>('/api/search', { params: queryOf(params) }), signal);
  }

  /**
   * GET /api/answer with the same parameters as the search. Aborting the signal cancels the request (and
   * the generation on the server) and rejects with AbortedError.
   */
  answer(params: SearchParams, signal: AbortSignal): Promise<AnswerResponse> {
    return toPromise(this.http.get<AnswerResponse>('/api/answer', { params: queryOf(params) }), signal);
  }

  /** GET /api/facets. */
  facets(): Promise<FacetsResponse> {
    return toPromise(this.http.get<FacetsResponse>('/api/facets'));
  }
}

/** True for a 503 response: the answer model is absent, still loading or busy with another question. */
export function isUnavailable(error: unknown): boolean {
  return error instanceof HttpErrorResponse && error.status === 503;
}

function queryOf(params: SearchParams): HttpParams {
  let query = new HttpParams().set('q', params.q);
  if (params.tax) query = query.set('tax', params.tax);
  if (params.article) query = query.set('article', params.article);
  if (params.year) query = query.set('year', params.year);
  return query;
}

function toPromise<T>(source: Observable<T>, signal?: AbortSignal): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    if (signal?.aborted) {
      reject(new AbortedError());
      return;
    }
    const subscription = source.subscribe({ next: resolve, error: reject });
    signal?.addEventListener(
      'abort',
      () => {
        subscription.unsubscribe();
        reject(new AbortedError());
      },
      { once: true },
    );
  });
}
