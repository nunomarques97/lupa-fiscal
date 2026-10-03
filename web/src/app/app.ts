import {
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { FacetsResponse, SearchApi } from './api';
import { FilterFields } from './filter-fields';
import { articlePhrase, rulingCount, taxLabel } from './format';
import { ResultItem } from './result-item';
import { MAX_QUERY_LENGTH, NO_FILTERS, Filters, SearchParams, paramsFromUrl, paramsToUrl, sameParams } from './search-params';
import { SearchStore } from './search-store';

export const VALIDATION_MESSAGE = 'Escreva uma pergunta para pesquisar.';

const NARROW = '(max-width: 720px)';

@Component({
  selector: 'app-root',
  imports: [FilterFields, ResultItem, RouterLink],
  templateUrl: './app.html',
})
export class App {
  protected readonly store = inject(SearchStore);
  private readonly api = inject(SearchApi);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);

  protected readonly maxQueryLength = MAX_QUERY_LENGTH;
  protected readonly validationMessage = VALIDATION_MESSAGE;
  protected readonly examples = [
    'Vendi a casa onde morava. Tenho de pagar mais-valias?',
    'Passo recibos verdes e trabalho a partir de casa. Que despesas posso deduzir?',
    'O meu filho tem 26 anos e ainda estuda. Continua a ser dependente?',
  ];

  /** The question box as typed (may differ from the submitted search). */
  protected readonly query = signal('');
  /** The filter controls as set. */
  protected readonly filters = signal<Filters>({ ...NO_FILTERS });
  /** Filters being edited in the mobile sheet, applied only with "Aplicar filtros". */
  protected readonly draft = signal<Filters>({ ...NO_FILTERS });
  protected readonly invalid = signal(false);
  protected readonly facets = signal<FacetsResponse | null>(null);
  protected readonly narrow = signal(false);

  private readonly queryInput = viewChild.required<ElementRef<HTMLInputElement>>('queryInput');
  private readonly summary = viewChild<ElementRef<HTMLElement>>('summary');
  private readonly sheet = viewChild.required<ElementRef<HTMLDialogElement>>('sheet');
  private readonly sheetToggle = viewChild.required<ElementRef<HTMLButtonElement>>('sheetToggle');

  protected readonly state = this.store.state;
  protected readonly retrying = computed(() => {
    const state = this.state();
    return state.kind === 'loading' && state.retrying;
  });
  /** The error block also stays while a retry runs, so focus can stay on "Tentar de novo". */
  protected readonly showError = computed(() => this.state().kind === 'error' || this.retrying());
  protected readonly summaryText = computed(() => {
    const state = this.state();
    switch (state.kind) {
      case 'loading':
        return 'A pesquisar…';
      case 'results':
        return `${rulingCount(state.response.results.length)}, por proximidade`;
      case 'empty':
        return rulingCount(0);
      case 'error':
        return 'Pesquisa interrompida';
      default:
        return '';
    }
  });
  protected readonly tookText = computed(() => {
    const state = this.state();
    return state.kind === 'results' ? `${Math.round(state.response.tookMs)} ms` : '';
  });
  protected readonly activeFilterCount = computed(() => Object.values(this.filters()).filter(Boolean).length);
  protected readonly activeFilterSummary = computed(() => {
    const { tax, article, year } = this.filters();
    const parts = [tax ? taxLabel(tax) : '', article ? articlePhrase(article) : '', year].filter(Boolean);
    return parts.length ? parts.join(' · ') : 'Sem filtros';
  });

  constructor() {
    const route = inject(ActivatedRoute);
    route.queryParamMap.pipe(takeUntilDestroyed()).subscribe(map => {
      this.restore(paramsFromUrl(map));
      // An article without a tax is dropped (article numbers belong to one tax code); so is its URL parameter.
      if (map.has('article') && !this.filters().article) {
        void this.router.navigate([], { queryParams: { article: null }, queryParamsHandling: 'merge', replaceUrl: true });
      }
    });
    void this.loadFacets();

    const media = typeof matchMedia === 'function' ? matchMedia(NARROW) : null;
    if (media) {
      this.narrow.set(media.matches);
      const listener = (event: MediaQueryListEvent) => this.narrow.set(event.matches);
      media.addEventListener('change', listener);
      inject(DestroyRef).onDestroy(() => media.removeEventListener('change', listener));
    }
  }

  protected onInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.query.set(value);
    if (this.invalid() && value.trim()) this.invalid.set(false);
  }

  protected submit(event: Event): void {
    event.preventDefault();
    this.searchWith(this.filters(), true);
  }

  protected onFilterChange(next: Filters): void {
    this.filters.set(next);
    // A filter change searches again when there is a question to search for.
    if (this.query().trim()) this.searchWith(next, false);
  }

  protected clearFilters(): void {
    this.onFilterChange({ ...NO_FILTERS });
  }

  /** "Limpar filtros" in the empty state: the button disappears, so focus goes to the question box. */
  protected clearFiltersFromEmpty(): void {
    this.queryInput().nativeElement.focus();
    this.clearFilters();
  }

  protected async retry(): Promise<void> {
    await this.store.retry();
    afterNextRender(
      () => {
        // The retry button is gone after a successful retry; keep focus inside the results region.
        const active = document.activeElement;
        if (!active || active === document.body) this.summary()?.nativeElement.focus();
      },
      { injector: this.injector },
    );
  }

  protected exampleClicked(): void {
    // The example link is replaced by the results; the question box receives focus instead.
    this.queryInput().nativeElement.focus();
  }

  protected openSheet(): void {
    this.draft.set(this.filters());
    const dialog = this.sheet().nativeElement;
    dialog.showModal();
    dialog.querySelector('select')?.focus();
  }

  protected applySheet(): void {
    const next = this.draft();
    this.sheet().nativeElement.close();
    this.onFilterChange(next);
  }

  protected sheetClosed(): void {
    this.sheetToggle().nativeElement.focus();
  }

  protected sheetClicked(event: MouseEvent): void {
    // A click on the backdrop (the dialog element itself, outside its content) closes the sheet.
    if (event.target === this.sheet().nativeElement) this.sheet().nativeElement.close();
  }

  private searchWith(filters: Filters, fromSubmit: boolean): void {
    const q = this.query().trim();
    if (!q) {
      if (fromSubmit) {
        this.invalid.set(true);
        this.store.announce(VALIDATION_MESSAGE);
      }
      return;
    }
    this.invalid.set(false);
    if (!this.facets()) void this.loadFacets();
    const params: SearchParams = { q: q.slice(0, MAX_QUERY_LENGTH), ...filters };
    void this.store.search(params);
    void this.router.navigate([], { queryParams: paramsToUrl(params) });
  }

  /** Applies a search from the URL (first load, shared link, back and forward). */
  private restore(params: SearchParams): void {
    if (sameParams(params, this.store.submitted())) return;
    this.query.set(params.q);
    this.filters.set({ tax: params.tax, article: params.article, year: params.year });
    this.invalid.set(false);
    if (params.q) void this.store.search(params);
    else this.store.reset();
  }

  private async loadFacets(): Promise<void> {
    try {
      this.facets.set(await this.api.facets());
    } catch {
      // The filters keep "Todos" and any value from the URL; facets are requested again on the next search.
    }
  }
}
