import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  DAYS, IMPACTS, PRIORITIES, URGENCIES, adminKeys, adminService, formatTarget,
  minutesToTime, timeToMinutes,
} from '@/services/adminService';
import { useToast } from '@/contexts/ToastContext';
import { Badge, Button, Card, CardBody, CardHeader, ErrorState, LoadingState } from '@/components/ui';
import s from './admin.module.css';

const TABS = [
  { key: 'policies', label: 'SLA policies' },
  { key: 'calendars', label: 'Business calendars' },
];

const DEFAULT_TARGETS = [
  { priority: 'Critical', responseMinutes: 15, resolutionMinutes: 240, warningThresholdPercent: 70 },
  { priority: 'High', responseMinutes: 60, resolutionMinutes: 480, warningThresholdPercent: 70 },
  { priority: 'Medium', responseMinutes: 240, resolutionMinutes: 1440, warningThresholdPercent: 70 },
  { priority: 'Low', responseMinutes: 480, resolutionMinutes: 2880, warningThresholdPercent: 70 },
];

function PolicyForm({ policy, reference, onSave, onCancel, saving, error }) {
  const [form, setForm] = useState(() => (policy
    ? {
        name: policy.name,
        description: policy.description ?? '',
        businessCalendarId: policy.businessCalendarId ?? '',
        categoryId: policy.categoryId ?? '',
        isDefault: policy.isDefault,
        isActive: policy.isActive,
        pauseWhenWaitingOnOthers: policy.pauseWhenWaitingOnOthers,
        targets: PRIORITIES.map((priority) =>
          policy.targets.find((t) => t.priority === priority)
          ?? DEFAULT_TARGETS.find((t) => t.priority === priority)),
      }
    : {
        name: '',
        description: '',
        businessCalendarId: '',
        categoryId: '',
        isDefault: false,
        isActive: true,
        pauseWhenWaitingOnOthers: true,
        targets: DEFAULT_TARGETS,
      }));

  const set = (patch) => setForm((f) => ({ ...f, ...patch }));

  function setTarget(priority, patch) {
    set({
      targets: form.targets.map((t) => (t.priority === priority ? { ...t, ...patch } : t)),
    });
  }

  return (
    <form
      className={s.form}
      onSubmit={(e) => {
        e.preventDefault();
        onSave({
          ...form,
          businessCalendarId: form.businessCalendarId || null,
          categoryId: form.categoryId || null,
          ticketType: null,
        });
      }}
    >
      <label className={s.field}>
        <span className={s.label}>Name</span>
        <input className={s.input} required value={form.name}
               onChange={(e) => set({ name: e.target.value })} />
      </label>

      <div className={s.formRow}>
        <label className={s.field}>
          <span className={s.label}>Business calendar</span>
          <select className={s.select} value={form.businessCalendarId}
                  onChange={(e) => set({ businessCalendarId: e.target.value })}>
            <option value="">Continuous — 24/7</option>
            {reference.businessCalendars.map((c) => (
              <option key={c.id} value={c.id}>{c.name}</option>
            ))}
          </select>
        </label>

        <label className={s.field}>
          <span className={s.label}>Applies to category</span>
          <select className={s.select} value={form.categoryId}
                  onChange={(e) => set({ categoryId: e.target.value })}>
            <option value="">Any category</option>
            {reference.categories.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>
      </div>

      <label className={s.checkbox}>
        <input type="checkbox" checked={form.pauseWhenWaitingOnOthers}
               onChange={(e) => set({ pauseWhenWaitingOnOthers: e.target.checked })} />
        Pause the clock while waiting on the requester or a third party
      </label>

      <label className={s.checkbox}>
        <input type="checkbox" checked={form.isDefault}
               onChange={(e) => set({ isDefault: e.target.checked })} />
        Default policy for this organization
      </label>

      <label className={s.checkbox}>
        <input type="checkbox" checked={form.isActive}
               onChange={(e) => set({ isActive: e.target.checked })} />
        Active
      </label>

      <div>
        <span className={s.label}>Targets</span>
        <div className={s.tableWrap}>
          <table className={s.table}>
            <thead>
              <tr>
                <th scope="col">Priority</th>
                <th scope="col">First response (min)</th>
                <th scope="col">Resolution (min)</th>
                <th scope="col">Warn at (%)</th>
              </tr>
            </thead>
            <tbody>
              {form.targets.map((target) => (
                <tr key={target.priority}>
                  <th scope="row">{target.priority}</th>
                  <td>
                    <input className={s.input} type="number" min={1} required
                           value={target.responseMinutes}
                           onChange={(e) => setTarget(target.priority, {
                             responseMinutes: Number(e.target.value),
                           })} />
                  </td>
                  <td>
                    <input className={s.input} type="number" min={1} required
                           value={target.resolutionMinutes}
                           onChange={(e) => setTarget(target.priority, {
                             resolutionMinutes: Number(e.target.value),
                           })} />
                  </td>
                  <td>
                    <input className={s.input} type="number" min={1} max={99} required
                           value={target.warningThresholdPercent}
                           onChange={(e) => setTarget(target.priority, {
                             warningThresholdPercent: Number(e.target.value),
                           })} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <p className={s.hint}>
        Minutes are counted against the calendar above, not the wall clock. Resolution
        cannot be sooner than first response — that is not a stricter policy, it is one
        that breaches before a reply is even due.
      </p>

      {error ? <p className={s.error}>{error}</p> : null}

      <div className={s.formActions}>
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>Cancel</Button>
        <Button type="submit" size="sm" loading={saving}>Save policy</Button>
      </div>
    </form>
  );
}

const PRIORITY_CLASS = {
  Critical: s.pCritical,
  High: s.pHigh,
  Medium: s.pMedium,
  Low: s.pLow,
};

/**
 * A policy's own impact-by-urgency grid.
 *
 * <p>The whole grid is always shown, but only cells this policy has actually decided
 * are marked as overrides. That distinction is the point of the screen: sixteen
 * priorities with no indication of provenance cannot tell an administrator which ones
 * this policy chose and which it is inheriting, and inheriting is the answer for most
 * of them most of the time.</p>
 */
function PolicyMatrix({ policy, onClose }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState(null);

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: adminKeys.policyPriorityMatrix(policy.id),
    queryFn: () => adminService.sla.policyPriorityMatrix(policy.id),
  });

  const invalidate = () => {
    setDraft(null);
    queryClient.invalidateQueries({ queryKey: adminKeys.policyPriorityMatrix(policy.id) });
  };

  const save = useMutation({
    mutationFn: (cells) => adminService.sla.savePolicyPriorityMatrix(policy.id, {
      cells: cells.map(({ impact, urgency, priority }) => ({ impact, urgency, priority })),
      reason: `Edited from the SLA policy screen for ${policy.name}`,
    }),
    onSuccess: (result) => {
      invalidate();
      toast.success(
        result.hasOverrides
          ? `${result.overriddenCells} ${result.overriddenCells === 1 ? 'cell overrides' : 'cells override'} the organization matrix`
          : 'This policy now follows the organization matrix',
        'Applies to tickets raised from now on',
      );
    },
    onError: (failure) => toast.error('Could not save the matrix', failure.detail),
  });

  const clear = useMutation({
    mutationFn: () => adminService.sla.clearPolicyPriorityMatrix(policy.id),
    onSuccess: () => {
      invalidate();
      toast.success('Overrides cleared', 'This policy follows the organization matrix again');
    },
    onError: (failure) => toast.error('Could not clear the overrides', failure.detail),
  });

  if (isPending) return <LoadingState label="Loading the matrix" />;
  if (isError) return <ErrorState error={error} onRetry={refetch} title="Could not load the matrix" />;

  const cells = draft ?? data.cells;
  const lookup = new Map(cells.map((c) => [`${c.impact}|${c.urgency}`, c]));

  function set(impact, urgency, priority) {
    setDraft(cells.map((c) => (c.impact === impact && c.urgency === urgency
      ? { ...c, priority }
      : c)));
  }

  return (
    <Card>
      <CardHeader
        title={`Priority matrix — ${policy.name}`}
        subtitle={data.hasOverrides
          ? `${data.overriddenCells} of 16 cells decided by this policy, the rest inherited`
          : 'Following the organization matrix entirely'}
        actions={(
          <div className={s.headerActions}>
            {draft ? (
              <>
                <Button size="sm" variant="ghost" onClick={() => setDraft(null)}>Revert</Button>
                <Button size="sm" loading={save.isPending} onClick={() => save.mutate(cells)}>Save</Button>
              </>
            ) : (
              <>
                {data.hasOverrides ? (
                  <Button size="sm" variant="secondary" loading={clear.isPending}
                          onClick={() => clear.mutate()}>
                    Follow the organization matrix
                  </Button>
                ) : null}
                <Button size="sm" variant="ghost" onClick={onClose}>Close</Button>
              </>
            )}
          </div>
        )}
      />
      <CardBody>
        <div className={s.tableWrap}>
          <table className={s.matrix}>
            <thead>
              <tr>
                <th scope="col">Impact \ Urgency</th>
                {URGENCIES.map((u) => <th key={u} scope="col">{u}</th>)}
              </tr>
            </thead>
            <tbody>
              {IMPACTS.map((impact) => (
                <tr key={impact}>
                  <th scope="row">{impact}</th>
                  {URGENCIES.map((urgency) => {
                    const cell = lookup.get(`${impact}|${urgency}`) ?? {};
                    const overridden = cell.source === 'Policy';

                    return (
                      <td key={urgency}>
                        <select
                          className={`${s.matrixSelect} ${PRIORITY_CLASS[cell.priority] ?? ''}`}
                          value={cell.priority ?? 'Medium'}
                          aria-label={`${impact} impact with ${urgency} urgency`
                            + (overridden ? ', overridden by this policy' : ', inherited')}
                          onChange={(e) => set(impact, urgency, e.target.value)}
                        >
                          {PRIORITIES.map((p) => <option key={p} value={p}>{p}</option>)}
                        </select>
                        <span className={overridden ? s.chip : s.muted}
                              style={{ fontSize: 'var(--fs-xs)' }}>
                          {overridden ? 'this policy' : 'inherited'}
                        </span>
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <p className={s.hint} style={{ marginTop: 'var(--s-3)' }}>
          A cell you set to the same value it already inherits is not stored as an
          override — otherwise it would quietly pin the value and this policy would stop
          following later changes to the organization matrix for no visible reason.
          Changes apply to tickets raised from now on; existing tickets keep the priority
          their SLA clock was started against.
        </p>
      </CardBody>
    </Card>
  );
}

function Policies({ reference }) {
  const toast = useToast();
  const queryClient = useQueryClient();

  const [creating, setCreating] = useState(false);
  const [editingId, setEditingId] = useState(null);
  const [matrixId, setMatrixId] = useState(null);

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: adminKeys.slaPolicies(),
    queryFn: adminService.sla.policies,
  });

  const save = useMutation({
    mutationFn: ({ id, body }) => (id
      ? adminService.sla.updatePolicy(id, body)
      : adminService.sla.createPolicy(body)),
    onSuccess: () => {
      setCreating(false);
      setEditingId(null);
      queryClient.invalidateQueries({ queryKey: ['admin'] });
      toast.success('Policy saved', 'Applies to clocks started from now on');
    },
    onError: (failure) => toast.error('Could not save the policy', failure.detail),
  });

  if (isPending) return <LoadingState label="Loading policies" />;
  if (isError) return <ErrorState error={error} onRetry={refetch} title="Could not load policies" />;

  const editing = data.find((p) => p.id === editingId);

  return (
    <div className={s.stack}>
      <Card>
        <CardHeader
          title="SLA policies"
          subtitle="Response and resolution targets, measured against a working calendar"
          actions={<Button size="sm" onClick={() => setCreating(true)}>Add</Button>}
        />

        <div className={s.tableWrap}>
          <table className={s.table}>
            <thead>
              <tr>
                <th scope="col">Policy</th>
                <th scope="col">Calendar</th>
                <th scope="col">Applies to</th>
                <th scope="col">Targets</th>
                <th scope="col">Running</th>
                <th scope="col"><span className="sr-only">Actions</span></th>
              </tr>
            </thead>
            <tbody>
              {data.map((policy) => (
                <tr key={policy.id}>
                  <th scope="row">
                    {policy.name}
                    {policy.isDefault ? <Badge tone="info">default</Badge> : null}
                    {!policy.isActive ? <Badge tone="neutral">inactive</Badge> : null}
                    {!policy.pauseWhenWaitingOnOthers
                      ? <span className={s.permissionKey}>never pauses</span>
                      : null}
                  </th>
                  <td className={policy.businessCalendarName ? undefined : s.muted}>
                    {policy.businessCalendarName ?? '24/7'}
                  </td>
                  <td className={policy.categoryName ? undefined : s.muted}>
                    {policy.categoryName ?? 'any category'}
                  </td>
                  <td>
                    <span className={s.chips}>
                      {policy.targets.map((t) => (
                        <span key={t.priority} className={s.chip}>
                          {t.priority}: {formatTarget(t.responseMinutes)} / {formatTarget(t.resolutionMinutes)}
                        </span>
                      ))}
                    </span>
                  </td>
                  <td>{policy.activeClocks || <span className={s.muted}>—</span>}</td>
                  <td className={s.rowActions}>
                    <button type="button" className={s.linkButton}
                            onClick={() => {
                              setMatrixId(matrixId === policy.id ? null : policy.id);
                              setEditingId(null);
                            }}>
                      Priority matrix
                    </button>
                    <button type="button" className={s.linkButton}
                            onClick={() => {
                              setEditingId(editingId === policy.id ? null : policy.id);
                              setMatrixId(null);
                            }}>
                      Edit
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      {matrixId ? (
        <PolicyMatrix
          policy={data.find((p) => p.id === matrixId)}
          onClose={() => setMatrixId(null)}
        />
      ) : null}

      {creating ? (
        <Card>
          <CardHeader title="New SLA policy" />
          <CardBody>
            <PolicyForm
              reference={reference}
              saving={save.isPending}
              error={save.error?.detail}
              onCancel={() => setCreating(false)}
              onSave={(body) => save.mutate({ id: null, body })}
            />
          </CardBody>
        </Card>
      ) : null}

      {editing ? (
        <Card>
          <CardHeader
            title={`Edit ${editing.name}`}
            subtitle={editing.activeClocks > 0
              ? `${editing.activeClocks} clocks are running against this policy — they keep the deadlines they were given`
              : 'No clocks are running against this policy'}
          />
          <CardBody>
            <PolicyForm
              key={editing.id}
              policy={editing}
              reference={reference}
              saving={save.isPending}
              error={save.error?.detail}
              onCancel={() => setEditingId(null)}
              onSave={(body) => save.mutate({ id: editing.id, body })}
            />
          </CardBody>
        </Card>
      ) : null}
    </div>
  );
}

function CalendarForm({ calendar, onSave, onCancel, saving, error }) {
  const [form, setForm] = useState(() => (calendar
    ? {
        name: calendar.name,
        code: calendar.code,
        timeZoneId: calendar.timeZoneId,
        isDefault: calendar.isDefault,
        isActive: calendar.isActive,
        hours: DAYS.map((day) => {
          const existing = calendar.hours.find((h) => h.dayOfWeek === day);
          return {
            dayOfWeek: day,
            enabled: Boolean(existing),
            start: minutesToTime(existing?.startMinute ?? 540),
            end: minutesToTime(existing?.endMinute ?? 1020),
          };
        }),
      }
    : {
        name: '',
        code: '',
        timeZoneId: 'UTC',
        isDefault: false,
        isActive: true,
        hours: DAYS.map((day) => ({
          dayOfWeek: day,
          enabled: day !== 'Saturday' && day !== 'Sunday',
          start: '09:00',
          end: '17:00',
        })),
      }));

  const set = (patch) => setForm((f) => ({ ...f, ...patch }));

  function setDay(day, patch) {
    set({ hours: form.hours.map((h) => (h.dayOfWeek === day ? { ...h, ...patch } : h)) });
  }

  return (
    <form
      className={s.form}
      onSubmit={(e) => {
        e.preventDefault();
        onSave({
          name: form.name,
          code: form.code,
          timeZoneId: form.timeZoneId,
          isDefault: form.isDefault,
          isActive: form.isActive,
          hours: form.hours
            .filter((h) => h.enabled)
            .map((h) => ({
              dayOfWeek: h.dayOfWeek,
              startMinute: timeToMinutes(h.start),
              endMinute: timeToMinutes(h.end),
            })),
        });
      }}
    >
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
        <label className={s.field}>
          <span className={s.label}>Time zone</span>
          <input className={s.input} required value={form.timeZoneId}
                 placeholder="UTC, Asia/Karachi, Europe/London"
                 onChange={(e) => set({ timeZoneId: e.target.value })} />
        </label>
      </div>

      <div>
        <span className={s.label}>Working hours</span>
        <div className={s.week}>
          {form.hours.map((hour) => (
            <div key={hour.dayOfWeek} className={s.weekRow}>
              <label className={s.checkbox}>
                <input type="checkbox" checked={hour.enabled}
                       onChange={(e) => setDay(hour.dayOfWeek, { enabled: e.target.checked })} />
                <span className={s.weekDay}>{hour.dayOfWeek}</span>
              </label>

              <input className={s.input} type="time" disabled={!hour.enabled} value={hour.start}
                     onChange={(e) => setDay(hour.dayOfWeek, { start: e.target.value })} />
              <input className={s.input} type="time" disabled={!hour.enabled} value={hour.end}
                     onChange={(e) => setDay(hour.dayOfWeek, { end: e.target.value })} />
              <span className={s.muted}>
                {hour.enabled ? `${timeToMinutes(hour.end) - timeToMinutes(hour.start)}m` : 'closed'}
              </span>
            </div>
          ))}
        </div>
      </div>

      <p className={s.hint}>
        A calendar with no working days means continuous cover, not no cover — an empty
        schedule would otherwise make every deadline unreachable.
      </p>

      <label className={s.checkbox}>
        <input type="checkbox" checked={form.isDefault}
               onChange={(e) => set({ isDefault: e.target.checked })} />
        Default calendar
      </label>

      {error ? <p className={s.error}>{error}</p> : null}

      <div className={s.formActions}>
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>Cancel</Button>
        <Button type="submit" size="sm" loading={saving}>Save calendar</Button>
      </div>
    </form>
  );
}

function Calendars() {
  const toast = useToast();
  const queryClient = useQueryClient();

  const [creating, setCreating] = useState(false);
  const [editingId, setEditingId] = useState(null);
  const [holidayFor, setHolidayFor] = useState(null);
  const [holiday, setHoliday] = useState({ name: '', dateUtc: '', isRecurring: false });

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: adminKeys.calendars(),
    queryFn: adminService.sla.calendars,
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['admin'] });

  const save = useMutation({
    mutationFn: ({ id, body }) => (id
      ? adminService.sla.updateCalendar(id, body)
      : adminService.sla.createCalendar(body)),
    onSuccess: () => { setCreating(false); setEditingId(null); invalidate(); toast.success('Calendar saved'); },
    onError: (failure) => toast.error('Could not save the calendar', failure.detail),
  });

  const addHoliday = useMutation({
    mutationFn: ({ id, body }) => adminService.sla.addHoliday(id, body),
    onSuccess: () => {
      setHolidayFor(null);
      setHoliday({ name: '', dateUtc: '', isRecurring: false });
      invalidate();
      toast.success('Holiday added');
    },
    onError: (failure) => toast.error('Could not add that day', failure.detail),
  });

  const removeHoliday = useMutation({
    mutationFn: ({ id, holidayId }) => adminService.sla.removeHoliday(id, holidayId),
    onSuccess: () => { invalidate(); toast.success('Holiday removed'); },
  });

  if (isPending) return <LoadingState label="Loading calendars" />;
  if (isError) return <ErrorState error={error} onRetry={refetch} title="Could not load calendars" />;

  const editing = data.find((c) => c.id === editingId);

  return (
    <div className={s.stack}>
      <Card>
        <CardHeader
          title="Business calendars"
          subtitle="Working hours and holidays, which is what SLA minutes are counted against"
          actions={<Button size="sm" onClick={() => setCreating(true)}>Add</Button>}
        />

        <div className={s.tableWrap}>
          <table className={s.table}>
            <thead>
              <tr>
                <th scope="col">Calendar</th>
                <th scope="col">Time zone</th>
                <th scope="col">Working days</th>
                <th scope="col">Holidays</th>
                <th scope="col">Policies</th>
                <th scope="col"><span className="sr-only">Actions</span></th>
              </tr>
            </thead>
            <tbody>
              {data.map((calendar) => (
                <tr key={calendar.id}>
                  <th scope="row">
                    {calendar.name}
                    {calendar.isDefault ? <Badge tone="info">default</Badge> : null}
                    <span className={s.permissionKey}>{calendar.code}</span>
                  </th>
                  <td className={s.mono}>{calendar.timeZoneId}</td>
                  <td>
                    {calendar.hours.length === 0 ? (
                      <span className={s.muted}>continuous</span>
                    ) : (
                      <span className={s.chips}>
                        {calendar.hours.map((h) => (
                          <span key={h.dayOfWeek} className={s.chip}>
                            {h.dayOfWeek.slice(0, 3)} {minutesToTime(h.startMinute)}–{minutesToTime(h.endMinute)}
                          </span>
                        ))}
                      </span>
                    )}
                  </td>
                  <td>{calendar.holidays.length || <span className={s.muted}>—</span>}</td>
                  <td>{calendar.policiesUsing || <span className={s.muted}>—</span>}</td>
                  <td className={s.rowActions}>
                    <button type="button" className={s.linkButton}
                            onClick={() => setEditingId(editingId === calendar.id ? null : calendar.id)}>
                      Edit
                    </button>
                    <button type="button" className={s.linkButton}
                            onClick={() => setHolidayFor(calendar.id)}>
                      Holidays
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
          <CardHeader title="New calendar" />
          <CardBody>
            <CalendarForm
              saving={save.isPending}
              error={save.error?.detail}
              onCancel={() => setCreating(false)}
              onSave={(body) => save.mutate({ id: null, body })}
            />
          </CardBody>
        </Card>
      ) : null}

      {editing ? (
        <Card>
          <CardHeader title={`Edit ${editing.name}`} />
          <CardBody>
            <CalendarForm
              key={editing.id}
              calendar={editing}
              saving={save.isPending}
              error={save.error?.detail}
              onCancel={() => setEditingId(null)}
              onSave={(body) => save.mutate({ id: editing.id, body })}
            />
          </CardBody>
        </Card>
      ) : null}

      {holidayFor ? (
        <Card>
          <CardHeader
            title="Holidays"
            subtitle={data.find((c) => c.id === holidayFor)?.name}
            actions={
              <Button size="sm" variant="ghost" onClick={() => setHolidayFor(null)}>Close</Button>
            }
          />
          <CardBody>
            {(data.find((c) => c.id === holidayFor)?.holidays ?? []).length > 0 ? (
              <div className={s.tableWrap}>
                <table className={s.table}>
                  <thead>
                    <tr>
                      <th scope="col">Day</th>
                      <th scope="col">Date</th>
                      <th scope="col">Repeats</th>
                      <th scope="col"><span className="sr-only">Actions</span></th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.find((c) => c.id === holidayFor).holidays.map((h) => (
                      <tr key={h.id}>
                        <th scope="row">{h.name}</th>
                        <td>{new Date(h.dateUtc).toLocaleDateString()}</td>
                        <td className={s.muted}>{h.isRecurring ? 'every year' : 'once'}</td>
                        <td className={s.rowActions}>
                          <button type="button" className={`${s.linkButton} ${s.danger}`}
                                  onClick={() => removeHoliday.mutate({
                                    id: holidayFor, holidayId: h.id,
                                  })}>
                            Remove
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <p className={s.hint}>No holidays on this calendar yet.</p>
            )}

            <form
              className={s.form}
              style={{ marginTop: 'var(--s-3)' }}
              onSubmit={(e) => {
                e.preventDefault();
                addHoliday.mutate({
                  id: holidayFor,
                  body: {
                    name: holiday.name,
                    dateUtc: new Date(`${holiday.dateUtc}T00:00:00Z`).toISOString(),
                    isRecurring: holiday.isRecurring,
                  },
                });
              }}
            >
              <div className={s.formRow}>
                <label className={s.field}>
                  <span className={s.label}>Name</span>
                  <input className={s.input} required value={holiday.name}
                         onChange={(e) => setHoliday((h) => ({ ...h, name: e.target.value }))} />
                </label>
                <label className={s.field}>
                  <span className={s.label}>Date</span>
                  <input className={s.input} type="date" required value={holiday.dateUtc}
                         onChange={(e) => setHoliday((h) => ({ ...h, dateUtc: e.target.value }))} />
                </label>
              </div>

              <label className={s.checkbox}>
                <input type="checkbox" checked={holiday.isRecurring}
                       onChange={(e) => setHoliday((h) => ({ ...h, isRecurring: e.target.checked }))} />
                Repeats every year
              </label>

              <div className={s.formActions}>
                <Button type="submit" size="sm" loading={addHoliday.isPending}>Add holiday</Button>
              </div>
            </form>
          </CardBody>
        </Card>
      ) : null}
    </div>
  );
}

export function SlaPoliciesPage() {
  const [tab, setTab] = useState('policies');

  const { data: reference } = useQuery({
    queryKey: adminKeys.reference(),
    queryFn: adminService.reference,
    staleTime: 300_000,
  });

  return (
    <>
      <header className={s.header}>
        <div>
          <h2 className={s.title}>SLA policies and calendars</h2>
          <p className={s.subtitle}>
            Targets are counted in working minutes against a calendar, with weekends,
            holidays and daylight-saving transitions handled. Editing a policy changes
            what future clocks get; running clocks keep the deadline they were given.
          </p>
        </div>
      </header>

      <div className={s.tabs} role="tablist" aria-label="SLA sections">
        {TABS.map((item) => (
          <button
            key={item.key}
            type="button"
            role="tab"
            aria-selected={tab === item.key}
            className={`${s.tab} ${tab === item.key ? s.tabActive : ''}`}
            onClick={() => setTab(item.key)}
          >
            {item.label}
          </button>
        ))}
      </div>

      {tab === 'policies'
        ? (reference ? <Policies reference={reference} /> : <LoadingState label="Loading" />)
        : <Calendars />}
    </>
  );
}
