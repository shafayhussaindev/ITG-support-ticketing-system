import { useState } from 'react';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { adminKeys, adminService } from '@/services/adminService';
import { useAuth } from '@/contexts/AuthContext';
import { useToast } from '@/contexts/ToastContext';
import { Badge, Button, Card, CardBody, CardHeader, EmptyState, ErrorState, Skeleton } from '@/components/ui';
import { formatRelative } from '@/utils/datetime';
import s from './admin.module.css';

const EMPTY_FILTERS = { search: '', roleId: '', teamId: '', activeOnly: false };

const BLANK_USER = {
  email: '',
  firstName: '',
  lastName: '',
  jobTitle: '',
  phoneNumber: '',
  departmentId: '',
  officeId: '',
  isAvailableForAssignment: true,
  maxConcurrentTickets: 0,
};

/**
 * Shows a generated password once, with the reason it is only shown once.
 *
 * Deliberately not copyable-and-forgettable: the administrator has to deal with it
 * now, because there is no second chance to read it and no stored copy to recover.
 */
function OneTimePassword({ result, onDismiss }) {
  return (
    <div className={s.notice}>
      <strong>{result.notice}</strong>
      <div className={s.secretBox}>
        <span>{result.temporaryPassword}</span>
        <Button
          size="sm"
          variant="ghost"
          onClick={() => navigator.clipboard?.writeText(result.temporaryPassword)}
        >
          Copy
        </Button>
      </div>
      <div style={{ marginTop: 'var(--s-2)' }}>
        <Button size="sm" variant="secondary" onClick={onDismiss}>I have passed it on</Button>
      </div>
    </div>
  );
}

function UserForm({ user, reference, onSave, onCancel, saving, error }) {
  const [form, setForm] = useState(() => (user
    ? {
        email: user.email,
        firstName: user.firstName,
        lastName: user.lastName,
        jobTitle: user.jobTitle ?? '',
        phoneNumber: user.phoneNumber ?? '',
        departmentId: user.departmentId ?? '',
        officeId: user.officeId ?? '',
        isAvailableForAssignment: user.isAvailableForAssignment,
        maxConcurrentTickets: user.maxConcurrentTickets,
      }
    : BLANK_USER));

  const set = (patch) => setForm((f) => ({ ...f, ...patch }));

  return (
    <form
      className={s.form}
      onSubmit={(event) => {
        event.preventDefault();
        onSave(form);
      }}
    >
      {!user ? (
        <label className={s.field}>
          <span className={s.label}>Work email</span>
          <input
            className={s.input}
            type="email"
            required
            value={form.email}
            onChange={(e) => set({ email: e.target.value })}
          />
        </label>
      ) : null}

      <div className={s.formRow}>
        <label className={s.field}>
          <span className={s.label}>First name</span>
          <input className={s.input} required value={form.firstName}
                 onChange={(e) => set({ firstName: e.target.value })} />
        </label>
        <label className={s.field}>
          <span className={s.label}>Last name</span>
          <input className={s.input} required value={form.lastName}
                 onChange={(e) => set({ lastName: e.target.value })} />
        </label>
      </div>

      <div className={s.formRow}>
        <label className={s.field}>
          <span className={s.label}>Job title</span>
          <input className={s.input} value={form.jobTitle}
                 onChange={(e) => set({ jobTitle: e.target.value })} />
        </label>
        <label className={s.field}>
          <span className={s.label}>Phone</span>
          <input className={s.input} value={form.phoneNumber}
                 onChange={(e) => set({ phoneNumber: e.target.value })} />
        </label>
      </div>

      <div className={s.formRow}>
        <label className={s.field}>
          <span className={s.label}>Department</span>
          <select className={s.select} value={form.departmentId}
                  onChange={(e) => set({ departmentId: e.target.value })}>
            <option value="">None</option>
            {reference.departments.map((d) => (
              <option key={d.id} value={d.id}>{d.name}</option>
            ))}
          </select>
        </label>
        <label className={s.field}>
          <span className={s.label}>Office</span>
          <select className={s.select} value={form.officeId}
                  onChange={(e) => set({ officeId: e.target.value })}>
            <option value="">None</option>
            {reference.offices.map((o) => (
              <option key={o.id} value={o.id}>{o.name}</option>
            ))}
          </select>
        </label>
      </div>

      {user ? (
        <>
          <div className={s.formRow}>
            <label className={s.field}>
              <span className={s.label}>Concurrent ticket cap</span>
              <input className={s.input} type="number" min={0} max={500}
                     value={form.maxConcurrentTickets}
                     onChange={(e) => set({ maxConcurrentTickets: Number(e.target.value) })} />
            </label>
          </div>

          <label className={s.checkbox}>
            <input type="checkbox" checked={form.isAvailableForAssignment}
                   onChange={(e) => set({ isAvailableForAssignment: e.target.checked })} />
            Available for automatic assignment
          </label>
          <p className={s.hint}>
            Clearing this keeps the account active but takes them out of routing —
            what you want for someone on leave rather than someone who has left.
          </p>
        </>
      ) : (
        <p className={s.hint}>
          A one-time password is generated on save and shown once. You never choose it,
          and it is never stored in readable form.
        </p>
      )}

      {error ? <p className={s.error}>{error}</p> : null}

      <div className={s.formActions}>
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>Cancel</Button>
        <Button type="submit" size="sm" loading={saving}>
          {user ? 'Save changes' : 'Create account'}
        </Button>
      </div>
    </form>
  );
}

