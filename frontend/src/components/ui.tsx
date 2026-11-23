import { cloneElement, isValidElement, ReactElement, ReactNode, useEffect, useId, useRef, useState } from 'react';
import { ApiError, HistoryEntry, PersonRef } from '../api/types';

// ------------------------------------------------------------------ formatting

const dateFmt = new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
const timeFmt = new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
export const fmtDate = (s: string | null | undefined) => (s ? dateFmt.format(new Date(s.length === 10 ? s + 'T12:00:00Z' : s)) : '–');
const timeYearFmt = new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
/** Date and time; the year only when it is not this year. */
export const fmtTime = (s: string | null | undefined) => {
  if (!s) return '–';
  const d = new Date(s);
  return (d.getFullYear() === new Date().getFullYear() ? timeFmt : timeYearFmt).format(d);
};
/** "PendingManager" → "Pending manager". */
export const human = (s: string) => {
  const t = s.replace(/(?!^)([A-Z])/g, ' $1').toLowerCase();
  return t.charAt(0).toUpperCase() + t.slice(1);
};
export const actionLabel: Record<string, string> = {
  submit: 'Submit', approve: 'Approve', reject: 'Reject', cancel: 'Cancel request', provision: 'Provision now', revoke: 'Revoke access',
  activate: 'Activate', 'start-leave': 'Start leave', return: 'Return from leave', 'give-notice': 'Give notice', 'withdraw-notice': 'Withdraw notice', leave: 'Offboard now',
  start: 'Start', complete: 'Record result', waive: 'Waive',
  take: 'Take ticket', hold: 'Put on hold', resume: 'Resume', resolve: 'Resolve', reopen: 'Reopen', close: 'Confirm and close',
};
/** Actions that need a comment (the API rejects them without one). */
export const needsComment = new Set(['reject', 'revoke', 'waive', 'hold', 'resolve']);

// ------------------------------------------------------------------ small pieces

const tone: Record<string, string> = {
  Active: 'good', Completed: 'good', Closed: 'quiet', Resolved: 'good', Approved: 'good',
  PendingManager: 'wait', PendingOwner: 'wait', PendingSecurity: 'wait', Submitted: 'wait', Assigned: 'wait', InProgress: 'wait', New: 'wait', OnHold: 'wait', Preboarding: 'wait',
  Rejected: 'bad', Overdue: 'bad', Expired: 'bad', Revoked: 'bad', Left: 'quiet', Offboarding: 'warn', OnLeave: 'warn', Cancelled: 'quiet', Waived: 'quiet', Draft: 'quiet',
};

export function Status({ value }: { value: string }) {
  return <span className={`status s-${tone[value] ?? 'quiet'}`}>{human(value)}</span>;
}

export function Priority({ value }: { value: string }) {
  return <span className={`prio p-${value.toLowerCase()}`}>{value}</span>;
}

export function Person({ p }: { p: PersonRef | null | undefined }) {
  return p ? <a href={`#/employees/${p.id}`}>{p.name}</a> : <span className="muted">–</span>;
}

export function PageHead({ title, intro, actions }: { title: string; intro?: ReactNode; actions?: ReactNode }) {
  return (
    <header className="page-head">
      <div>
        <h1>{title}</h1>
        {intro && <p className="intro">{intro}</p>}
      </div>
      {actions && <div className="head-actions">{actions}</div>}
    </header>
  );
}

export function Tabs({ value, options, onChange }: { value: string; options: [string, string, number?][]; onChange(v: string): void }) {
  return (
    <div className="tabs" role="tablist">
      {options.map(([v, label, count]) => (
        <button key={v} type="button" role="tab" aria-selected={value === v} className={value === v ? 'on' : ''} onClick={() => onChange(v)}>
          {label}
          {count ? <span className="count">{count}</span> : null}
        </button>
      ))}
    </div>
  );
}

export function Empty({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="empty">
      <strong>{title}</strong>
      {children && <p>{children}</p>}
    </div>
  );
}

