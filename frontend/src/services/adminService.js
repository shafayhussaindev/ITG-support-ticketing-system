import { api } from './apiClient';

/** Drops empty filter values so the query string says only what was actually asked. */
function query(params = {}) {
  const search = new URLSearchParams();

  Object.entries(params).forEach(([key, value]) => {
    if (value !== undefined && value !== null && value !== '' && value !== false) {
      search.set(key, String(value));
    }
  });

  return search.toString();
}

export const adminService = {
  reference: () => api.get('/admin/reference'),

  users: {
    list: (params) => api.get(`/admin/users?${query(params)}`),
    get: (id) => api.get(`/admin/users/${id}`),
    create: (body) => api.post('/admin/users', body),
    update: (id, body) => api.put(`/admin/users/${id}`, body),
    setRoles: (id, body) => api.put(`/admin/users/${id}/roles`, body),
    setActive: (id, body) => api.post(`/admin/users/${id}/active`, body),
    resetPassword: (id) => api.post(`/admin/users/${id}/reset-password`, {}),
    revokeSessions: (id) => api.post(`/admin/users/${id}/revoke-sessions`, {}),
  },

  roles: {
    list: () => api.get('/admin/roles'),
    permissions: () => api.get('/admin/permissions'),
    create: (body) => api.post('/admin/roles', body),
    update: (id, body) => api.put(`/admin/roles/${id}`, body),
    setPermissions: (id, body) => api.put(`/admin/roles/${id}/permissions`, body),
    remove: (id) => api.delete(`/admin/roles/${id}`),
  },

  teams: {
    list: () => api.get('/admin/teams'),
    create: (body) => api.post('/admin/teams', body),
    update: (id, body) => api.put(`/admin/teams/${id}`, body),
    saveMember: (id, body) => api.put(`/admin/teams/${id}/members`, body),
    removeMember: (id, userId) => api.delete(`/admin/teams/${id}/members/${userId}`),
  },

  catalog: {
    categories: () => api.get('/admin/catalog/categories'),
    createCategory: (body) => api.post('/admin/catalog/categories', body),
    updateCategory: (id, body) => api.put(`/admin/catalog/categories/${id}`, body),
    deleteCategory: (id) => api.delete(`/admin/catalog/categories/${id}`),
    createSubcategory: (body) => api.post('/admin/catalog/subcategories', body),
    updateSubcategory: (id, body) => api.put(`/admin/catalog/subcategories/${id}`, body),
    applications: () => api.get('/admin/catalog/applications'),
    createApplication: (body) => api.post('/admin/catalog/applications', body),
    updateApplication: (id, body) => api.put(`/admin/catalog/applications/${id}`, body),
    createModule: (body) => api.post('/admin/catalog/modules', body),
    updateModule: (id, body) => api.put(`/admin/catalog/modules/${id}`, body),
    priorityMatrix: () => api.get('/admin/catalog/priority-matrix'),
    savePriorityMatrix: (body) => api.put('/admin/catalog/priority-matrix', body),
  },

  sla: {
    policies: () => api.get('/admin/sla/policies'),
    createPolicy: (body) => api.post('/admin/sla/policies', body),
    updatePolicy: (id, body) => api.put(`/admin/sla/policies/${id}`, body),
    calendars: () => api.get('/admin/sla/calendars'),
    createCalendar: (body) => api.post('/admin/sla/calendars', body),
    updateCalendar: (id, body) => api.put(`/admin/sla/calendars/${id}`, body),
    addHoliday: (id, body) => api.post(`/admin/sla/calendars/${id}/holidays`, body),
    removeHoliday: (id, holidayId) => api.delete(`/admin/sla/calendars/${id}/holidays/${holidayId}`),
  },

  settings: {
    list: () => api.get('/admin/settings'),
    save: (body) => api.put('/admin/settings', body),
    remove: (id) => api.delete(`/admin/settings/${id}`),
  },
};

export const adminKeys = {
  reference: () => ['admin', 'reference'],
  users: (params) => ['admin', 'users', params],
  user: (id) => ['admin', 'user', id],
  roles: () => ['admin', 'roles'],
  permissions: () => ['admin', 'permissions'],
  teams: () => ['admin', 'teams'],
  categories: () => ['admin', 'categories'],
  applications: () => ['admin', 'applications'],
  priorityMatrix: () => ['admin', 'priority-matrix'],
  slaPolicies: () => ['admin', 'sla-policies'],
  calendars: () => ['admin', 'calendars'],
  settings: () => ['admin', 'settings'],
};

export const PRIORITIES = ['Critical', 'High', 'Medium', 'Low'];
export const IMPACTS = ['Critical', 'High', 'Medium', 'Low'];
export const URGENCIES = ['Critical', 'High', 'Medium', 'Low'];

export const DATA_SCOPES = [
  { value: 'Own', label: 'Own — only what they raised' },
  { value: 'Assigned', label: 'Assigned — theirs plus what they own' },
  { value: 'Team', label: 'Team — their teams and the unassigned pool' },
  { value: 'Department', label: 'Department — their department' },
  { value: 'Organization', label: 'Organization — everything in the tenant' },
  { value: 'All', label: 'All — every organization' },
];

export const DAYS = [
  'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday',
];

/** Minutes past midnight to a 24-hour clock, which is how the calendar stores hours. */
export function minutesToTime(minutes) {
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return `${String(hours).padStart(2, '0')}:${String(rest).padStart(2, '0')}`;
}

export function timeToMinutes(value) {
  const [hours, minutes] = String(value).split(':').map(Number);
  return (hours || 0) * 60 + (minutes || 0);
}

/** Renders a target in hours or days once minutes stop being readable. */
export function formatTarget(minutes) {
  if (minutes < 60) return `${minutes}m`;
  if (minutes < 1440) return `${(minutes / 60).toFixed(minutes % 60 === 0 ? 0 : 1)}h`;
  return `${(minutes / 1440).toFixed(minutes % 1440 === 0 ? 0 : 1)}d`;
}
