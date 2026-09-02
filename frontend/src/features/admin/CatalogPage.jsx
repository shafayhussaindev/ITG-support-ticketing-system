import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { IMPACTS, PRIORITIES, URGENCIES, adminKeys, adminService } from '@/services/adminService';
import { useToast } from '@/contexts/ToastContext';
import { Badge, Button, Card, CardBody, CardHeader, ErrorState, LoadingState } from '@/components/ui';
import s from './admin.module.css';

const TABS = [
  { key: 'categories', label: 'Categories' },
  { key: 'applications', label: 'Applications' },
  { key: 'matrix', label: 'Priority matrix' },
];

const PRIORITY_CLASS = {
  Critical: s.pCritical,
  High: s.pHigh,
  Medium: s.pMedium,
  Low: s.pLow,
};

const BLANK_CATEGORY = {
  name: '', code: '', description: '', defaultTeamId: '', slaPolicyId: '',
  displayOrder: 0, isActive: true, isInternalOnly: false,
};

const BLANK_SUBCATEGORY = {
  name: '', code: '', description: '', defaultTeamId: '', defaultImpact: '',
  displayOrder: 0, isActive: true,
};

const BLANK_APPLICATION = {
  name: '', code: '', description: '', vendor: '', version: '',
  owningTeamId: '', isBusinessCritical: false, isActive: true,
};

