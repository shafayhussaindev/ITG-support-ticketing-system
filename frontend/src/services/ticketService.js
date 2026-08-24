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

  // What the caller may claim, so the form can say so before they submit rather than
  // the server quietly reducing it afterwards.
  severityCeiling: () => api.get('/tickets/severity-ceiling'),

  attachments: (id) => api.get(`/tickets/${id}/attachments`),

  uploadAttachment: (id, file, { commentId, isInternalOnly } = {}) => {
    const form = new FormData();
    form.append('file', file);
    if (commentId) form.append('commentId', commentId);
    if (isInternalOnly) form.append('isInternalOnly', 'true');

    return api.upload(`/tickets/${id}/attachments`, form);
  },
  deleteAttachment: (id, attachmentId) => api.delete(`/tickets/${id}/attachments/${attachmentId}`),

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
  attachments: (id) => ['tickets', 'attachments', id],
  timeline: (id) => ['tickets', 'timeline', id],
};
