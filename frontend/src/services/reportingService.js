import { api } from './apiClient';

export const reportingService = {
  dashboard: (days = 30) => api.get(`/dashboard?days=${days}`),
  ticketRating: (ticketId) => api.get(`/tickets/${ticketId}/feedback`),
  submitRating: (ticketId, body) => api.post(`/tickets/${ticketId}/feedback`, body),
};

export const knowledgeService = {
  search: ({ search = '', categoryId = '', status = '', page = 1, pageSize = 20 } = {}) => {
    const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
    if (search) params.set('search', search);
    if (categoryId) params.set('categoryId', categoryId);
    if (status) params.set('status', status);
    return api.get(`/knowledge/articles?${params}`);
  },
  get: (id) => api.get(`/knowledge/articles/${id}`),
  versions: (id) => api.get(`/knowledge/articles/${id}/versions`),
  suggestions: (text, categoryId) => {
    const params = new URLSearchParams({ take: '5' });
    if (text) params.set('text', text);
    if (categoryId) params.set('categoryId', categoryId);
    return api.get(`/knowledge/suggestions?${params}`);
  },
  create: (body) => api.post('/knowledge/articles', body),
  update: (id, body) => api.put(`/knowledge/articles/${id}`, body),
  changeStatus: (id, body) => api.post(`/knowledge/articles/${id}/status`, body),
  feedback: (id, body) => api.post(`/knowledge/articles/${id}/feedback`, body),
  recordView: (id) => api.post(`/knowledge/articles/${id}/view`, {}),
};

export const reportingKeys = {
  dashboard: (days) => ['dashboard', days],
  rating: (ticketId) => ['rating', ticketId],
};

export const knowledgeKeys = {
  search: (params) => ['knowledge', 'search', params],
  article: (id) => ['knowledge', 'article', id],
  versions: (id) => ['knowledge', 'versions', id],
  suggestions: (text, categoryId) => ['knowledge', 'suggestions', text, categoryId],
};

/** Renders a minute count as a readable duration, or an em dash when there is no data. */
export function formatMinutes(minutes) {
  if (minutes === null || minutes === undefined) {
    return '—';
  }

  const total = Math.round(minutes);

  if (total < 60) {
    return `${total}m`;
  }

  if (total < 1440) {
    return `${Math.floor(total / 60)}h ${total % 60}m`;
  }

  return `${Math.floor(total / 1440)}d ${Math.floor((total % 1440) / 60)}h`;
}
