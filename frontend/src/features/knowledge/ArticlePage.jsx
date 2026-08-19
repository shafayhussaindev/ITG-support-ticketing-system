import { useEffect } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import { knowledgeKeys, knowledgeService } from '@/services/reportingService';
import { useToast } from '@/contexts/ToastContext';
import {
  Badge, Button, Card, CardBody, CardHeader, EmptyState, ErrorState, LoadingState,
} from '@/components/ui';
import { formatDateTime, formatRelative } from '@/utils/datetime';
import s from './ArticlePage.module.css';

const ACTION_LABEL = {
  InReview: 'Send for review',
  Published: 'Publish',
  Archived: 'Archive',
};

export function ArticlePage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const queryClient = useQueryClient();

  const { data: article, isPending, isError, error, refetch } = useQuery({
    queryKey: knowledgeKeys.article(id),
    queryFn: () => knowledgeService.get(id),
  });

  const { data: versions } = useQuery({
    queryKey: knowledgeKeys.versions(id),
    queryFn: () => knowledgeService.versions(id),
    enabled: Boolean(article),
  });

  // Recorded once per article. The counter is a rough popularity signal, not an
  // analytics event, so a re-render must not inflate it.
  useEffect(() => {
    if (article?.id) {
      knowledgeService.recordView(article.id).catch(() => {
        // A failed view count must never disrupt reading the article.
      });
    }
  }, [article?.id]);

  const vote = useMutation({
    mutationFn: (wasHelpful) => knowledgeService.feedback(id, { wasHelpful }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: knowledgeKeys.article(id) });
      toast.success('Thanks for the feedback');
    },
    onError: (err) => toast.error('Could not record that', err.detail),
  });

  const changeStatus = useMutation({
    mutationFn: (status) => knowledgeService.changeStatus(id, { status }),
    onSuccess: (updated) => {
      queryClient.invalidateQueries({ queryKey: knowledgeKeys.article(id) });
      queryClient.invalidateQueries({ queryKey: ['knowledge', 'search'] });
      toast.success(`Article is now ${updated.status.toLowerCase()}`);
    },
    onError: (err) => toast.error('Could not change the status', err.detail),
  });

  if (isPending) {
    return <LoadingState label="Loading article" />;
  }

  if (isError) {
    return error?.status === 404 ? (
      <Card>
        <CardBody>
          <EmptyState
            icon="⌕"
            title="Article not found"
            message="It may be a draft, or staff-only guidance you are not able to read."
            actions={<Button size="sm" onClick={() => navigate('/knowledge')}>Back to the knowledge base</Button>}
          />
        </CardBody>
      </Card>
    ) : (
      <ErrorState error={error} onRetry={refetch} title="Could not load the article" />
    );
  }

  // Edit is a navigation, not a status change, so it is split out rather than
  // dropped — it was filtered away entirely while there was no editor to reach.
  const canEdit = article.availableActions.includes('Edit');
  const statusActions = article.availableActions.filter((a) => a !== 'Edit');

  return (
    <div className={s.layout}>
      <article className={s.main}>
        <button type="button" className={s.back} onClick={() => navigate('/knowledge')}>
          &larr; Knowledge base
        </button>

        <div className={s.badges}>
          {article.status === 'Published'
            ? <Badge tone="success">Published</Badge>
            : (
              <Badge tone={article.status === 'InReview' ? 'warning' : 'neutral'}>
                {article.status === 'InReview' ? 'In review' : article.status}
              </Badge>
            )}

          {article.visibility === 'Internal' ? (
            <Badge tone="warning">Staff only &mdash; never paste this into a reply</Badge>
          ) : null}

          {article.categoryName ? <Badge tone="neutral">{article.categoryName}</Badge> : null}
        </div>

        <h2 className={s.title}>{article.title}</h2>
        <p className={s.summary}>{article.summary}</p>

        <p className={s.byline}>
          By {article.authorName} &middot; version {article.currentVersion} &middot;{' '}
          <time dateTime={article.publishedAtUtc ?? article.createdAtUtc}>
            {formatRelative(article.publishedAtUtc ?? article.createdAtUtc)}
          </time>
          {' '}&middot; {article.viewCount} views
        </p>

        {/* Rendered as pre-wrapped text, never parsed as markup. Article content is
            user input, and injecting it as HTML would be a stored-XSS route. */}
        <div className={s.content}>{article.content}</div>

        {article.tags?.length ? (
          <div className={s.tags}>
            {article.tags.map((tag) => <span key={tag} className={s.tag}>{tag}</span>)}
          </div>
        ) : null}

        <Card className={s.voteCard}>
          <CardBody>
            <div className={s.voteRow}>
              <span className={s.voteLabel}>
                {article.myVoteWasHelpful === null || article.myVoteWasHelpful === undefined
                  ? 'Did this solve your problem?'
                  : article.myVoteWasHelpful
                    ? 'You marked this helpful.'
                    : 'You marked this not helpful.'}
              </span>

              <div className={s.voteButtons}>
                <Button
                  size="sm"
                  variant={article.myVoteWasHelpful === true ? 'primary' : 'secondary'}
                  loading={vote.isPending}
                  onClick={() => vote.mutate(true)}
                >
                  Yes ({article.helpfulCount})
                </Button>
                <Button
                  size="sm"
                  variant={article.myVoteWasHelpful === false ? 'danger' : 'secondary'}
                  loading={vote.isPending}
                  onClick={() => vote.mutate(false)}
                >
                  No ({article.notHelpfulCount})
                </Button>
              </div>
            </div>
          </CardBody>
        </Card>
      </article>

      <aside className={s.side}>
        {canEdit ? (
          <Card>
            <CardHeader title="Revise" subtitle="Saving adds a version and keeps the old one" />
            <CardBody>
              <Button
                size="sm"
                fullWidth
                variant="secondary"
                onClick={() => navigate(`/knowledge/${article.id}/edit`)}
              >
                Edit this article
              </Button>
            </CardBody>
          </Card>
        ) : null}

        {statusActions.length > 0 ? (
          <Card>
            <CardHeader title="Lifecycle" subtitle="Publishing is a separate permission from editing" />
            <CardBody className={s.actions}>
              {statusActions.map((action) => (
                <Button
                  key={action}
                  size="sm"
                  fullWidth
                  variant={action === 'Published' ? 'primary' : 'secondary'}
                  loading={changeStatus.isPending}
                  onClick={() => changeStatus.mutate(action)}
                >
                  {ACTION_LABEL[action] ?? action}
                </Button>
              ))}
            </CardBody>
          </Card>
        ) : null}

        {versions?.length ? (
          <Card>
            <CardHeader
              title="History"
              subtitle={`${versions.length} version${versions.length === 1 ? '' : 's'}`}
            />
            <CardBody>
              <ol className={s.versions}>
                {versions.map((version) => (
                  <li key={version.version}>
                    <span className={s.versionNo}>v{version.version}</span>
                    <span className={s.versionMeta}>
                      {version.changedByName ?? 'Unknown'}{' '}&middot;{' '}
                      <time title={formatDateTime(version.changedAtUtc)}>
                        {formatRelative(version.changedAtUtc)}
                      </time>
                    </span>
                    {version.changeNote ? (
                      <span className={s.versionNote}>{version.changeNote}</span>
                    ) : null}
                  </li>
                ))}
              </ol>
            </CardBody>
          </Card>
        ) : null}

        {article.sourceTicketId ? (
          <Card>
            <CardHeader title="Written from" />
            <CardBody>
              <Button
                size="sm"
                variant="secondary"
                fullWidth
                onClick={() => navigate(`/tickets/${article.sourceTicketId}`)}
              >
                View the source ticket
              </Button>
            </CardBody>
          </Card>
        ) : null}
      </aside>
    </div>
  );
}
