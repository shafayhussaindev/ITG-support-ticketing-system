import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { DATA_SCOPES, adminKeys, adminService } from '@/services/adminService';
import { useToast } from '@/contexts/ToastContext';
import { Badge, Button, Card, CardBody, CardHeader, ErrorState, LoadingState } from '@/components/ui';
import s from './admin.module.css';

const BLANK_ROLE = { name: '', description: '', defaultScope: 'Own' };

/**
 * Turns the role ladder into the places a role can sit in it.
 *
 * An administrator knows their organization's hierarchy by name — a new role belongs
 * above Staff, or below the Manager. They have no way of knowing that the
 * system stores that as the integer 45, nor should they: rank is an ordering hint with
 * no bearing on what a role may actually do, and a numeric field invites the reader to
 * assume otherwise. So the choice is offered the way it is actually thought about, and
 * the number is derived from it.
 *
 * The rank produced sits half way between the two neighbours. The server re-spaces
 * every role to multiples of ten after each save, which is what guarantees that half
 * way between two neighbours is always a whole number nobody else holds.
 *
 * @param roles   every role, highest authority first
 * @param editing the role being moved, excluded from its own neighbour list
 */
export function buildPositions(roles, editing) {
  const others = roles.filter((r) => r.id !== editing?.id);

  if (others.length === 0) {
    return [{ label: 'The only role', rank: 10 }];
  }

  const highest = others[0];
  const lowest = others[others.length - 1];

  return [
    { label: `Above ${highest.name} — the most authority`, rank: highest.rank + 10 },

    ...others.slice(0, -1).map((role, i) => ({
      label: `Between ${role.name} and ${others[i + 1].name}`,
      rank: Math.round((role.rank + others[i + 1].rank) / 2),
    })),

    // Halved rather than reduced by ten, so the lowest rung can never go negative
    // however many times a role is pushed to the bottom.
    { label: `Below ${lowest.name} — the least authority`, rank: Math.floor(lowest.rank / 2) },
  ];
}

/** Which position a role currently occupies, so editing opens on where it already is. */
export function currentPosition(positions, role) {
  if (!role) {
    // A new role starts at the bottom. Authority is granted deliberately, and the
    // administrator is about to pick its permissions anyway.
    return positions.length - 1;
  }

  let closest = 0;

  positions.forEach((position, i) => {
    if (Math.abs(position.rank - role.rank) < Math.abs(positions[closest].rank - role.rank)) {
      closest = i;
    }
  });

  return closest;
}

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

function RoleForm({ role, roles, onSave, onCancel, saving, error }) {
  const positions = useMemo(() => buildPositions(roles, role), [roles, role]);

  const [form, setForm] = useState(() => (role
    ? {
        name: role.name,
        description: role.description ?? '',
        defaultScope: role.defaultScope,
      }
    : BLANK_ROLE));

  const [position, setPosition] = useState(() => currentPosition(positions, role));

  const set = (patch) => setForm((f) => ({ ...f, ...patch }));

  const submit = (event) => {
    event.preventDefault();
    onSave({ ...form, rank: positions[Math.min(position, positions.length - 1)].rank });
  };

  return (
    <form className={s.form} onSubmit={submit}>
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
        Scope decides which rows the role&apos;s permissions may touch — a different
        question from which actions it may perform. Someone with ticket.view_team and
        an Own scope sees only their own tickets, which is usually not what was meant.
      </p>

      <label className={s.field}>
        <span className={s.label}>Position in the hierarchy</span>
        <select className={s.select} value={position}
                onChange={(e) => setPosition(Number(e.target.value))}>
          {positions.map((option, i) => (
            <option key={option.label} value={i}>{option.label}</option>
          ))}
        </select>
      </label>

      <p className={s.hint}>
        Position decides only where the role appears in lists like this one. It grants
        nothing on its own — a role placed above the Manager can still do no more than
        the permissions ticked for it.
      </p>

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
                  <th scope="col">Order</th>
                  <th scope="col">People</th>
                  <th scope="col">Permissions</th>
                  <th scope="col"><span className="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                {roles.map((role, index) => (
                  <tr key={role.id} className={selectedId === role.id ? s.selectedRow : undefined}>
                    <th scope="row">
                      {role.name}
                      {role.isSystemRole ? <Badge tone="info">system</Badge> : null}
                      {role.description
                        ? <span className={s.permissionKey}>{role.description}</span>
                        : null}
                    </th>
                    <td>{role.defaultScope}</td>
                    <td className={s.muted}>{index + 1}</td>
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
                roles={roles}
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
                    roles={roles}
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
                      Scope <strong>{selected.defaultScope}</strong>, placed{' '}
                      <strong>{roles.findIndex((r) => r.id === selected.id) + 1}</strong>{' '}
                      of {roles.length} by authority.
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
