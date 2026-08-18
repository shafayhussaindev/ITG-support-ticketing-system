import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { DATA_SCOPES, adminKeys, adminService } from '@/services/adminService';
import { useToast } from '@/contexts/ToastContext';
import { Badge, Button, Card, CardBody, CardHeader, ErrorState, LoadingState } from '@/components/ui';
import s from './admin.module.css';

const BLANK_ROLE = { name: '', description: '', defaultScope: 'Own', rank: 10 };

/**
 * The permission checklist, grouped by area.
 *
 * Every key is shown with its identifier rather than only a friendly name: the key is
 * what appears in an audit row and in a 403, so an administrator diagnosing "why can't
 * they do that" needs to see the same string the system uses.
 */
function PermissionPicker({ permissions, selected, onToggle, disabled }) {
  const groups = useMemo(() => {
    const map = new Map();

    permissions.forEach((permission) => {
      const list = map.get(permission.category) ?? [];
      list.push(permission);
      map.set(permission.category, list);
    });

    return [...map.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [permissions]);

  return (
    <div>
      {groups.map(([category, items]) => (
        <div key={category} className={s.permissionGroup}>
          <div className={s.permissionGroupTitle}>{category}</div>
          <div className={s.permissionGrid}>
            {items.map((permission) => (
              <label key={permission.key} className={s.permissionItem}>
                <input
                  type="checkbox"
                  disabled={disabled}
                  checked={selected.has(permission.key)}
                  onChange={() => onToggle(permission.key)}
                />
                <span>
                  {permission.name}
                  <span className={s.permissionKey}>{permission.key}</span>
                </span>
              </label>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

function RoleForm({ role, onSave, onCancel, saving, error }) {
  const [form, setForm] = useState(() => (role
    ? {
        name: role.name,
        description: role.description ?? '',
        defaultScope: role.defaultScope,
        rank: role.rank,
      }
    : BLANK_ROLE));

  const set = (patch) => setForm((f) => ({ ...f, ...patch }));

  return (
    <form className={s.form} onSubmit={(event) => { event.preventDefault(); onSave(form); }}>
      {!role ? (
        <label className={s.field}>
          <span className={s.label}>Name</span>
          <input className={s.input} required value={form.name}
                 onChange={(e) => set({ name: e.target.value })} />
        </label>
      ) : null}

      <label className={s.field}>
        <span className={s.label}>Description</span>
        <textarea className={s.textarea} value={form.description}
                  onChange={(e) => set({ description: e.target.value })} />
      </label>

      <label className={s.field}>
        <span className={s.label}>Data scope</span>
        <select className={s.select} value={form.defaultScope}
                onChange={(e) => set({ defaultScope: e.target.value })}>
          {DATA_SCOPES.map((scope) => (
            <option key={scope.value} value={scope.value}>{scope.label}</option>
          ))}
        </select>
      </label>

      <p className={s.hint}>
        Scope decides which rows the role's permissions may touch — a different
        question from which actions it may perform. Someone with ticket.view_team and
        an Own scope sees only their own tickets, which is usually not what was meant.
      </p>

      <label className={s.field}>
        <span className={s.label}>Rank</span>
        <input className={s.input} type="number" min={0} max={1000} value={form.rank}
               onChange={(e) => set({ rank: Number(e.target.value) })} />
      </label>

      {error ? <p className={s.error}>{error}</p> : null}

      <div className={s.formActions}>
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>Cancel</Button>
        <Button type="submit" size="sm" loading={saving}>{role ? 'Save' : 'Create role'}</Button>
      </div>
    </form>
  );
}

export function RolesPage() {
  const toast = useToast();
  const queryClient = useQueryClient();

  const [selectedId, setSelectedId] = useState(null);
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(null);

  const { data: roles, isPending, isError, error, refetch } = useQuery({
    queryKey: adminKeys.roles(),
    queryFn: adminService.roles.list,
  });

  const { data: permissions } = useQuery({
    queryKey: adminKeys.permissions(),
    queryFn: adminService.roles.permissions,
    staleTime: 600_000,
  });

  const selected = roles?.find((r) => r.id === selectedId) ?? null;

  function invalidate() {
    queryClient.invalidateQueries({ queryKey: ['admin'] });
  }

  const create = useMutation({
    mutationFn: (body) => adminService.roles.create(body),
    onSuccess: (role) => {
      setCreating(false);
      setSelectedId(role.id);
      invalidate();
      toast.success('Role created');
    },
  });

  const update = useMutation({
    mutationFn: ({ id, body }) => adminService.roles.update(id, body),
    onSuccess: () => { setEditing(false); invalidate(); toast.success('Role updated'); },
  });

  const savePermissions = useMutation({
    mutationFn: ({ id, keys }) => adminService.roles.setPermissions(id, { permissionKeys: keys }),
    onSuccess: () => { setDraft(null); invalidate(); toast.success('Permissions saved'); },
    onError: (failure) => toast.error('Could not save permissions', failure.detail),
  });

  const remove = useMutation({
    mutationFn: (id) => adminService.roles.remove(id),
    onSuccess: () => { setSelectedId(null); invalidate(); toast.success('Role deleted'); },
    onError: (failure) => toast.error('Could not delete that role', failure.detail),
  });

  if (isPending) return <LoadingState label="Loading roles" />;
  if (isError) return <ErrorState error={error} onRetry={refetch} title="Could not load roles" />;

  const working = draft ?? new Set(selected?.permissions ?? []);
  const dirty = draft !== null;

  function togglePermission(key) {
    setDraft((current) => {
      const next = new Set(current ?? selected.permissions);
      next.has(key) ? next.delete(key) : next.add(key);
      return next;
    });
  }

  return (
    <>
      <header className={s.header}>
        <div>
          <h2 className={s.title}>Roles and permissions</h2>
          <p className={s.subtitle}>
            Roles are database rows, not hardcoded checks — nothing in the code branches
            on a role name. A permission change takes effect for a session when its
            access token next refreshes, within fifteen minutes.
          </p>
        </div>

        <div className={s.headerActions}>
          <Button size="sm" onClick={() => { setCreating(true); setSelectedId(null); }}>
            Add a role
          </Button>
        </div>
      </header>

      <div className={`${s.split} ${s.splitWide}`}>
        <Card>
          <div className={s.tableWrap}>
            <table className={s.table}>
              <thead>
                <tr>
                  <th scope="col">Role</th>
                  <th scope="col">Scope</th>
                  <th scope="col">Rank</th>
                  <th scope="col">People</th>
                  <th scope="col">Permissions</th>
                  <th scope="col"><span className="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                {roles.map((role) => (
                  <tr key={role.id} className={selectedId === role.id ? s.selectedRow : undefined}>
                    <th scope="row">
                      {role.name}
                      {role.isSystemRole ? <Badge tone="info">system</Badge> : null}
                      {role.description
                        ? <span className={s.permissionKey}>{role.description}</span>
                        : null}
                    </th>
                    <td>{role.defaultScope}</td>
                    <td className={s.muted}>{role.rank}</td>
                    <td>{role.userCount}</td>
                    <td>{role.permissions.length}</td>
                    <td className={s.rowActions}>
                      <button
                        type="button"
                        className={s.linkButton}
                        onClick={() => {
                          setSelectedId(role.id === selectedId ? null : role.id);
                          setCreating(false);
                          setEditing(false);
                          setDraft(null);
                        }}
                      >
                        {selectedId === role.id ? 'Close' : 'Edit'}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>

        {creating ? (
          <Card>
            <CardHeader title="New role" />
            <CardBody>
              <RoleForm
                saving={create.isPending}
                error={create.error?.detail}
                onCancel={() => setCreating(false)}
                onSave={(form) => create.mutate({ ...form, permissionKeys: [] })}
              />
            </CardBody>
          </Card>
        ) : null}

        {selected ? (
          <div className={s.stack}>
            <Card>
              <CardHeader
                title={selected.name}
                subtitle={`${selected.userCount} ${selected.userCount === 1 ? 'person holds' : 'people hold'} this role`}
                actions={
                  <Button size="sm" variant="secondary" onClick={() => setEditing((e) => !e)}>
                    {editing ? 'Cancel' : 'Settings'}
                  </Button>
                }
              />
              <CardBody>
                {editing ? (
                  <RoleForm
                    role={selected}
                    saving={update.isPending}
                    error={update.error?.detail}
                    onCancel={() => setEditing(false)}
                    onSave={(form) => update.mutate({
                      id: selected.id,
                      body: {
                        description: form.description,
                        defaultScope: form.defaultScope,
                        rank: form.rank,
                      },
                    })}
                  />
                ) : (
                  <>
                    <p className={s.hint}>
                      Scope <strong>{selected.defaultScope}</strong>, rank {selected.rank}.
                      {selected.isSystemRole
                        ? ' A system role: its permissions are editable, but it cannot be renamed or removed — seed data and documentation refer to it by name.'
                        : ''}
                    </p>

                    {!selected.isSystemRole ? (
                      <div className={s.headerActions} style={{ marginTop: 'var(--s-3)' }}>
                        <Button size="sm" variant="danger" loading={remove.isPending}
                                onClick={() => remove.mutate(selected.id)}>
                          Delete role
                        </Button>
                      </div>
                    ) : null}
                  </>
                )}
              </CardBody>
            </Card>

            <Card>
              <CardHeader
                title="Permissions"
                subtitle={`${working.size} of ${permissions?.length ?? 0} granted`}
                actions={dirty ? (
                  <div className={s.headerActions}>
                    <Button size="sm" variant="ghost" onClick={() => setDraft(null)}>Revert</Button>
                    <Button size="sm" loading={savePermissions.isPending}
                            onClick={() => savePermissions.mutate({
                              id: selected.id,
                              keys: [...working],
                            })}>
                      Save
                    </Button>
                  </div>
                ) : null}
              />
              <CardBody>
                {permissions ? (
                  <PermissionPicker
                    permissions={permissions}
                    selected={working}
                    onToggle={togglePermission}
                  />
                ) : (
                  <LoadingState label="Loading permissions" />
                )}
              </CardBody>
            </Card>
          </div>
        ) : null}
      </div>
    </>
  );
}
