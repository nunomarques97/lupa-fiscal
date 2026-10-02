import { Component, computed, input, signal } from '@angular/core';

import { SearchResult } from './api';
import { articleLabel, dateShort, sectionLabel } from './format';
import { passageView } from './passage';

let nextId = 0;

/**
 * One ranked passage with its citation: process number, article, date and section beside the quoted
 * passage on desktop, above it on mobile. Passage text and highlights are inserted as text only.
 */
@Component({
  selector: 'li[app-result-item]',
  host: { class: 'result', 'data-result': '', '[attr.data-ruling-id]': 'result().rulingId' },
  template: `
    <div class="cite">
      <span class="rank" aria-hidden="true">{{ rank() }}</span>
      <span class="proc">Processo {{ result().processNumber }}</span>
      <dl>
        <dt>Artigo</dt>
        <dd>{{ articleText() }}</dd>
        @if (result().date; as date) {
          <dt>Data</dt>
          <dd><time [attr.datetime]="date">{{ dateText() }}</time></dd>
        }
        <dt>Secção</dt>
        <dd>Secção: {{ section() }}</dd>
      </dl>
    </div>
    <div class="body">
      <h3 class="subject">{{ result().subject }}</h3>
      <blockquote class="passage" [id]="passageId" [attr.cite]="result().sourceUrl">@if (view().before) {<span aria-hidden="true">… </span>}@for (segment of view().segments; track $index) {@if (segment.mark) {<mark>{{ segment.text }}</mark>} @else {{{ segment.text }}}}@if (view().after) {<span aria-hidden="true"> …</span>}</blockquote>
      <div class="actions">
        <a [href]="result().sourceUrl" target="_blank" rel="noopener noreferrer">Abrir PDF oficial<span class="sr-only"> (abre num novo separador)</span></a>
        @if (cut()) {
          <button type="button" [attr.aria-expanded]="expanded()" [attr.aria-controls]="passageId" (click)="expanded.set(!expanded())">Ler passagem completa</button>
        }
      </div>
    </div>
  `,
})
export class ResultItem {
  readonly result = input.required<SearchResult>();
  readonly rank = input.required<number>();
  /** Excerpt length in characters (240 on mobile, 360 on desktop). */
  readonly excerptChars = input(360);

  protected readonly passageId = `passage-${++nextId}`;
  protected readonly expanded = signal(false);

  private readonly excerpt = computed(() => passageView(this.result().passage, this.result().highlights ?? [], this.excerptChars()));
  protected readonly cut = computed(() => this.excerpt().before || this.excerpt().after);
  protected readonly view = computed(() =>
    this.expanded() ? passageView(this.result().passage, this.result().highlights ?? [], null) : this.excerpt(),
  );

  protected readonly articleText = computed(() => {
    const { article, tax } = this.result();
    return article ? `Art. ${articleLabel(article)} ${tax}` : tax;
  });
  protected readonly dateText = computed(() => dateShort(this.result().date ?? ''));
  protected readonly section = computed(() => sectionLabel(this.result().section));
}