export function Failure({ error }: { error: ApiError }) {
  return (
    <div className="failure" role="alert">
      <strong>{error.status === 403 ? 'You do not have access to this.' : error.status === 404 ? 'Not found.' : 'This did not load.'}</strong>
      <p>{error.message}</p>
    </div>
  );
}

export function Loading() {
  return <div className="loading" aria-busy="true">Loading…</div>;
}

/**
 * A labelled form control. The label is tied to the control by id and the hint / error by aria-describedby,
 * so screen readers (and tests) get the label alone as the control's name.
 */
export function Field({ label, hint, error, children }: { label: string; hint?: string; error?: string; children: ReactNode }) {
  const id = useId();
  const note = error ?? hint;
  const control = isValidElement(children)
    ? cloneElement(children as ReactElement<Record<string, unknown>>, { id, 'aria-describedby': note ? `${id}-note` : undefined, 'aria-invalid': error ? true : undefined })
    : children;
  return (
    <div className={`field${error ? ' invalid' : ''}`}>
      <label className="label" htmlFor={id}>{label}</label>
      {control}
      {note && <span id={`${id}-note`} className={error ? 'err' : 'hint'}>{note}</span>}
    </div>
  );
}

export function Pager({ page, size, total, onPage }: { page: number; size: number; total: number; onPage(p: number): void }) {
  const pages = Math.max(1, Math.ceil(total / size));
  if (total <= size) return <p className="pager-info">{total} {total === 1 ? 'row' : 'rows'}</p>;
  return (
    <nav className="pager" aria-label="Pages">
      <span className="pager-info">{(page - 1) * size + 1}–{Math.min(page * size, total)} of {total}</span>
      <button type="button" className="btn small" disabled={page <= 1} onClick={() => onPage(page - 1)}>Previous</button>
      <span>Page {page} of {pages}</span>
      <button type="button" className="btn small" disabled={page >= pages} onClick={() => onPage(page + 1)}>Next</button>
    </nav>
  );
}

export function SortHead({ label, field, sort, desc, onSort }: { label: string; field: string; sort: string; desc: boolean; onSort(f: string): void }) {
  const on = sort === field;
  return (
    <th aria-sort={on ? (desc ? 'descending' : 'ascending') : 'none'}>
      <button type="button" className="sort" onClick={() => onSort(field)}>
        {label}
        <span aria-hidden="true">{on ? (desc ? ' ▾' : ' ▴') : ''}</span>
      </button>
    </th>
  );
}

/** Debounced value (search boxes). */
export function useDebounced<T>(value: T, ms = 250): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}

// ------------------------------------------------------------------ dialog for comments, scores, dates

export interface Ask { title: string; label: string; kind: 'text' | 'number' | 'date'; required: boolean; confirm: string }

export function AskDialog({ ask, onDone }: { ask: Ask; onDone(value: string | null): void }) {
  const [value, setValue] = useState('');
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const d = ref.current;
    if (d && !d.open) d.showModal();
  }, []);
  const ok = !ask.required || value.trim().length > 0;
  return (
    <dialog ref={ref} className="dialog" onClose={() => onDone(null)} aria-labelledby="ask-title">
      <form method="dialog" onSubmit={(e) => { e.preventDefault(); if (ok) onDone(value.trim()); }}>
        <h2 id="ask-title">{ask.title}</h2>
        <Field label={ask.label}>
          {ask.kind === 'text'
            ? <textarea rows={3} value={value} autoFocus onChange={(e) => setValue(e.target.value)} />
            : <input type={ask.kind} value={value} autoFocus min={ask.kind === 'number' ? 0 : undefined} max={ask.kind === 'number' ? 100 : undefined} onChange={(e) => setValue(e.target.value)} />}
        </Field>
        <div className="dialog-actions">
          <button type="button" className="btn" onClick={() => onDone(null)}>Back</button>
          <button type="submit" className="btn primary" disabled={!ok}>{ask.confirm}</button>
        </div>
      </form>
    </dialog>
  );
}

