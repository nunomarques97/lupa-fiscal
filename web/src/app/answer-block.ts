import { Component, ElementRef, Injector, afterNextRender, computed, inject, input } from '@angular/core';

import { AnswerCitation, SearchResult } from './api';
import { AnswerStore, REFUSAL_TEXT } from './answer-store';
import { resultAnchor } from './format';

/** A citation marker: a link to the cited result in the list or, when it is not listed, to its official PDF. */
interface Marker {
  id: number;
  processNumber: string;
  /** Rank of the cited ruling in the results list, or null when the list does not hold it. */
  rank: number | null;
  href: string;
}

/**
 * The answer to the current search, above the summary line and the results. Every sentence ends with
 * markers naming the process number of the passages it comes from; a marker moves focus to that result
 * in the list, whose citation links to the official PDF. Text is inserted as text only.
 */
@Component({
  selector: 'app-answer-block',
  template: `
    @switch (state().kind) {
      @case ('unavailable') {
        <p class="answer-quiet" tabindex="-1" data-answer data-state="unavailable" data-answer-focus>
          A resposta escrita não está disponível neste momento. Os excertos abaixo não são afetados.
        </p>
      }
      @default {
        <section
          class="answer"
          aria-labelledby="answer-title"
          [attr.aria-busy]="store.busy() ? 'true' : null"
          data-answer
          [attr.data-state]="state().kind"
        >
          <h2 class="answer-title" id="answer-title" tabindex="-1" data-answer-focus>Resposta gerada a partir das passagens citadas</h2>
          <p class="answer-note">Cada frase indica o processo de onde vem. Confirme sempre na informação vinculativa oficial.</p>

          @if (state().kind === 'loading' && !retrying()) {
            <p class="answer-status">A preparar a resposta…</p>
            <div class="answer-skeleton" aria-hidden="true">
              <div class="bar"></div>
              <div class="bar" style="width: 70%"></div>
            </div>
          }

          @if (state(); as current) {
            @if (current.kind === 'answered') {
              <p class="answer-text">
                @for (sentence of current.answer.sentences; track $index) {
                  <!-- Explicit spaces: whitespace between elements is removed from templates. -->
                  <span class="answer-sentence" data-answer-sentence>{{ sentence.text }}</span>{{ ' ' }}<span class="answer-marks">(@for (marker of markersOf(sentence.citations); track marker.id; let last = $last) {<a
                    class="answer-mark"
                    [href]="marker.href"
                    [attr.target]="marker.rank === null ? '_blank' : null"
                    [attr.rel]="marker.rank === null ? 'noopener noreferrer' : null"
                    [attr.data-citation]="marker.id"
                    (click)="follow($event, marker)"
                  >Processo {{ marker.processNumber }}@if (marker.rank !== null) {<span class="sr-only">, resultado {{ marker.rank }} da lista</span>} @else {<span class="sr-only">, abrir PDF oficial (abre num novo separador)</span>}</a>@if (!last) {; }})</span>{{ ' ' }}
                }
              </p>
            }
          }

          @if (state().kind === 'refused') {
            <p class="answer-refused">{{ refusal }}</p>
          }

          @if (state().kind === 'error' || retrying()) {
            <div class="answer-error" data-answer-error>
              <p><strong>Não foi possível preparar a resposta.</strong> Os resultados abaixo mantêm-se.</p>
              <button
                class="btn-quiet"
                type="button"
                [attr.aria-disabled]="retrying() ? 'true' : null"
                (click)="retrying() || retry()"
              >{{ retrying() ? 'A tentar de novo…' : 'Tentar de novo' }}</button>
            </div>
          }
        </section>
      }
    }
  `,
})
export class AnswerBlock {
  protected readonly store = inject(AnswerStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  /** The results shown for the same search, to link each citation to its item in the list. */
  readonly results = input.required<SearchResult[]>();

  protected readonly refusal = REFUSAL_TEXT;
  protected readonly state = this.store.state;
  protected readonly retrying = computed(() => {
    const state = this.state();
    return state.kind === 'loading' && state.retrying;
  });

  private readonly markers = computed(() => {
    const state = this.state();
    const byId = new Map<number, Marker>();
    if (state.kind !== 'answered') return byId;
    const ranks = new Map<string, number>();
    this.results().forEach((result, i) => {
      if (!ranks.has(result.rulingId)) ranks.set(result.rulingId, i + 1);
    });
    for (const citation of state.answer.citations) {
      if (!byId.has(citation.id)) byId.set(citation.id, marker(citation, ranks.get(citation.rulingId) ?? null));
    }
    return byId;
  });

  protected markersOf(ids: number[]): Marker[] {
    const markers = this.markers();
    return ids.flatMap(id => markers.get(id) ?? []);
  }

  /** A listed citation moves focus to its result (which scrolls it into view); otherwise the link opens the PDF. */
  protected follow(event: MouseEvent, marker: Marker): void {
    if (marker.rank === null) return;
    const target = document.getElementById(resultAnchor(marker.rank));
    if (!target) return;
    event.preventDefault();
    target.focus();
  }

  protected async retry(): Promise<void> {
    await this.store.retry();
    afterNextRender(
      () => {
        // The retry button is gone after the retry completes; keep focus inside the answer block.
        const active = document.activeElement;
        if (!active || active === document.body) this.host.nativeElement.querySelector<HTMLElement>('[data-answer-focus]')?.focus();
      },
      { injector: this.injector },
    );
  }
}

function marker(citation: AnswerCitation, rank: number | null): Marker {
  return { id: citation.id, processNumber: citation.processNumber, rank, href: rank === null ? citation.sourceUrl : `#${resultAnchor(rank)}` };
}
