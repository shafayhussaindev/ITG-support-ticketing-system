import { api } from './apiClient';

export const slaService = {
  /** Returns null when the ticket has no SLA policy attached (the API answers 204). */
  forTicket: (ticketId) => api.get(`/tickets/${ticketId}/sla`),
  escalations: (openOnly = true) => api.get(`/escalations?openOnly=${openOnly}`),

  // Counted on the server: the listing is capped at 200 rows, so totalling it in the
  // browser would under-report at exactly the moment the queue is worst.
  escalationSummary: () => api.get('/escalations/summary'),

  acknowledgeEscalation: (id, note) =>
    api.post(`/escalations/${id}/acknowledge`, { note: note || null }),
};

export const notificationService = {
  list: ({ unreadOnly = false, take = 20 } = {}) =>
    api.get(`/notifications?unreadOnly=${unreadOnly}&take=${take}`),
  markRead: (ids) => api.post('/notifications/read', { ids, all: false }),
  markAllRead: () => api.post('/notifications/read', { ids: null, all: true }),
};

export const slaKeys = {
  ticket: (id) => ['sla', 'ticket', id],
  escalations: (openOnly) => ['sla', 'escalations', openOnly],
  escalationSummary: () => ['sla', 'escalations', 'summary'],
};

export const notificationKeys = {
  mine: ['notifications', 'mine'],
};

/**
 * Formats a signed minute count as a human duration.
 * Negative values read as overdue rather than as a minus sign, because "-3h 20m"
 * is easy to misread as time remaining at a glance.
 */
export function formatSlaRemaining(minutes) {
  if (minutes === null || minutes === undefined || Number.isNaN(minutes)) {
    return '—';
  }

  const overdue = minutes < 0;
  const total = Math.round(Math.abs(minutes));

  const days = Math.floor(total / 1440);
  const hours = Math.floor((total % 1440) / 60);
  const mins = total % 60;

  let text;
  if (days > 0) {
    text = `${days}d ${hours}h`;
  } else if (hours > 0) {
    text = `${hours}h ${mins}m`;
  } else {
    text = `${mins}m`;
  }

  return overdue ? `${text} overdue` : `${text} left`;
}

/** Maps SLA position onto a visual tone, matching the server's own thresholds. */
export function slaTone(sla) {
  if (!sla) {
    return 'neutral';
  }

  if (sla.resolutionState === 'Breached') {
    return 'danger';
  }

  if (sla.resolutionState === 'Met') {
    return 'success';
  }

  if (sla.resolutionState === 'Cancelled') {
    return 'neutral';
  }

  if (sla.isPaused) {
    return 'info';
  }

  return sla.resolutionConsumedPercent >= sla.warningThresholdPercent ? 'warning' : 'success';
}
