import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import { knowledgeKeys, knowledgeService } from '@/services/reportingService';
import { api } from '@/services/apiClient';
import { useAuth } from '@/contexts/AuthContext';
import { useToast } from '@/contexts/ToastContext';
import { Button, Card, CardBody, CardHeader, ErrorState, Field, LoadingState } from '@/components/ui';
import s from './ArticleEditorPage.module.css';

/**
 * Who each visibility level exposes the article to.
 *
 * Spelled out rather than left to the label, because this is the one field on the form
 * that can leak something: an internal workaround written for agents reads perfectly
 * plausibly to a requester, and the difference between the first two options is the
 * difference between guidance and a disclosure.
 */
const VISIBILITY = [
  { value: 'Internal', label: 'Internal — support staff only', hint: 'Requesters can never see it. Use for anything naming a server, a credential, or a workaround nobody outside support should attempt.' },
  { value: 'Organization', label: 'Organization — anyone signed in here', hint: 'The usual choice. Requesters in this organization can read it once it is published.' },
  { value: 'Public', label: 'Public — every organization', hint: 'Readable by signed-in users in other tenants too. Only for guidance that carries nothing specific to this organization.' },
];

const BLANK = {
  title: '',
  summary: '',
  content: '',
  categoryId: '',
  visibility: 'Organization',
  tags: '',
  changeNote: '',
};

/** Splits the tag box into a clean list: trimmed, de-duplicated, empties dropped. */
function parseTags(text) {
  const seen = new Set();

  return text
    .split(',')
    .map((tag) => tag.trim())
    .filter((tag) => {
      if (!tag || seen.has(tag.toLowerCase())) {
        return false;
      }

      seen.add(tag.toLowerCase());
      return true;
    });
}

/**
 * Writes a new article, or revises an existing one.
 *
 * <p>One component for both because the fields are identical and the difference is two
 * behaviours rather than two forms: creating asks for no change note because there is
 * nothing to describe a change to, and editing loads the article first.</p>
 *
 * <p>Publishing is deliberately absent. It is a separate permission and a separate act,
 * and it happens from the article itself once somebody has read what they are
 * approving — a Publish button here would let an author send their own draft live from
 * inside the editor without ever seeing it rendered.</p>
 */
