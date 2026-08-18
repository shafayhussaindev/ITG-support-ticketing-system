import s from './TicketBadges.module.css';

/*
  Status and priority are shown with distinct visual languages on purpose.

  Priority is a four-step ramp, so it reads as a scale — colour intensity carries the
  ordering. Status is categorical, so it uses a dot plus a neutral chip rather than a
  colour ramp that would imply an order the statuses do not have.

  Both carry text, never colour alone: about one in twelve men has a colour-vision
  deficiency, and a red-versus-amber chip is exactly the pairing they cannot separate.
*/

const PRIORITY_LABELS = {
  Critical: 'Critical',
  High: 'High',
  Medium: 'Medium',
  Low: 'Low',
};

export function PriorityBadge({ priority }) {
  const key = PRIORITY_LABELS[priority] ? priority : 'Medium';

  return (
    <span className={`${s.priority} ${s[`p${key}`]}`} title={`Priority: ${key}`}>
      <span className={s.bar} aria-hidden="true" />
      {PRIORITY_LABELS[key]}
    </span>
  );
}

const STATUS_TONE = {
  New: 'new',
  Assigned: 'assigned',
  InProgress: 'progress',
  WaitingForRequester: 'waiting',
  WaitingForThirdParty: 'waiting',
  Escalated: 'escalated',
  Resolved: 'resolved',
  Closed: 'closed',
  Reopened: 'reopened',
  Cancelled: 'cancelled',
};

/** Turns PascalCase status names into readable text. */
export function humanizeStatus(status = '') {
  return status.replace(/([a-z])([A-Z])/g, '$1 $2');
}

export function StatusBadge({ status }) {
  const tone = STATUS_TONE[status] ?? 'new';

  return (
    <span className={`${s.status} ${s[`s${tone}`]}`}>
      <span className={s.dot} aria-hidden="true" />
      {humanizeStatus(status)}
    </span>
  );
}

export function TypeBadge({ type }) {
  return <span className={s.type}>{humanizeStatus(type)}</span>;
}
