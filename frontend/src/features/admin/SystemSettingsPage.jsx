import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { adminKeys, adminService } from '@/services/adminService';
import { useToast } from '@/contexts/ToastContext';
import { Badge, Button, Card, CardBody, CardHeader, EmptyState, ErrorState, LoadingState } from '@/components/ui';
import { formatDateTime } from '@/utils/datetime';
import s from './admin.module.css';

const VALUE_TYPES = ['string', 'int', 'bool', 'decimal', 'json'];

const BLANK = {
  key: '',
  value: '',
  valueType: 'string',
  description: '',
  category: '',
  isSensitive: false,
};

function SettingForm({ setting, onSave, onCancel, saving, error }) {
  const [form, setForm] = useState(() => (setting
    ? {
        key: setting.key,
        value: setting.value,
        valueType: setting.valueType,
        description: setting.description ?? '',
        category: setting.category ?? '',
        isSensitive: setting.isSensitive,
      }
    : BLANK));

  const set = (patch) => setForm((f) => ({ ...f, ...patch }));
  const untouchedSecret = setting?.isSensitive && form.value === setting.value;

  return (
    <form className={s.form} onSubmit={(e) => { e.preventDefault(); onSave(form); }}>
      <div className={s.formRow}>
        <label className={s.field}>
          <span className={s.label}>Key</span>
          <input
            className={s.input}
            required
            readOnly={Boolean(setting)}
            value={form.key}
            placeholder="Integration.Erp.BaseUrl"
            onChange={(e) => set({ key: e.target.value })}
          />
        </label>

        <label className={s.field}>
          <span className={s.label}>Type</span>
          <select className={s.select} value={form.valueType}
                  onChange={(e) => set({ valueType: e.target.value })}>
            {VALUE_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
          </select>
        </label>

        <label className={s.field}>
          <span className={s.label}>Category</span>
          <input className={s.input} value={form.category}
                 onChange={(e) => set({ category: e.target.value })} />
        </label>
      </div>

      <label className={s.field}>
        <span className={s.label}>Value</span>
        {form.valueType === 'bool' ? (
          <select className={s.select} value={form.value}
                  onChange={(e) => set({ value: e.target.value })}>
            <option value="true">true</option>
            <option value="false">false</option>
          </select>
        ) : form.valueType === 'json' ? (
          <textarea className={s.textarea} value={form.value}
                    onChange={(e) => set({ value: e.target.value })} />
        ) : (
          <input className={s.input} value={form.value}
                 onChange={(e) => set({ value: e.target.value })} />
        )}
      </label>

      {untouchedSecret ? (
        <p className={s.hint}>
          The stored value is masked and is not sent to the browser. Saving without
          typing a new one leaves it exactly as it is.
        </p>
      ) : null}

      <label className={s.field}>
        <span className={s.label}>Description</span>
        <input className={s.input} value={form.description}
               onChange={(e) => set({ description: e.target.value })} />
      </label>

      <label className={s.checkbox}>
        <input type="checkbox" checked={form.isSensitive}
               onChange={(e) => set({ isSensitive: e.target.checked })} />
        Sensitive — mask the value everywhere and keep it out of the audit trail
      </label>

      {error ? <p className={s.error}>{error}</p> : null}

      <div className={s.formActions}>
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>Cancel</Button>
        <Button type="submit" size="sm" loading={saving}>Save</Button>
      </div>
    </form>
  );
}

export function SystemSettingsPage() {
  const toast = useToast();
  const queryClient = useQueryClient();

  const [creating, setCreating] = useState(false);
  const [editingId, setEditingId] = useState(null);

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: adminKeys.settings(),
    queryFn: adminService.settings.list,
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: adminKeys.settings() });

  const save = useMutation({
    mutationFn: (body) => adminService.settings.save(body),
    onSuccess: () => { setCreating(false); setEditingId(null); invalidate(); toast.success('Setting saved'); },
    onError: (failure) => toast.error('Could not save that', failure.detail),
  });

  const remove = useMutation({
    mutationFn: (id) => adminService.settings.remove(id),
    onSuccess: () => { invalidate(); toast.success('Override removed'); },
  });

  if (isPending) return <LoadingState label="Loading settings" />;
  if (isError) return <ErrorState error={error} onRetry={refetch} title="Could not load settings" />;

  const editing = data.find((setting) => setting.id === editingId);
  const grouped = new Map();

  data.forEach((setting) => {
    const key = setting.category ?? 'General';
    grouped.set(key, [...(grouped.get(key) ?? []), setting]);
  });

  return (
    <>
      <header className={s.header}>
        <div>
          <h2 className={s.title}>System settings</h2>
          <p className={s.subtitle}>
            Runtime configuration you can change without a deployment. Saving always
            writes a row owned by this organization — global defaults are never edited
            from here, because that would silently change every other tenant.
          </p>
        </div>

        <div className={s.headerActions}>
          <Button size="sm" onClick={() => { setCreating(true); setEditingId(null); }}>
            Add a setting
          </Button>
        </div>
      </header>

      <div className={s.notice} style={{ marginBottom: 'var(--s-4)' }}>
        Secrets that the application itself needs — the JWT signing key, the database
        connection string, the AI provider key — are <strong>not</strong> here. They
        come from environment variables or user-secrets and never touch the database or
        the browser. AI capabilities are switched on at{' '}
        <Link to="/admin/ai">Administration → AI assistance</Link>.
      </div>

      <div className={s.stack}>
        {data.length === 0 ? (
          <EmptyState
            icon="⚙"
            title="Nothing configured"
            message="No organization-level settings have been created yet."
          />
        ) : (
          [...grouped.entries()].map(([category, settings]) => (
            <Card key={category}>
              <CardHeader title={category} />
              <div className={s.tableWrap}>
                <table className={s.table}>
                  <thead>
                    <tr>
                      <th scope="col">Key</th>
                      <th scope="col">Value</th>
                      <th scope="col">Type</th>
                      <th scope="col">Changed</th>
                      <th scope="col"><span className="sr-only">Actions</span></th>
                    </tr>
                  </thead>
                  <tbody>
                    {settings.map((setting) => (
                      <tr key={setting.id}>
                        <th scope="row">
                          <span className={s.mono}>{setting.key}</span>
                          {setting.isSystemManaged ? <Badge tone="warning">system</Badge> : null}
                          {!setting.isOrganizationOverride
                            ? <Badge tone="neutral">global default</Badge>
                            : null}
                          {setting.description
                            ? <span className={s.permissionKey}>{setting.description}</span>
                            : null}
                        </th>
                        <td className={s.mono}>{setting.value}</td>
                        <td className={s.muted}>{setting.valueType}</td>
                        <td className={s.muted}>
                          {setting.updatedAtUtc ? formatDateTime(setting.updatedAtUtc) : '—'}
                        </td>
                        <td className={s.rowActions}>
                          <button
                            type="button"
                            className={s.linkButton}
                            onClick={() => {
                              setEditingId(editingId === setting.id ? null : setting.id);
                              setCreating(false);
                            }}
                          >
                            Edit
                          </button>
                          {setting.isOrganizationOverride ? (
                            <button
                              type="button"
                              className={`${s.linkButton} ${s.danger}`}
                              onClick={() => remove.mutate(setting.id)}
                            >
                              Revert
                            </button>
                          ) : null}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </Card>
          ))
        )}

        {creating ? (
          <Card>
            <CardHeader title="New setting" />
            <CardBody>
              <SettingForm
                saving={save.isPending}
                error={save.error?.detail}
                onCancel={() => setCreating(false)}
                onSave={(form) => save.mutate(form)}
              />
            </CardBody>
          </Card>
        ) : null}

        {editing ? (
          <Card>
            <CardHeader title={editing.key} subtitle={editing.description ?? undefined} />
            <CardBody>
              <SettingForm
                key={editing.id}
                setting={editing}
                saving={save.isPending}
                error={save.error?.detail}
                onCancel={() => setEditingId(null)}
                onSave={(form) => save.mutate(form)}
              />
            </CardBody>
          </Card>
        ) : null}
      </div>
    </>
  );
}
