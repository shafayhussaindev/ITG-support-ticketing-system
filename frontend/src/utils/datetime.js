/*
  The server stores and returns UTC. Everything shown to a user is rendered in their
  own locale and zone by the browser, so a Karachi agent and a London manager reading
  the same ticket each see their own local time without the server knowing either.
*/

const UNITS = [
  ['year', 31_536_000_000],
  ['month', 2_592_000_000],
  ['week', 604_800_000],
  ['day', 86_400_000],
  ['hour', 3_600_000],
  ['minute', 60_000],
];

const relative = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

/** "3 hours ago", "in 2 days". Falls back to an absolute date beyond a year. */
export function formatRelative(utcValue) {
  if (!utcValue) {
    return '—';
  }

  const then = new Date(utcValue).getTime();

  if (Number.isNaN(then)) {
    return '—';
  }

  const diff = then - Date.now();
  const magnitude = Math.abs(diff);

  if (magnitude < 45_000) {
    return 'just now';
  }

  for (const [unit, ms] of UNITS) {
    if (magnitude >= ms) {
      return relative.format(Math.round(diff / ms), unit);
    }
  }

  return relative.format(Math.round(diff / 1000), 'second');
}

export function formatDateTime(utcValue) {
  if (!utcValue) {
    return '—';
  }

  const date = new Date(utcValue);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString();
}

export function formatDate(utcValue) {
  if (!utcValue) {
    return '—';
  }

  const date = new Date(utcValue);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleDateString();
}
