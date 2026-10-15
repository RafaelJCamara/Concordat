import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { HlmAlertImports } from '@spartan-ng/helm/alert';
import { HlmButton } from '@spartan-ng/helm/button';
import { ChangeConfirmation } from '../../../shared/ui/change-confirmation/change-confirmation';

const INPUT =
  'border-input bg-background focus-visible:ring-ring flex h-9 w-full rounded-md border px-3 py-1 font-mono text-sm focus-visible:ring-1 focus-visible:outline-none';

/**
 * Ending a subject's life: retiring it, and then, only once it is retired, deleting it for good
 * (ADR-046).
 *
 * <b>One panel with one next step.</b> A subject that is not retired is offered retiring and nothing
 * else, because the registry refuses to delete it (`subject_not_retired`) and a button that cannot
 * work is a button that gets a support ticket. A retired one is offered the deletion.
 *
 * <b>Every sentence in the two confirmations was measured against the registry</b>, not read off its
 * code: what a retirement keeps and refuses (`DELETE` answers the subject as `RETIRED`; a version
 * registered afterwards is `409 subject_retired`; owner, policy and reviewers refuse a retired
 * subject), what a deletion removes and what stops it (the .NET suite and `e2e/subject-purge.spec.ts`
 * pin each refusal and the 404 a schema answers afterwards), and what each writes to the audit log.
 * A registry that starts doing more or less has to change this text with it.
 *
 * <b>Deleting asks for the subject's name.</b> The second click is not enough for something that cannot
 * be undone and frees a name: it takes the name typed out, so that the thing being deleted is the thing
 * the person is looking at and not the one in the next tab.
 *
 * <b>It sends nothing itself</b>, like the other editors: it reports what was confirmed, and what the
 * registry answered comes back in through `error` and `saving`. Whether to render it at all (the
 * `subject:admin` scope, ADR-018) is the page's.
 */
@Component({
  selector: 'cd-subject-lifecycle-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmAlertImports, HlmButton, ChangeConfirmation],
  host: { class: 'block' },
  template: `
    <section
      class="border-border bg-card space-y-4 rounded-xl border p-5"
      aria-labelledby="subject-lifecycle-title"
    >
      <div>
        <h2 id="subject-lifecycle-title" class="text-foreground text-base font-semibold">
          {{ retired() ? 'Delete this subject' : 'Retire this subject' }}
        </h2>
        <p class="text-muted-foreground mt-1 text-sm">
          @if (retired()) {
            <span class="font-mono break-words">{{ subject() }}</span> is retired. Deleting it for
            good removes it, with every version it holds, and cannot be undone.
          } @else {
            Retiring <span class="font-mono break-words">{{ subject() }}</span> ends it: it stops
            accepting versions, for good. It is the step before it can be deleted.
          }
        </p>
      </div>

      @if (error(); as failure) {
        <div hlmAlert variant="destructive" role="alert">
          <h3 hlmAlertTitle>
            {{ retired() ? 'The registry did not delete it' : 'The registry did not retire it' }}
          </h3>
          <p hlmAlertDescription>{{ failure }}</p>
        </div>
      }

      @if (confirming()) {
        @if (retired()) {
          <cd-change-confirmation
            [heading]="'Delete ' + subject() + ' for good?'"
            confirmLabel="Yes, delete it for good"
            busyLabel="Deleting…"
            declineLabel="Keep it"
            [busy]="saving()"
            [confirmDisabled]="!nameMatches()"
            (confirmed)="purge.emit()"
            (declined)="close()"
          >
            <li>
              <span class="text-foreground"
                >It removes the subject and its {{ versions() }}
                {{ versions() === 1 ? 'version' : 'versions' }}.</span
              >
              There is no undo and no grace period.
            </li>
            <li>
              The registry refuses while a contract binds it, a registered service declares it, or a
              schema used by another subject's version refers to it, and says which. Remove those
              first.
            </li>
            <li>
              Schemas are never deleted, but one that only this subject used can no longer be
              fetched by its id afterwards.
            </li>
            <li>
              The name is free as soon as it is gone: a new subject called
              <span class="font-mono break-words">{{ subject() }}</span> starts again at version 1.
            </li>
            <li>
              The audit log keeps <span class="font-mono">SUBJECT_PURGED</span>, naming the account
              that did it and how many versions went, beside the entries the subject already has.
              Nobody is notified.
            </li>
            <li class="list-none ps-0">
              <label
                class="text-foreground mt-2 block text-sm font-medium"
                for="subject-purge-name"
              >
                Type <span class="font-mono break-words">{{ subject() }}</span> to confirm
              </label>
              <input
                id="subject-purge-name"
                type="text"
                autocomplete="off"
                autocapitalize="off"
                spellcheck="false"
                [class]="'mt-1.5 ' + inputClass"
                [value]="typed()"
                (input)="typed.set(value($event))"
              />
            </li>
          </cd-change-confirmation>
        } @else {
          <cd-change-confirmation
            [heading]="'Retire ' + subject() + '?'"
            confirmLabel="Yes, retire it"
            busyLabel="Retiring…"
            declineLabel="Keep it active"
            [busy]="saving()"
            (confirmed)="retire.emit()"
            (declined)="close()"
          >
            <li>
              <span class="text-foreground">It cannot be undone.</span> A retired subject accepts no
              new version, and its owner, compatibility policy and reviewers can no longer be
              changed.
            </li>
            <li>
              It keeps its name and every version it has, and they can still be read. Nothing is
              deleted, and the name stays taken.
            </li>
            <li>
              The audit log gets one entry, <span class="font-mono">SUBJECT_RETIRED</span>, naming
              the account that did it. Nobody is notified.
            </li>
          </cd-change-confirmation>
        }
      } @else {
        <button hlmBtn type="button" variant="outline" (click)="confirming.set(true)">
          {{ retired() ? 'Delete for good…' : 'Retire subject…' }}
        </button>
      }
    </section>
  `,
})
export class SubjectLifecyclePanel {
  /** The subject, by name. */
  readonly subject = input.required<string>();

  /** Its lifecycle, as the registry says it (`ACTIVE`, `DEPRECATED`, `RETIRED`). */
  readonly lifecycle = input.required<string>();

  /** How many versions it holds, so a deletion says how many go. */
  readonly versions = input.required<number>();

  /** The registry's refusal of the last attempt, in its own words, or null. */
  readonly error = input<string | null>(null);

  /** Whether the registry has not answered yet. */
  readonly saving = input(false);

  /** The retirement was confirmed. */
  readonly retire = output();

  /** The deletion was confirmed, with the name typed. */
  readonly purge = output();

  protected readonly inputClass = INPUT;
  protected readonly confirming = signal(false);
  protected readonly typed = signal('');

  protected readonly retired = computed(() => this.lifecycle() === 'RETIRED');

  /**
   * Whether what was typed is the name, exactly. Not trimmed and not folded: subject names are
   * case-sensitive, and a confirmation that accepted `ACME.Orders` for `acme.orders` would be
   * confirming a different subject.
   */
  protected readonly nameMatches = computed(() => this.typed() === this.subject());

  constructor() {
    // A confirmation belongs to the state it was opened in. Once the registry has retired the subject
    // the panel is offering the next step, and a retirement confirmation left open would turn into a
    // deletion one on the same screen, one click from the thing it has not asked about. The same goes
    // for a different subject arriving in the same page.
    effect(() => {
      this.lifecycle();
      this.subject();

      untracked(() => this.close());
    });
  }

  protected close(): void {
    this.confirming.set(false);
    this.typed.set('');
  }

  protected value(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }
}