/** Buttons for the actions the current state allows; asks for a comment / score / date where needed. */
export function ActionBar({ actions, onAction, extra }: { actions: string[]; onAction(action: string, value: string | null): Promise<void>; extra?: Record<string, Partial<Ask>> }) {
  const [asking, setAsking] = useState<{ action: string; ask: Ask } | null>(null);
  const [busy, setBusy] = useState(false);
  if (!actions.length) return null;
  const run = async (action: string, value: string | null) => {
    setBusy(true);
    try { await onAction(action, value); } finally { setBusy(false); }
  };
  const click = (action: string) => {
    const custom = extra?.[action];
    if (custom || needsComment.has(action)) {
      setAsking({
        action,
        ask: { title: actionLabel[action] ?? human(action), label: 'Comment', kind: 'text', required: needsComment.has(action), confirm: actionLabel[action] ?? human(action), ...custom },
      });
    } else void run(action, null);
  };
  const danger = new Set(['reject', 'revoke', 'cancel', 'leave']);
  return (
    <div className="action-bar">
      {actions.map((a) => (
        <button key={a} type="button" disabled={busy} className={`btn ${danger.has(a) ? 'danger' : a === actions.find((x) => !danger.has(x)) ? 'primary' : ''}`} onClick={() => click(a)}>
          {actionLabel[a] ?? human(a)}
        </button>
      ))}
      {asking && <AskDialog ask={asking.ask} onDone={(v) => { const a = asking.action; setAsking(null); if (v !== null) void run(a, v); }} />}
    </div>
  );
}

// ------------------------------------------------------------------ the workflow rail and history

/**
 * The state machine of a workflow, drawn as a rail of stations: done, current (with the possible actions
 * attached) and still ahead. End states that leave the happy path are shown as a branch.
 */
export function StateRail({ steps, current, outcome, children }: { steps: string[]; current: number; outcome?: { label: string; tone: string } | null; children?: ReactNode }) {
  return (
    <div className="rail" aria-label="Workflow">
      <ol>
        {steps.map((s, i) => {
          const state = outcome ? (i <= Math.max(current, 0) ? 'done' : 'skipped') : i < current ? 'done' : i === current ? 'here' : 'ahead';
          return (
            <li key={s} className={state} aria-current={state === 'here' ? 'step' : undefined}>
              <span className="dot" aria-hidden="true" />
              <span className="name">{s}</span>
            </li>
          );
        })}
      </ol>
      {outcome && <p className={`rail-outcome s-${outcome.tone}`}>{outcome.label}</p>}
      {children && <div className="rail-actions">{children}</div>}
    </div>
  );
}

export function History({ items }: { items: HistoryEntry[] }) {
  if (!items.length) return null;
  return (
    <ol className="history">
      {items.map((h, i) => (
        <li key={i}>
          <time dateTime={h.at}>{fmtTime(h.at)}</time>
          <div>
            <strong>{h.actor}</strong> {verb(h)}
            {h.comment && <q>{h.comment}</q>}
          </div>
        </li>
      ))}
    </ol>
  );
}

function verb(h: HistoryEntry) {
  const to = h.to ? human(h.to).toLowerCase() : '';
  const map: Record<string, string> = {
    create: 'created the request', submit: `submitted it (${to})`, approve: `approved (${to})`, reject: 'rejected it', cancel: 'cancelled it',
    provision: 'provisioned the access', revoke: 'revoked the access', expire: 'let it expire', hire: `registered the hire (${to})`, open: 'opened the ticket',
    assign: 'assigned the course',
  };
  return map[h.action] ?? `${h.action.replace(/-/g, ' ')} → ${to}`;
}

export function Toasts({ items }: { items: { id: number; text: string; kind: string }[] }) {
  return (
    <div className="toasts" role="status" aria-live="polite">
      {items.map((t) => <div key={t.id} className={`toast ${t.kind}`}>{t.text}</div>)}
    </div>
  );
}

export function download(fileName: string, contentType: string, bytes: Uint8Array) {
  const url = URL.createObjectURL(new Blob([bytes as BlobPart], { type: contentType }));
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