function CategoryForm({ category, reference, onSave, onCancel, saving, error }) {
  const [form, setForm] = useState(() => (category
    ? {
        name: category.name,
        code: category.code,
        description: category.description ?? '',
        defaultTeamId: category.defaultTeamId ?? '',
        slaPolicyId: category.slaPolicyId ?? '',
        displayOrder: category.displayOrder,
        isActive: category.isActive,
        isInternalOnly: category.isInternalOnly,
      }
    : BLANK_CATEGORY));

  const set = (patch) => setForm((f) => ({ ...f, ...patch }));

  return (
    <form className={s.form} onSubmit={(e) => { e.preventDefault(); onSave(form); }}>
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

      <div className={s.formRow}>
        <label className={s.field}>
          <span className={s.label}>Routes to team</span>
          <select className={s.select} value={form.defaultTeamId}
                  onChange={(e) => set({ defaultTeamId: e.target.value })}>
            <option value="">Unrouted</option>
            {reference.teams.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
          </select>
        </label>

        <label className={s.field}>
          <span className={s.label}>SLA policy</span>
          <select className={s.select} value={form.slaPolicyId}
                  onChange={(e) => set({ slaPolicyId: e.target.value })}>
            <option value="">Organization default</option>
            {reference.slaPolicies.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
          </select>
        </label>

        <label className={s.field}>
          <span className={s.label}>Display order</span>
          <input className={s.input} type="number" value={form.displayOrder}
                 onChange={(e) => set({ displayOrder: Number(e.target.value) })} />
        </label>
      </div>

      <label className={s.checkbox}>
        <input type="checkbox" checked={form.isActive}
               onChange={(e) => set({ isActive: e.target.checked })} />
        Active — offered on the raise-a-ticket form
      </label>

      <label className={s.checkbox}>
        <input type="checkbox" checked={form.isInternalOnly}
               onChange={(e) => set({ isInternalOnly: e.target.checked })} />
        Internal only — staff can file here, requesters cannot
      </label>

      {error ? <p className={s.error}>{error}</p> : null}

      <div className={s.formActions}>
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>Cancel</Button>
        <Button type="submit" size="sm" loading={saving}>Save</Button>
      </div>
    </form>
  );
}

function SubcategoryForm({ categoryId, reference, onSave, onCancel, saving }) {
  const [form, setForm] = useState(BLANK_SUBCATEGORY);
  const set = (patch) => setForm((f) => ({ ...f, ...patch }));

  return (
    <form
      className={s.form}
      onSubmit={(e) => {
        e.preventDefault();
        onSave({
          ...form,
          categoryId,
          defaultTeamId: form.defaultTeamId || null,
          defaultImpact: form.defaultImpact || null,
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
          <span className={s.label}>Default impact</span>
          <select className={s.select} value={form.defaultImpact}
                  onChange={(e) => set({ defaultImpact: e.target.value })}>
            <option value="">None</option>
            {IMPACTS.map((i) => <option key={i} value={i}>{i}</option>)}
          </select>
        </label>
      </div>

      <label className={s.field}>
        <span className={s.label}>Routes to team</span>
        <select className={s.select} value={form.defaultTeamId}
                onChange={(e) => set({ defaultTeamId: e.target.value })}>
          <option value="">Inherit from the category</option>
          {reference.teams.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
        </select>
      </label>

      <div className={s.formActions}>
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>Cancel</Button>
        <Button type="submit" size="sm" loading={saving}>Add subcategory</Button>
      </div>
    </form>
  );
}

function Categories({ reference }) {
  const toast = useToast();
  const queryClient = useQueryClient();

  const [editingId, setEditingId] = useState(null);
  const [creating, setCreating] = useState(false);
  const [addingChildTo, setAddingChildTo] = useState(null);

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: adminKeys.categories(),
    queryFn: adminService.catalog.categories,
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['admin'] });

  const save = useMutation({
    mutationFn: ({ id, body }) => (id
      ? adminService.catalog.updateCategory(id, body)
      : adminService.catalog.createCategory(body)),
    onSuccess: () => { setEditingId(null); setCreating(false); invalidate(); toast.success('Saved'); },
  });

  const addChild = useMutation({
    mutationFn: (body) => adminService.catalog.createSubcategory(body),
    onSuccess: () => { setAddingChildTo(null); invalidate(); toast.success('Subcategory added'); },
    onError: (failure) => toast.error('Could not add that', failure.detail),
  });

  const remove = useMutation({
    mutationFn: (id) => adminService.catalog.deleteCategory(id),
    onSuccess: () => { invalidate(); toast.success('Category archived'); },
    onError: (failure) => toast.error('Could not remove it', failure.detail),
  });

  if (isPending) return <LoadingState label="Loading categories" />;
  if (isError) return <ErrorState error={error} onRetry={refetch} title="Could not load categories" />;

  function normalise(form) {
    return {
      ...form,
      defaultTeamId: form.defaultTeamId || null,
      slaPolicyId: form.slaPolicyId || null,
    };
  }

  return (
    <div className={s.stack}>
      <Card>
        <CardHeader
          title="Categories"
          subtitle="What a requester picks, and what decides routing and SLA"
          actions={<Button size="sm" onClick={() => setCreating(true)}>Add</Button>}
        />

        <div className={s.tableWrap}>
          <table className={s.table}>
            <thead>
              <tr>
                <th scope="col">Category</th>
                <th scope="col">Routes to</th>
                <th scope="col">SLA</th>
                <th scope="col">Subcategories</th>
                <th scope="col">Tickets</th>
                <th scope="col"><span className="sr-only">Actions</span></th>
              </tr>
            </thead>
            <tbody>
              {data.map((category) => (
                <tr key={category.id}>
                  <th scope="row">
                    {category.name}
                    {!category.isActive ? <Badge tone="neutral">inactive</Badge> : null}
                    {category.isInternalOnly ? <Badge tone="info">internal</Badge> : null}
                    <span className={s.permissionKey}>{category.code}</span>
                  </th>
                  <td className={category.defaultTeamName ? undefined : s.muted}>
                    {category.defaultTeamName ?? 'unrouted'}
                  </td>
                  <td className={category.slaPolicyName ? undefined : s.muted}>
                    {category.slaPolicyName ?? 'default'}
                  </td>
                  <td>
                    <span className={s.chips}>
                      {category.subcategories.length === 0
                        ? <span className={s.muted}>none</span>
                        : category.subcategories.map((sub) => (
                            <span key={sub.id} className={s.chip}>{sub.name}</span>
                          ))}
                    </span>
                  </td>
                  <td>{category.ticketCount || <span className={s.muted}>—</span>}</td>
                  <td className={s.rowActions}>
                    <button type="button" className={s.linkButton}
                            onClick={() => setEditingId(editingId === category.id ? null : category.id)}>
                      Edit
                    </button>
                    <button type="button" className={s.linkButton}
                            onClick={() => setAddingChildTo(category.id)}>
                      + Sub
                    </button>
                    {category.ticketCount === 0 ? (
                      <button type="button" className={`${s.linkButton} ${s.danger}`}
                              onClick={() => remove.mutate(category.id)}>
                        Delete
                      </button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      {creating ? (
        <Card>
          <CardHeader title="New category" />
          <CardBody>
            <CategoryForm
              reference={reference}
              saving={save.isPending}
              error={save.error?.detail}
              onCancel={() => setCreating(false)}
              onSave={(form) => save.mutate({ id: null, body: normalise(form) })}
            />
          </CardBody>
        </Card>
      ) : null}

      {editingId ? (
        <Card>
          <CardHeader title="Edit category" />
          <CardBody>
            <CategoryForm
              key={editingId}
              category={data.find((c) => c.id === editingId)}
              reference={reference}
              saving={save.isPending}
              error={save.error?.detail}
              onCancel={() => setEditingId(null)}
              onSave={(form) => save.mutate({ id: editingId, body: normalise(form) })}
            />
          </CardBody>
        </Card>
      ) : null}

      {addingChildTo ? (
        <Card>
          <CardHeader
            title="New subcategory"
            subtitle={data.find((c) => c.id === addingChildTo)?.name}
          />
          <CardBody>
            <SubcategoryForm
              key={addingChildTo}
              categoryId={addingChildTo}
              reference={reference}
              saving={addChild.isPending}
              onCancel={() => setAddingChildTo(null)}
              onSave={(body) => addChild.mutate(body)}
            />
          </CardBody>
        </Card>
      ) : null}
    </div>
  );
}

/*
  Declared at module scope on purpose. As a function nested inside Applications it
  was re-created on every parent render, and React treats a new function as a new
  component type: the form unmounted and remounted, and whatever the user had typed
  vanished each time a query refetched behind it.
*/
function ApplicationForm({ application, reference, saving, onSave, onCancel }) {
  const [form, setForm] = useState(() => (application
    ? {
        name: application.name,
        code: application.code,
        description: '',
        vendor: application.vendor ?? '',
        version: application.version ?? '',
        owningTeamId: application.owningTeamId ?? '',
        isBusinessCritical: application.isBusinessCritical,
        isActive: application.isActive,
      }
    : BLANK_APPLICATION));

  const set = (patch) => setForm((f) => ({ ...f, ...patch }));

  return (
    <form
      className={s.form}
      onSubmit={(e) => {
        e.preventDefault();
        onSave({ ...form, owningTeamId: form.owningTeamId || null });
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
      </div>

      <div className={s.formRow}>
        <label className={s.field}>
          <span className={s.label}>Vendor</span>
          <input className={s.input} value={form.vendor}
                 onChange={(e) => set({ vendor: e.target.value })} />
        </label>
        <label className={s.field}>
          <span className={s.label}>Version</span>
          <input className={s.input} value={form.version}
                 onChange={(e) => set({ version: e.target.value })} />
        </label>
        <label className={s.field}>
          <span className={s.label}>Owning team</span>
          <select className={s.select} value={form.owningTeamId}
                  onChange={(e) => set({ owningTeamId: e.target.value })}>
            <option value="">None</option>
            {reference.teams.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
          </select>
        </label>
      </div>

      <label className={s.checkbox}>
        <input type="checkbox" checked={form.isBusinessCritical}
               onChange={(e) => set({ isBusinessCritical: e.target.checked })} />
        Business critical
      </label>

      <label className={s.checkbox}>
        <input type="checkbox" checked={form.isActive}
               onChange={(e) => set({ isActive: e.target.checked })} />
        Active
      </label>

      <div className={s.formActions}>
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>Cancel</Button>
        <Button type="submit" size="sm" loading={saving}>Save</Button>
      </div>
    </form>
  );
}

function Applications({ reference }) {
  const toast = useToast();
  const queryClient = useQueryClient();

  const [creating, setCreating] = useState(false);
  const [editingId, setEditingId] = useState(null);
  const [moduleFor, setModuleFor] = useState(null);
  const [moduleForm, setModuleForm] = useState({ name: '', code: '' });

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: adminKeys.applications(),
    queryFn: adminService.catalog.applications,
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['admin'] });

  const save = useMutation({
    mutationFn: ({ id, body }) => (id
      ? adminService.catalog.updateApplication(id, body)
      : adminService.catalog.createApplication(body)),
    onSuccess: () => { setCreating(false); setEditingId(null); invalidate(); toast.success('Saved'); },
  });

  const addModule = useMutation({
    mutationFn: (body) => adminService.catalog.createModule(body),
    onSuccess: () => {
      setModuleFor(null);
      setModuleForm({ name: '', code: '' });
      invalidate();
      toast.success('Module added');
    },
  });

  if (isPending) return <LoadingState label="Loading applications" />;
  if (isError) return <ErrorState error={error} onRetry={refetch} title="Could not load applications" />;

  const editing = data.find((a) => a.id === editingId);

  return (
    <div className={s.stack}>
      <Card>
        <CardHeader
          title="Applications"
          subtitle="The systems tickets are raised against, and their modules"
          actions={<Button size="sm" onClick={() => setCreating(true)}>Add</Button>}
        />

        <div className={s.tableWrap}>
          <table className={s.table}>
            <thead>
              <tr>
                <th scope="col">Application</th>
                <th scope="col">Vendor</th>
                <th scope="col">Owning team</th>
                <th scope="col">Modules</th>
                <th scope="col"><span className="sr-only">Actions</span></th>
              </tr>
            </thead>
            <tbody>
              {data.map((application) => (
                <tr key={application.id}>
                  <th scope="row">
                    {application.name}
                    {application.isBusinessCritical ? <Badge tone="danger">critical</Badge> : null}
                    {!application.isActive ? <Badge tone="neutral">inactive</Badge> : null}
                    <span className={s.permissionKey}>{application.code}</span>
                  </th>
                  <td className={s.muted}>
                    {application.vendor ?? '—'}
                    {application.version ? ` ${application.version}` : ''}
                  </td>
                  <td className={application.owningTeamName ? undefined : s.muted}>
                    {application.owningTeamName ?? 'none'}
                  </td>
                  <td>
                    <span className={s.chips}>
                      {application.modules.length === 0
                        ? <span className={s.muted}>none</span>
                        : application.modules.map((m) => (
                            <span key={m.id} className={s.chip}>{m.name}</span>
                          ))}
                    </span>
                  </td>
                  <td className={s.rowActions}>
                    <button type="button" className={s.linkButton}
                            onClick={() => setEditingId(editingId === application.id ? null : application.id)}>
                      Edit
                    </button>
                    <button type="button" className={s.linkButton}
                            onClick={() => setModuleFor(application.id)}>
                      + Module
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
          <CardHeader title="New application" />
          <CardBody>
            <ApplicationForm
              reference={reference}
              saving={save.isPending}
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
            <ApplicationForm
              reference={reference}
              saving={save.isPending}
              key={editing.id}
              application={editing}
              onCancel={() => setEditingId(null)}
              onSave={(body) => save.mutate({ id: editing.id, body })}
            />
          </CardBody>
        </Card>
      ) : null}

      {moduleFor ? (
        <Card>
          <CardHeader title="New module" subtitle={data.find((a) => a.id === moduleFor)?.name} />
          <CardBody>
            <form
              className={s.form}
              onSubmit={(e) => {
                e.preventDefault();
                addModule.mutate({ ...moduleForm, applicationId: moduleFor, displayOrder: 0 });
              }}
            >
              <div className={s.formRow}>
                <label className={s.field}>
                  <span className={s.label}>Name</span>
                  <input className={s.input} required value={moduleForm.name}
                         onChange={(e) => setModuleForm((f) => ({ ...f, name: e.target.value }))} />
                </label>
                <label className={s.field}>
                  <span className={s.label}>Code</span>
                  <input className={s.input} required maxLength={20} value={moduleForm.code}
                         onChange={(e) => setModuleForm((f) => ({
                           ...f, code: e.target.value.toUpperCase(),
                         }))} />
                </label>
              </div>

              <div className={s.formActions}>
                <Button type="button" size="sm" variant="ghost"
                        onClick={() => setModuleFor(null)}>Cancel</Button>
                <Button type="submit" size="sm" loading={addModule.isPending}>Add module</Button>
              </div>
            </form>
          </CardBody>
        </Card>
      ) : null}
    </div>
  );
}

/**
 * The impact-by-urgency grid.
 *
 * Edited as a grid because that is what it is. A list of sixteen rows would be
 * technically equivalent and would hide the thing an administrator is actually
 * checking: that the diagonal makes sense and nothing is inconsistent with its
 * neighbours.
 */
function PriorityMatrix() {
  const toast = useToast();
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState(null);

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: adminKeys.priorityMatrix(),
    queryFn: adminService.catalog.priorityMatrix,
  });

  useEffect(() => {
    if (data) setDraft(null);
  }, [data]);

  const save = useMutation({
    mutationFn: (cells) => adminService.catalog.savePriorityMatrix({
      cells,
      reason: 'Edited from the administration screen',
    }),
    onSuccess: () => {
      setDraft(null);
      queryClient.invalidateQueries({ queryKey: adminKeys.priorityMatrix() });
      toast.success('Matrix saved', 'Applies to tickets raised from now on');
    },
    onError: (failure) => toast.error('Could not save the matrix', failure.detail),
  });

  if (isPending) return <LoadingState label="Loading the matrix" />;
  if (isError) return <ErrorState error={error} onRetry={refetch} title="Could not load the matrix" />;

  const cells = draft ?? data;
  const lookup = new Map(cells.map((c) => [`${c.impact}|${c.urgency}`, c.priority]));

  function set(impact, urgency, priority) {
    setDraft(cells.map((c) => (c.impact === impact && c.urgency === urgency
      ? { ...c, priority }
      : c)));
  }

  return (
    <Card>
      <CardHeader
        title="Priority matrix"
        subtitle="Impact down, urgency across"
        actions={draft ? (
          <div className={s.headerActions}>
            <Button size="sm" variant="ghost" onClick={() => setDraft(null)}>Revert</Button>
            <Button size="sm" loading={save.isPending} onClick={() => save.mutate(cells)}>Save</Button>
          </div>
        ) : null}
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
                    const priority = lookup.get(`${impact}|${urgency}`);
                    return (
                      <td key={urgency}>
                        <select
                          className={`${s.matrixSelect} ${PRIORITY_CLASS[priority] ?? ''}`}
                          value={priority}
                          aria-label={`${impact} impact with ${urgency} urgency`}
                          onChange={(e) => set(impact, urgency, e.target.value)}
                        >
                          {PRIORITIES.map((p) => <option key={p} value={p}>{p}</option>)}
                        </select>
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <p className={s.hint} style={{ marginTop: 'var(--s-3)' }}>
          Nothing in the code maps impact and urgency to a priority — the calculator
          reads these rows. Changes apply to tickets raised from now on: existing
          tickets keep the priority they were given, because their SLA clocks were
          started against it.
        </p>
      </CardBody>
    </Card>
  );
}

export function CatalogPage() {
  const [tab, setTab] = useState('categories');

  const { data: reference } = useQuery({
    queryKey: adminKeys.reference(),
    queryFn: adminService.reference,
    staleTime: 300_000,
  });

  return (
    <>
      <header className={s.header}>
        <div>
          <h2 className={s.title}>Categories and applications</h2>
          <p className={s.subtitle}>
            The catalogue a requester chooses from, and the grid that turns their
            answers into a priority. Categories in use are archived rather than
            deleted, so a ticket raised last year still says what it always said.
          </p>
        </div>
      </header>

      <div className={s.tabs} role="tablist" aria-label="Catalogue sections">
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

      {!reference ? (
        <LoadingState label="Loading" />
      ) : tab === 'categories' ? (
        <Categories reference={reference} />
      ) : tab === 'applications' ? (
        <Applications reference={reference} />
      ) : (
        <PriorityMatrix />
      )}
    </>
  );
}