export function ArticleEditorPage() {
  const { id } = useParams();
  const editing = Boolean(id);
  const navigate = useNavigate();
  const toast = useToast();
  const queryClient = useQueryClient();
  const { can } = useAuth();

  const [form, setForm] = useState(BLANK);
  const [touched, setTouched] = useState(false);

  // Nothing to wait for when writing something new.
  const [seeded, setSeeded] = useState(!editing);

  const { data: categories = [] } = useQuery({
    queryKey: ['catalog', 'categories'],
    queryFn: () => api.get('/categories'),
  });

  const { data: article, isError, error, refetch } = useQuery({
    queryKey: knowledgeKeys.article(id),
    queryFn: () => knowledgeService.get(id),
    enabled: editing,
  });

  // Seeded from the data rather than from inside the fetch. Reaching the editor from
  // the article means the article is already in the cache, so no fetch runs and a
  // queryFn that filled the form would never fire — which is exactly how this loaded
  // blank the first time. Guarded so a refetch cannot discard what has been typed
  // since.
  useEffect(() => {
    if (!editing || !article || seeded) {
      return;
    }

    setForm({
      title: article.title,
      summary: article.summary,
      content: article.content,
      categoryId: article.categoryId ?? '',
      visibility: article.visibility,
      tags: (article.tags ?? []).join(', '),
      changeNote: '',
    });

    setSeeded(true);
  }, [editing, article, seeded]);

  const set = (patch) => {
    setForm((f) => ({ ...f, ...patch }));
    setTouched(true);
  };

  const save = useMutation({
    mutationFn: (body) => (editing
      ? knowledgeService.update(id, body)
      : knowledgeService.create(body)),
    onSuccess: (article) => {
      queryClient.invalidateQueries({ queryKey: ['knowledge'] });
      toast.success(editing ? 'Article saved' : 'Draft created');

      // Straight to the article, not back to the list: the writer needs to see it
      // rendered, and publishing lives there.
      navigate(`/knowledge/${article.id}`);
    },
    onError: (err) => toast.error(
      editing ? 'Could not save the article' : 'Could not create the article',
      err.detail,
    ),
  });

  const problems = useMemo(() => {
    const found = {};

    if (!form.title.trim()) found.title = 'An article needs a title.';
    if (!form.summary.trim()) found.summary = 'The summary is what people read in search results.';
    if (!form.content.trim()) found.content = 'There is nothing to publish yet.';

    return found;
  }, [form]);

  const valid = Object.keys(problems).length === 0;

  if (editing && isError) {
    return <ErrorState error={error} onRetry={refetch} title="Could not load the article" />;
  }

  if (!seeded) {
    return <LoadingState label="Loading article" />;
  }

  // Guards the address bar, not just the button. The API refuses either way, but a
  // form that submits and then 403s wastes what somebody just wrote.
  if (!can(editing ? 'knowledge.edit' : 'knowledge.create')) {
    return (
      <Card>
        <CardBody>
          <p className={s.denied}>
            {editing
              ? 'Editing an article needs the knowledge.edit permission.'
              : 'Writing an article needs the knowledge.create permission.'}
          </p>
          <Button size="sm" variant="secondary" onClick={() => navigate('/knowledge')}>
            Back to the knowledge base
          </Button>
        </CardBody>
      </Card>
    );
  }

  function submit(event) {
    event.preventDefault();
    setTouched(true);

    if (!valid) {
      return;
    }

    const body = {
      title: form.title.trim(),
      summary: form.summary.trim(),
      content: form.content,
      categoryId: form.categoryId || null,
      visibility: form.visibility,
      tags: parseTags(form.tags),
    };

    save.mutate(editing ? { ...body, changeNote: form.changeNote.trim() || null } : body);
  }

  const chosenVisibility = VISIBILITY.find((v) => v.value === form.visibility);

  return (
    <div className={s.layout}>
      <form className={s.main} onSubmit={submit} noValidate>
        <button type="button" className={s.back} onClick={() => navigate(editing ? `/knowledge/${id}` : '/knowledge')}>
          &larr; {editing ? 'Back to the article' : 'Knowledge base'}
        </button>

        <h2 className={s.title}>{editing ? 'Edit article' : 'Write an article'}</h2>
        <p className={s.subtitle}>
          {editing
            ? 'Saving adds a version. Nothing that was published changes until you publish again.'
            : 'This starts as a draft. Nobody else sees it until somebody with permission publishes it.'}
        </p>

        <Card>
          <CardBody>
            <Field
              label="Title"
              required
              value={form.title}
              error={touched ? problems.title : undefined}
              hint="What somebody would search for, in their words rather than the system's."
              onChange={(e) => set({ title: e.target.value })}
            />

            <div className={s.field}>
              <label className={s.label} htmlFor="article-summary">
                Summary<span className={s.required} aria-hidden="true">*</span>
              </label>
              <textarea
                id="article-summary"
                className={s.textarea}
                rows={2}
                value={form.summary}
                onChange={(e) => set({ summary: e.target.value })}
              />
              <span className={s.hint}>
                One or two sentences. This is the line shown in search results, so it
                should say what the article resolves rather than restate the title.
              </span>
              {touched && problems.summary ? (
                <span className={s.error} role="alert">{problems.summary}</span>
              ) : null}
            </div>

            <div className={s.field}>
              <label className={s.label} htmlFor="article-content">
                Content<span className={s.required} aria-hidden="true">*</span>
              </label>
              <textarea
                id="article-content"
                className={`${s.textarea} ${s.contentArea}`}
                rows={20}
                spellCheck
                value={form.content}
                onChange={(e) => set({ content: e.target.value })}
              />
              <span className={s.hint}>
                Shown as written, with line breaks preserved. It is never parsed as
                markup, so an article cannot inject anything into the page.
              </span>
              {touched && problems.content ? (
                <span className={s.error} role="alert">{problems.content}</span>
              ) : null}
            </div>
          </CardBody>
        </Card>

        <div className={s.actions}>
          <Button type="button" variant="ghost" onClick={() => navigate(editing ? `/knowledge/${id}` : '/knowledge')}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending}>
            {editing ? 'Save changes' : 'Create draft'}
          </Button>
        </div>
      </form>

      <aside className={s.side}>
        <Card>
          <CardHeader title="Filing" subtitle="How people will find it" />
          <CardBody>
            <div className={s.field}>
              <label className={s.label} htmlFor="article-category">Category</label>
              <select
                id="article-category"
                className={s.select}
                value={form.categoryId}
                onChange={(e) => set({ categoryId: e.target.value })}
              >
                <option value="">No category</option>
                {categories.map((category) => (
                  <option key={category.id} value={category.id}>{category.name}</option>
                ))}
              </select>
              <span className={s.hint}>
                Also what the suggestion engine matches on when an agent opens a ticket
                in the same category.
              </span>
            </div>

            <div className={s.field}>
              <label className={s.label} htmlFor="article-tags">Tags</label>
              <input
                id="article-tags"
                className={s.input}
                value={form.tags}
                placeholder="erp, licensing, after-holiday"
                onChange={(e) => set({ tags: e.target.value })}
              />
              <span className={s.hint}>Separated by commas.</span>
            </div>

            {parseTags(form.tags).length > 0 ? (
              <div className={s.chips}>
                {parseTags(form.tags).map((tag) => <span key={tag} className={s.chip}>{tag}</span>)}
              </div>
            ) : null}
          </CardBody>
        </Card>

        <Card>
          <CardHeader title="Who can read it" />
          <CardBody>
            <div className={s.field}>
              <label className={s.label} htmlFor="article-visibility">Visibility</label>
              <select
                id="article-visibility"
                className={s.select}
                value={form.visibility}
                onChange={(e) => set({ visibility: e.target.value })}
              >
                {VISIBILITY.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </div>

            <p className={form.visibility === 'Internal' ? s.warn : s.hint}>
              {chosenVisibility?.hint}
            </p>
          </CardBody>
        </Card>

        {editing ? (
          <Card>
            <CardHeader title="Change note" subtitle="Recorded against this version" />
            <CardBody>
              <textarea
                className={s.textarea}
                rows={3}
                value={form.changeNote}
                placeholder="Added a prevention step for the reopening checklist."
                onChange={(e) => set({ changeNote: e.target.value })}
                aria-label="Change note"
              />
              <span className={s.hint}>
                Optional, and the only thing a reviewer has to tell them what moved
                between two versions.
              </span>
            </CardBody>
          </Card>
        ) : null}
      </aside>
    </div>
  );
}
