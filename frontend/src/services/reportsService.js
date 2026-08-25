import { api } from './apiClient';

/** Serialises the shared report filters, omitting anything unset. */
function toQuery({ fromUtc, toUtc, teamId, categoryId, staffId } = {}) {
  const params = new URLSearchParams();

  if (fromUtc) params.set('fromUtc', fromUtc);
  if (toUtc) params.set('toUtc', toUtc);
  if (teamId) params.set('teamId', teamId);
  if (categoryId) params.set('categoryId', categoryId);
  if (staffId) params.set('staffId', staffId);

  return params.toString();
}

export const reportsService = {
  slaCompliance: (filters) => api.get(`/reports/sla-compliance?${toQuery(filters)}`),
  staffPerformance: (filters) => api.get(`/reports/staff-performance?${toQuery(filters)}`),
  volumeTrend: (filters) => api.get(`/reports/volume-trend?${toQuery(filters)}`),
  satisfaction: (filters) => api.get(`/reports/satisfaction?${toQuery(filters)}`),
  customerBehaviour: (filters) => api.get(`/reports/customer-behaviour?${toQuery(filters)}`),
  export: (report, filters = {}) => api.download('/reports/export', { report, ...filters }),
};

export const auditService = {
  search: (params = {}) => {
    const query = new URLSearchParams();

    Object.entries(params).forEach(([key, value]) => {
      if (value !== undefined && value !== null && value !== '') {
        query.set(key, String(value));
      }
    });

    return api.get(`/audit?${query}`);
  },
  filters: () => api.get('/audit/filters'),
  entity: (id) => api.get(`/audit/entities/${id}`),
};

export const reportKeys = {
  sla: (filters) => ['reports', 'sla-compliance', filters],
  staff: (filters) => ['reports', 'staff-performance', filters],
  volume: (filters) => ['reports', 'volume-trend', filters],
  satisfaction: (filters) => ['reports', 'satisfaction', filters],
  customerBehaviour: (filters) => ['reports', 'customer-behaviour', filters],
};

export const auditKeys = {
  search: (params) => ['audit', 'search', params],
  filters: () => ['audit', 'filters'],
  entity: (id) => ['audit', 'entity', id],
};

/**
 * Turns a day count into the ISO instant the API expects.
 *
 * Anchored to midnight rather than to "now minus N × 24h", so the same choice
 * produces the same period all day and two people comparing figures at different
 * hours are looking at the same report.
 */
export function daysAgoIso(days) {
  const date = new Date();
  date.setUTCHours(0, 0, 0, 0);
  date.setUTCDate(date.getUTCDate() - days + 1);
  return date.toISOString();
}
