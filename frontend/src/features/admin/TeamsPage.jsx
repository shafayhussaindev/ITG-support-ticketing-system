import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { adminKeys, adminService } from '@/services/adminService';
import { useToast } from '@/contexts/ToastContext';
import { Badge, Button, Card, CardBody, CardHeader, ErrorState, LoadingState } from '@/components/ui';
import s from './admin.module.css';

const BLANK_TEAM = {
  name: '',
  code: '',
  description: '',
  departmentId: '',
  teamLeadId: '',
  escalationTeamId: '',
  acceptanceTimeoutMinutes: 30,
  isActive: true,
};

const TEAM_ROLES = ['Member', 'Lead', 'Backup'];

function TeamForm({ team, teams, reference, onSave, onCancel, saving, error }) {
  const [form, setForm] = useState(() => (team
    ? {
        name: team.name,
        code: team.code,
        description: team.description ?? '',
        departmentId: team.departmentId ?? '',
        teamLeadId: team.teamLeadId ?? '',
        escalationTeamId: team.escalationTeamId ?? '',
        acceptanceTimeoutMinutes: team.acceptanceTimeoutMinutes,
        isActive: team.isActive,
      }
    : BLANK_TEAM));

  const set = (patch) => setForm((f) => ({ ...f, ...patch }));

  return (
    <form className={s.form} onSubmit={(event) => { event.preventDefault(); onSave(form); }}>
      <div className={s.formRow}>
        <label className={s.field}>
          <span className={s.label}>Name</span>
          <input className={s.input} required value={form.name}
                 onChange={(e) => set({ name: e.target.value })} />
        </label>
        <label className={s.field}>
          <span className={s.label}>Code</span>
          <input className={s.input} required maxLength={20} value={form.code}
                 onChange={(e) => set({ code: e.target.value.toUpperCase() })} />
        </label>
      </div>

      <label className={s.field}>
        <span className={s.label}>Description</span>
        <textarea className={s.textarea} value={form.description}
                  onChange={(e) => set({ description: e.target.value })} />
      </label>

      <div className={s.formRow}>
        <label className={s.field}>
          <span className={s.label}>Department</span>
          <select className={s.select} value={form.departmentId}
                  onChange={(e) => set({ departmentId: e.target.value })}>
            <option value="">None</option>
            {reference.departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </select>
        </label>

        <label className={s.field}>
          <span className={s.label}>Team lead</span>
          <select className={s.select} value={form.teamLeadId}
                  onChange={(e) => set({ teamLeadId: e.target.value })}>
            <option value="">None</option>
            {reference.users.map((u) => <option key={u.id} value={u.id}>{u.name}</option>)}
          </select>
        </label>
      </div>

      <div className={s.formRow}>
        <label className={s.field}>
          <span className={s.label}>Escalates to</span>
          <select className={s.select} value={form.escalationTeamId}
                  onChange={(e) => set({ escalationTeamId: e.target.value })}>
            <option value="">Nowhere</option>
            {teams.filter((t) => t.id !== team?.id).map((t) => (
              <option key={t.id} value={t.id}>{t.name}</option>
            ))}
          </select>
        </label>

        <label className={s.field}>
          <span className={s.label}>Acceptance timeout (minutes)</span>
          <input className={s.input} type="number" min={1} max={10080}
                 value={form.acceptanceTimeoutMinutes}
                 onChange={(e) => set({ acceptanceTimeoutMinutes: Number(e.target.value) })} />
        </label>
      </div>

      <label className={s.checkbox}>
        <input type="checkbox" checked={form.isActive}
               onChange={(e) => set({ isActive: e.target.checked })} />
        Active
      </label>

      {error ? <p className={s.error}>{error}</p> : null}

      <div className={s.formActions}>
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>Cancel</Button>
        <Button type="submit" size="sm" loading={saving}>{team ? 'Save' : 'Create team'}</Button>
      </div>
    </form>
  );
}

function AddMember({ team, reference, onAdd, saving }) {
  const [userId, setUserId] = useState('');
  const [roleInTeam, setRoleInTeam] = useState('Member');
  const [capacityWeight, setCapacityWeight] = useState('1');

  const alreadyIn = new Set(team.members.map((m) => m.userId));
  const candidates = reference.users.filter((u) => !alreadyIn.has(u.id));

  return (
    <form
      className={s.form}
      onSubmit={(event) => {
        event.preventDefault();
        if (userId) {
          onAdd({ userId, roleInTeam, capacityWeight: Number(capacityWeight) });
          setUserId('');
        }
      }}
    >
      <div className={s.formRow}>
        <label className={s.field}>
          <span className={s.label}>Person</span>
          <select className={s.select} value={userId} onChange={(e) => setUserId(e.target.value)}>
            <option value="">Choose…</option>
            {candidates.map((u) => <option key={u.id} value={u.id}>{u.name}</option>)}
          </select>
        </label>

        <label className={s.field}>
          <span className={s.label}>Role in team</span>
          <select className={s.select} value={roleInTeam}
                  onChange={(e) => setRoleInTeam(e.target.value)}>
            {TEAM_ROLES.map((r) => <option key={r} value={r}>{r}</option>)}
          </select>
        </label>

        <label className={s.field}>
          <span className={s.label}>Capacity weight</span>
          <input className={s.input} type="number" min={0} max={10} step={0.1}
                 value={capacityWeight} onChange={(e) => setCapacityWeight(e.target.value)} />
        </label>
      </div>

      <p className={s.hint}>
        Capacity weight is the member&apos;s relative share of routed work. Zero keeps
        someone on the team but out of the rotation — right for a part-timer or
        somebody on secondment.
      </p>

      <div className={s.formActions}>
        <Button type="submit" size="sm" loading={saving} disabled={!userId}>Add to team</Button>
      </div>
    </form>
  );
}

export function TeamsPage() {
  const toast = useToast();
  const queryClient = useQueryClient();

  const [selectedId, setSelectedId] = useState(null);
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState(false);

  const { data: teams, isPending, isError, error, refetch } = useQuery({
    queryKey: adminKeys.teams(),
    queryFn: adminService.teams.list,
  });

  const { data: reference } = useQuery({
    queryKey: adminKeys.reference(),
    queryFn: adminService.reference,
    staleTime: 300_000,
  });

  const selected = teams?.find((t) => t.id === selectedId) ?? null;

  function invalidate() {
    queryClient.invalidateQueries({ queryKey: ['admin'] });
  }

  const save = useMutation({
    mutationFn: ({ id, body }) => (id
      ? adminService.teams.update(id, body)
      : adminService.teams.create(body)),
    onSuccess: (team) => {
      setCreating(false);
      setEditing(false);
      setSelectedId(team.id);
      invalidate();
      toast.success('Team saved');
    },
  });

  const saveMember = useMutation({
    mutationFn: ({ id, body }) => adminService.teams.saveMember(id, body),
    onSuccess: () => { invalidate(); toast.success('Member added'); },
    onError: (failure) => toast.error('Could not add that person', failure.detail),
  });

  const removeMember = useMutation({
    mutationFn: ({ id, userId }) => adminService.teams.removeMember(id, userId),
    onSuccess: () => { invalidate(); toast.success('Member removed'); },
    onError: (failure) => toast.error('Could not remove them', failure.detail),
  });

  if (isPending) return <LoadingState label="Loading teams" />;
  if (isError) return <ErrorState error={error} onRetry={refetch} title="Could not load teams" />;

  function normalise(form) {
    return {
      ...form,
      departmentId: form.departmentId || null,
      teamLeadId: form.teamLeadId || null,
      escalationTeamId: form.escalationTeamId || null,
    };
  }

  return (
    <>
      <header className={s.header}>
        <div>
          <h2 className={s.title}>Teams</h2>
          <p className={s.subtitle}>
            A team is who work routes to and where it escalates when a target slips.
            Members are deactivated rather than deleted, so tickets routed to somebody
            while they were on the team stay explicable.
          </p>
        </div>

        <div className={s.headerActions}>
          <Button size="sm" onClick={() => { setCreating(true); setSelectedId(null); }}>
            Add a team
          </Button>
        </div>
      </header>

      <div className={`${s.split} ${s.splitWide}`}>
        <Card>
          <div className={s.tableWrap}>
            <table className={s.table}>
              <thead>
                <tr>
                  <th scope="col">Team</th>
                  <th scope="col">Lead</th>
                  <th scope="col">Members</th>
                  <th scope="col">Open</th>
                  <th scope="col">Escalates to</th>
                  <th scope="col"><span className="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                {teams.map((team) => (
                  <tr key={team.id} className={selectedId === team.id ? s.selectedRow : undefined}>
                    <th scope="row">
                      {team.name}
                      {!team.isActive ? <Badge tone="neutral">inactive</Badge> : null}
                      <span className={s.permissionKey}>{team.code}</span>
                    </th>
                    <td>{team.teamLeadName ?? <span className={s.muted}>none</span>}</td>
                    <td>{team.members.length}</td>
                    <td>{team.openTickets || <span className={s.muted}>—</span>}</td>
                    <td className={s.muted}>{team.escalationTeamName ?? '—'}</td>
                    <td className={s.rowActions}>
                      <button
                        type="button"
                        className={s.linkButton}
                        onClick={() => {
                          setSelectedId(team.id === selectedId ? null : team.id);
                          setCreating(false);
                          setEditing(false);
                        }}
                      >
                        {selectedId === team.id ? 'Close' : 'Manage'}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>

        {creating && reference ? (
          <Card>
            <CardHeader title="New team" />
            <CardBody>
              <TeamForm
                teams={teams}
                reference={reference}
                saving={save.isPending}
                error={save.error?.detail}
                onCancel={() => setCreating(false)}
                onSave={(form) => save.mutate({ id: null, body: normalise(form) })}
              />
            </CardBody>
          </Card>
        ) : null}

        {selected && reference ? (
          <div className={s.stack}>
            <Card>
              <CardHeader
                title={selected.name}
                subtitle={selected.description ?? selected.code}
                actions={
                  <Button size="sm" variant="secondary" onClick={() => setEditing((e) => !e)}>
                    {editing ? 'Cancel' : 'Settings'}
                  </Button>
                }
              />
              <CardBody>
                {editing ? (
                  <TeamForm
                    team={selected}
                    teams={teams}
                    reference={reference}
                    saving={save.isPending}
                    error={save.error?.detail}
                    onCancel={() => setEditing(false)}
                    onSave={(form) => save.mutate({ id: selected.id, body: normalise(form) })}
                  />
                ) : (
                  <p className={s.hint}>
                    Unaccepted work is escalated after {selected.acceptanceTimeoutMinutes} minutes
                    {selected.escalationTeamName ? `, to ${selected.escalationTeamName}` : ''}.
                    {selected.openTickets > 0
                      ? ` ${selected.openTickets} open ${selected.openTickets === 1 ? 'ticket' : 'tickets'} routed here.`
                      : ' Nothing open.'}
                  </p>
                )}
              </CardBody>
            </Card>

            <Card>
              <CardHeader title="Members" subtitle="Capacity weight drives routing share" />
              <CardBody className={selected.members.length ? undefined : s.form}>
                {selected.members.length > 0 ? (
                  <div className={s.tableWrap}>
                    <table className={s.table}>
                      <thead>
                        <tr>
                          <th scope="col">Person</th>
                          <th scope="col">Role</th>
                          <th scope="col">Weight</th>
                          <th scope="col">Open</th>
                          <th scope="col"><span className="sr-only">Actions</span></th>
                        </tr>
                      </thead>
                      <tbody>
                        {selected.members.map((member) => (
                          <tr key={member.userId}>
                            <th scope="row">
                              {member.fullName}
                              {!member.isActive ? <Badge tone="neutral">deactivated</Badge> : null}
                              <span className={s.permissionKey}>{member.email}</span>
                            </th>
                            <td>{member.roleInTeam}</td>
                            <td>{member.capacityWeight}</td>
                            <td>{member.openTickets || <span className={s.muted}>—</span>}</td>
                            <td className={s.rowActions}>
                              <button
                                type="button"
                                className={`${s.linkButton} ${s.danger}`}
                                disabled={removeMember.isPending}
                                onClick={() => removeMember.mutate({
                                  id: selected.id,
                                  userId: member.userId,
                                })}
                              >
                                Remove
                              </button>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                ) : (
                  <p className={s.hint}>Nobody is on this team yet.</p>
                )}
              </CardBody>
            </Card>

            <Card>
              <CardHeader title="Add a member" />
              <CardBody>
                <AddMember
                  key={selected.id + selected.members.length}
                  team={selected}
                  reference={reference}
                  saving={saveMember.isPending}
                  onAdd={(body) => saveMember.mutate({ id: selected.id, body })}
                />
              </CardBody>
            </Card>
          </div>
        ) : null}
      </div>
    </>
  );
}
