import { api } from './apiClient';

function toQueryString(params = {}) {
  const search = new URLSearchParams();

  for (const [key, value] of Object.entries(params)) {
    // Blank filters must be omitted rather than sent empty: the API treats an
    // unparseable status as "no filter", but sending it anyway makes the URL noisy
    // and the query cache key unstable.
    if (value === undefined || value === null || value === '') {
      continue;
    }

    search.append(key, String(value));
  }

  const query = search.toString();
  return query ? `?${query}` : '';
}

export const ticketService = {
  list: (params) => api.get(`/tickets${toQueryString(params)}`),
  get: (id) => api.get(`/tickets/${id}`),
  create: (body) => api.post('/tickets', body),

  comments: (id) => api.get(`/tickets/${id}/comments`),
  addComment: (id, body) => api.post(`/tickets/${id}/comments`, body),

  timeline: (id) => api.get(`/tickets/${id}/timeline`),

  accept: (id) => api.post(`/tickets/${id}/accept`, {}),
  assign: (id, body) => api.post(`/tickets/${id}/assign`, body),
  changeStatus: (id, body) => api.post(`/tickets/${id}/status`, body),
  changePriority: (id, body) => api.post(`/tickets/${id}/priority`, body),
  resolve: (id, body) => api.post(`/tickets/${id}/resolve`, body),
  close: (id, body) => api.post(`/tickets/${id}/close`, body),
  reopen: (id, body) => api.post(`/tickets/${id}/reopen`, body),
};

/** Query keys in one place so an invalidation cannot miss a cache entry by typo. */
export const ticketKeys = {
  all: ['tickets'],
  list: (params) => ['tickets', 'list', params],
  detail: (id) => ['tickets', 'detail', id],
  comments: (id) => ['tickets', 'comments', id],
  timeline: (id) => ['tickets', 'timeline', id],
};