function RolePicker({ user, roles, onSave, saving }) {
  const [selected, setSelected] = useState(() => new Set(user.roleIds));

  function toggle(id) {
    setSelected((current) => {
      const next = new Set(current);
      next.has(id) ? next.delete(id) : next.add(id);
      return next;
    });
  }

  const dirty = selected.size !== user.roleIds.length
    || user.roleIds.some((id) => !selected.has(id));

  return (
    <div className={s.form}>
      <div>
        {roles.map((role) => (
          <label key={role.id} className={s.permissionItem}>
            <input type="checkbox" checked={selected.has(role.id)} onChange={() => toggle(role.id)} />
            <span>{role.name}</span>
          </label>
        ))}
      </div>

      <p className={s.hint}>
        A role change reaches an existing session only when its access token expires,
        within fifteen minutes. To cut someone off now, deactivate the account — that
        revokes their sessions immediately.
      </p>

      <div className={s.formActions}>
        <Button
          size="sm"
          disabled={!dirty}
          loading={saving}
          onClick={() => onSave([...selected])}
        >
          Save roles
        </Button>
      </div>
    </div>
  );
}

export function UsersPage() {
  const { can } = useAuth();
  const toast = useToast();
  const queryClient = useQueryClient();

  const [filters, setFilters] = useState(EMPTY_FILTERS);
  const [applied, setApplied] = useState(EMPTY_FILTERS);
  const [page, setPage] = useState(1);
  const [selectedId, setSelectedId] = useState(null);
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState(false);
  const [secret, setSecret] = useState(null);

  // Deleting answers for the tenant rather than administering it, so it is
  // gated on organizations.manage — which only Super Admin holds.
  const canDelete = can('organizations.manage');

  const params = { ...applied, page, pageSize: 25 };

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: adminKeys.users(params),
    queryFn: () => adminService.users.list(params),
    placeholderData: keepPreviousData,
  });

  const { data: reference } = useQuery({
    queryKey: adminKeys.reference(),
    queryFn: adminService.reference,
    staleTime: 300_000,
  });

  const { data: selected } = useQuery({
    queryKey: adminKeys.user(selectedId),
    queryFn: () => adminService.users.get(selectedId),
    enabled: Boolean(selectedId),
  });

  function invalidate() {
    queryClient.invalidateQueries({ queryKey: ['admin'] });
  }

  const create = useMutation({
    mutationFn: (body) => adminService.users.create(body),
    onSuccess: (result) => {
      setCreating(false);
      setSecret(result);
      invalidate();
    },
  });

  const update = useMutation({
    mutationFn: ({ id, body }) => adminService.users.update(id, body),
    onSuccess: () => {
      setEditing(false);
      invalidate();
      toast.success('Account updated');
    },
  });

  const setRoles = useMutation({
    mutationFn: ({ id, roleIds }) => adminService.users.setRoles(id, { roleIds }),
    onSuccess: () => {
      invalidate();
      toast.success('Roles updated');
    },
    onError: (failure) => toast.error('Could not change roles', failure.detail),
  });

  const setActive = useMutation({
    mutationFn: ({ id, isActive }) => adminService.users.setActive(id, { isActive }),
    onSuccess: (result) => {
      invalidate();
      toast.success(result.isActive ? 'Account restored' : 'Account deactivated and signed out');
    },
    onError: (failure) => toast.error('Could not change that', failure.detail),
  });

  const resetPassword = useMutation({
    mutationFn: (id) => adminService.users.resetPassword(id),
    onSuccess: (result) => {
      setSecret(result);
      invalidate();
    },
  });

  const remove = useMutation({
    mutationFn: (id) => adminService.users.remove(id),
    onSuccess: (result) => {
      setSelectedId(null);
      invalidate();

      // The message says what happened to the work, not just that something did.
      // "Deleted" alone would leave an administrator guessing whether the tickets
      // went with it.
      toast.success('Account deleted', result.message);
    },
    onError: (failure) =>
      toast.error('Could not delete that account', failure.detail),
  });

  const revoke = useMutation({
    mutationFn: (id) => adminService.users.revokeSessions(id),
    onSuccess: (count) => {
      invalidate();
      toast.success(`${count} session${count === 1 ? '' : 's'} revoked`);
    },
  });

  if (isError) {
    return <ErrorState error={error} onRetry={refetch} title="Could not load users" />;
  }

  return (
    <>
      <header className={s.header}>
        <div>
          <h2 className={s.title}>Users</h2>
          <p className={s.subtitle}>
            Accounts are never deleted — a name is attached to tickets, comments and
            audit rows that must stay attributable. Deactivating instead revokes every
            session the person holds and keeps the history intact.
          </p>
        </div>

        <div className={s.headerActions}>
          <Button size="sm" onClick={() => { setCreating(true); setSelectedId(null); }}>
            Add a user
          </Button>
        </div>
      </header>

      {secret ? <OneTimePassword result={secret} onDismiss={() => setSecret(null)} /> : null}

      <div className={`${s.split} ${selectedId || creating ? s.splitWide : ''}`}>
        <Card>
          <form
            className={s.filters}
            onSubmit={(event) => {
              event.preventDefault();
              setPage(1);
              setApplied(filters);
            }}
          >
            <input
              className={s.input}
              style={{ flex: '1 1 200px' }}
              type="search"
              placeholder="Name, email or job title…"
              value={filters.search}
              onChange={(e) => setFilters((f) => ({ ...f, search: e.target.value }))}
            />

            <select
              className={s.select}
              style={{ flex: '0 1 170px' }}
              value={filters.roleId}
              onChange={(e) => setFilters((f) => ({ ...f, roleId: e.target.value }))}
            >
              <option value="">Any role</option>
              {(reference?.roles ?? []).map((r) => (
                <option key={r.id} value={r.id}>{r.name}</option>
              ))}
            </select>

            <label className={s.checkbox}>
              <input
                type="checkbox"
                checked={filters.activeOnly}
                onChange={(e) => setFilters((f) => ({ ...f, activeOnly: e.target.checked }))}
              />
              Active only
            </label>

            <Button type="submit" size="sm">Search</Button>
          </form>

          {isPending ? (
            <div style={{ padding: 'var(--s-3)' }}>
              {Array.from({ length: 6 }, (_, i) => <Skeleton key={i} height={32} />)}
            </div>
          ) : data.items.length === 0 ? (
            <EmptyState icon="◐" title="Nobody matches" message="Try a broader search." />
          ) : (
            <>
              <div className={s.tableWrap}>
                <table className={s.table}>
                  <thead>
                    <tr>
                      <th scope="col">Person</th>
                      <th scope="col">Roles</th>
                      <th scope="col">Teams</th>
                      <th scope="col">Open</th>
                      <th scope="col">Last seen</th>
                      <th scope="col"><span className="sr-only">Actions</span></th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.items.map((user) => (
                      <tr key={user.id} className={selectedId === user.id ? s.selectedRow : undefined}>
                        <th scope="row">
                          {user.fullName}
                          <span className={s.permissionKey}>{user.email}</span>
                          {!user.isActive ? <Badge tone="neutral">deactivated</Badge> : null}
                          {user.lockoutEndUtc ? <Badge tone="danger">locked out</Badge> : null}
                        </th>
                        <td>
                          <span className={s.chips}>
                            {user.roles.map((r) => <span key={r} className={s.chip}>{r}</span>)}
                          </span>
                        </td>
                        <td>
                          <span className={s.chips}>
                            {user.teams.length === 0
                              ? <span className={s.muted}>—</span>
                              : user.teams.map((t) => <span key={t} className={s.chip}>{t}</span>)}
                          </span>
                        </td>
                        <td>{user.openTickets || <span className={s.muted}>—</span>}</td>
                        <td className={s.muted}>
                          {user.lastLoginAtUtc ? formatRelative(user.lastLoginAtUtc) : 'never'}
                        </td>
                        <td className={s.rowActions}>
                          <button
                            type="button"
                            className={s.linkButton}
                            onClick={() => {
                              setSelectedId(user.id === selectedId ? null : user.id);
                              setCreating(false);
                              setEditing(false);
                            }}
                          >
                            {selectedId === user.id ? 'Close' : 'Manage'}
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              <div className={s.pager}>
                <span className={s.pagerText}>
                  Page {data.page} of {data.totalPages} · {data.totalCount} accounts
                </span>
                <div className={s.pagerButtons}>
                  <Button size="sm" variant="secondary" disabled={!data.hasPrevious}
                          onClick={() => setPage((p) => p - 1)}>Previous</Button>
                  <Button size="sm" variant="secondary" disabled={!data.hasNext}
                          onClick={() => setPage((p) => p + 1)}>Next</Button>
                </div>
              </div>
            </>
          )}
        </Card>

        {creating && reference ? (
          <Card>
            <CardHeader title="New account" subtitle="A one-time password is generated on save" />
            <CardBody>
              <UserForm
                reference={reference}
                saving={create.isPending}
                error={create.error?.detail}
                onCancel={() => setCreating(false)}
                onSave={(form) => create.mutate({
                  ...form,
                  departmentId: form.departmentId || null,
                  officeId: form.officeId || null,
                })}
              />
            </CardBody>
          </Card>
        ) : null}

        {selectedId && selected && reference ? (
          <div className={s.stack}>
            <Card>
              <CardHeader
                title={`${selected.firstName} ${selected.lastName}`}
                subtitle={selected.email}
                actions={
                  <Button size="sm" variant="secondary" onClick={() => setEditing((e) => !e)}>
                    {editing ? 'Cancel' : 'Edit'}
                  </Button>
                }
              />
              <CardBody>
                {editing ? (
                  <UserForm
                    user={selected}
                    reference={reference}
                    saving={update.isPending}
                    error={update.error?.detail}
                    onCancel={() => setEditing(false)}
                    onSave={(form) => update.mutate({
                      id: selected.id,
                      body: {
                        ...form,
                        departmentId: form.departmentId || null,
                        officeId: form.officeId || null,
                      },
                    })}
                  />
                ) : (
                  <>
                    <p className={s.hint}>
                      {selected.activeSessions} active
                      {selected.activeSessions === 1 ? ' session' : ' sessions'}
                      {selected.mustChangePassword ? ' · must change password at next sign-in' : ''}
                    </p>

                    <div className={s.headerActions} style={{ marginTop: 'var(--s-3)' }}>
                      <Button
                        size="sm"
                        variant={selected.isActive ? 'danger' : 'secondary'}
                        loading={setActive.isPending}
                        onClick={() => setActive.mutate({
                          id: selected.id,
                          isActive: !selected.isActive,
                        })}
                      >
                        {selected.isActive ? 'Deactivate' : 'Restore'}
                      </Button>

                      <Button size="sm" variant="secondary" loading={resetPassword.isPending}
                              onClick={() => resetPassword.mutate(selected.id)}>
                        Reset password
                      </Button>

                      <Button size="sm" variant="ghost" loading={revoke.isPending}
                              disabled={selected.activeSessions === 0}
                              onClick={() => revoke.mutate(selected.id)}>
                        Sign out everywhere
                      </Button>

                      {canDelete ? (
                        <Button size="sm" variant="danger" loading={remove.isPending}
                                onClick={() => remove.mutate(selected.id)}>
                          Delete permanently
                        </Button>
                      ) : null}
                    </div>

                    {canDelete ? (
                      <p className={s.hint} style={{ marginTop: 'var(--s-2)' }}>
                        Deleting removes the person permanently and cannot be undone.
                        Any tickets, comments or articles they left behind stay in the
                        system and show them as <strong>Deleted user</strong>, because
                        that history has to remain attributable. An account that owns
                        nothing is removed outright.
                      </p>
                    ) : null}
                  </>
                )}
              </CardBody>
            </Card>

            <Card>
              <CardHeader title="Roles" subtitle="Permissions come from the union of these" />
              <CardBody>
                <RolePicker
                  key={selected.id + selected.roleIds.join()}
                  user={selected}
                  roles={reference.roles}
                  saving={setRoles.isPending}
                  onSave={(roleIds) => setRoles.mutate({ id: selected.id, roleIds })}
                />
              </CardBody>
            </Card>

            <Card>
              <CardHeader
                title="Effective permissions"
                subtitle={`${selected.effectivePermissions.length} after overrides`}
              />
              <CardBody>
                <div className={s.chips}>
                  {selected.effectivePermissions.map((key) => (
                    <span key={key} className={s.chip}>{key}</span>
                  ))}
                </div>
              </CardBody>
            </Card>

            {selected.teams.length > 0 ? (
              <Card>
                <CardHeader title="Teams" subtitle="Edited on the Teams screen" />
                <CardBody>
                  <div className={s.tableWrap}>
                    <table className={s.table}>
                      <thead>
                        <tr>
                          <th scope="col">Team</th>
                          <th scope="col">Role</th>
                          <th scope="col">Capacity</th>
                        </tr>
                      </thead>
                      <tbody>
                        {selected.teams.map((team) => (
                          <tr key={team.teamId}>
                            <th scope="row">{team.teamName}</th>
                            <td>{team.roleInTeam}</td>
                            <td>{team.capacityWeight}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </CardBody>
              </Card>
            ) : null}
          </div>
        ) : null}
      </div>
    </>
  );
}
