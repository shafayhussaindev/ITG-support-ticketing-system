import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ticketKeys, ticketService } from '@/services/ticketService';
import { useAuth } from '@/contexts/AuthContext';
import { useToast } from '@/contexts/ToastContext';
import { Button, Card, CardBody, CardHeader, EmptyState, Skeleton } from '@/components/ui';
import { formatDateTime, formatRelative } from '@/utils/datetime';
import { AttachmentList, formatSize } from './AttachmentList';
import s from './Conversation.module.css';

function initials(name = '') {
  return name.split(' ').filter(Boolean).slice(0, 2).map((p) => p[0]?.toUpperCase()).join('');
}

export function Conversation({ ticketId, ticketStatus }) {
  const { can, user } = useAuth();
  const toast = useToast();
  const queryClient = useQueryClient();

  const [body, setBody] = useState('');
  const [isInternal, setIsInternal] = useState(false);
  const [staged, setStaged] = useState([]);
  const [dragging, setDragging] = useState(false);

  const canWriteNote = can('ticket.internal_note');
  const canAttach = can('attachment.upload');
  const isClosed = ['Closed', 'Cancelled'].includes(ticketStatus);

  const { data: comments, isPending, isError, refetch } = useQuery({
    queryKey: ticketKeys.comments(ticketId),
    queryFn: () => ticketService.comments(ticketId),
  });

  const { data: attachments } = useQuery({
    queryKey: ticketKeys.attachments(ticketId),
    queryFn: () => ticketService.attachments(ticketId),
  });

  const removeAttachment = useMutation({
    mutationFn: (attachmentId) => ticketService.deleteAttachment(ticketId, attachmentId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ticketKeys.attachments(ticketId) });
      toast.success('Attachment removed');
    },
    onError: (error) => toast.error('Could not remove it', error.detail),
  });

  const addComment = useMutation({
    mutationFn: async () => {
      const comment = await ticketService.addComment(ticketId, { body: body.trim(), isInternal });

      // Uploaded after the comment exists, so each file is tied to the message it
      // arrived with — and so an upload that fails leaves a posted reply rather than
      // silently losing what the person wrote.
      for (const file of staged) {
        await ticketService.uploadAttachment(ticketId, file, {
          commentId: comment.id,
          isInternalOnly: isInternal,
        });
      }

      return comment;
    },
    onSuccess: () => {
      setBody('');
      setStaged([]);
      queryClient.invalidateQueries({ queryKey: ticketKeys.attachments(ticketId) });
      queryClient.invalidateQueries({ queryKey: ticketKeys.comments(ticketId) });
      queryClient.invalidateQueries({ queryKey: ticketKeys.detail(ticketId) });
      queryClient.invalidateQueries({ queryKey: ticketKeys.timeline(ticketId) });
      toast.success(isInternal ? 'Internal note added' : 'Reply sent');
    },
    onError: (error) => toast.error('Could not post that', error.detail ?? 'Please try again.'),
  });

  return (
    <Card>
      <CardHeader
        title="Conversation"
        subtitle={comments ? `${comments.length} message${comments.length === 1 ? '' : 's'}` : undefined}
      />

      <CardBody className={s.body}>
        {isPending ? (
          <div className={s.stack}>
            <Skeleton height={62} />
            <Skeleton height={62} />
          </div>
        ) : isError ? (
          <EmptyState
            icon="⚠"
            title="Could not load the conversation"
            actions={<Button size="sm" variant="secondary" onClick={refetch}>Try again</Button>}
          />
        ) : comments.length === 0 ? (
          <EmptyState
            icon="○"
            title="Nothing here yet"
            message="Replies and notes will appear in the order they were written."
          />
        ) : (
          <ol className={s.thread}>
            {comments.map((comment) => {
              const internal = comment.type === 'InternalNote';
              const mine = comment.authorId === user?.id;

              return (
                <li
                  key={comment.id}
                  className={[s.message, internal ? s.internal : '', mine ? s.mine : '']
                    .filter(Boolean)
                    .join(' ')}
                >
                  <span className={s.avatar} aria-hidden="true">{initials(comment.authorName)}</span>

                  <div className={s.bubble}>
                    <div className={s.messageHead}>
                      <span className={s.author}>{comment.authorName ?? 'System'}</span>

                      {internal ? (
                        // Labelled unmistakably: an agent must never be unsure whether
                        // what they are reading is visible to the requester.
                        <span className={s.internalTag}>Internal note — not visible to the requester</span>
                      ) : null}

                      {comment.isFirstResponse ? (
                        <span className={s.firstResponse}>First response</span>
                      ) : null}

                      <time
                        className={s.time}
                        dateTime={comment.createdAtUtc}
                        title={formatDateTime(comment.createdAtUtc)}
                      >
                        {formatRelative(comment.createdAtUtc)}
                      </time>
                    </div>

                    <p className={s.text}>{comment.body}</p>
                  </div>
                </li>
              );
            })}
          </ol>
        )}

        {attachments && attachments.length > 0 ? (
          <div className={s.attachments}>
            <p className={s.attachmentsTitle}>
              {attachments.length} {attachments.length === 1 ? 'attachment' : 'attachments'}
            </p>
            <AttachmentList
              ticketId={ticketId}
              attachments={attachments}
              canDelete={can('attachment.delete')}
              onDelete={(id) => removeAttachment.mutate(id)}
            />
          </div>
        ) : null}

        {isClosed ? (
          <p className={s.closedNote}>
            This ticket is {ticketStatus.toLowerCase()}. Reopen it to continue the conversation.
          </p>
        ) : (
          <form
            className={[
              s.composer,
              isInternal ? s.composerInternal : '',
              dragging ? s.composerDragging : '',
            ].filter(Boolean).join(' ')}
            onSubmit={(e) => {
              e.preventDefault();
              if (body.trim()) {
                addComment.mutate();
              }
            }}
            onDragOver={(e) => { e.preventDefault(); setDragging(true); }}
            onDragLeave={() => setDragging(false)}
            onDrop={(e) => {
              e.preventDefault();
              setDragging(false);
              setStaged((current) => [...current, ...Array.from(e.dataTransfer.files)]);
            }}
          >
            {canWriteNote ? (
              <div className={s.modeRow} role="radiogroup" aria-label="Message type">
                <button
                  type="button"
                  role="radio"
                  aria-checked={!isInternal}
                  className={`${s.mode} ${!isInternal ? s.modeActive : ''}`}
                  onClick={() => setIsInternal(false)}
                >
                  Public reply
                </button>
                <button
                  type="button"
                  role="radio"
                  aria-checked={isInternal}
                  className={`${s.mode} ${isInternal ? s.modeActiveInternal : ''}`}
                  onClick={() => setIsInternal(true)}
                >
                  Internal note
                </button>
              </div>
            ) : null}

            <label className="sr-only" htmlFor="comment-body">
              {isInternal ? 'Internal note' : 'Reply'}
            </label>
            <textarea
              id="comment-body"
              className={s.textarea}
              rows={3}
              value={body}
              onChange={(e) => setBody(e.target.value)}
              placeholder={
                isInternal
                  ? 'Context for colleagues. The requester will never see this.'
                  : 'Write a reply the requester will see…'
              }
            />

            {staged.length > 0 ? (
              <ul className={s.staged}>
                {staged.map((file, index) => (
                  <li key={`${file.name}-${index}`} className={s.stagedItem}>
                    <span className={s.stagedName}>{file.name}</span>
                    <span className={s.stagedSize}>{formatSize(file.size)}</span>
                    <button
                      type="button"
                      className={s.stagedRemove}
                      aria-label={`Remove ${file.name}`}
                      onClick={() => setStaged((c) => c.filter((_, i) => i !== index))}
                    >
                      &times;
                    </button>
                  </li>
                ))}
              </ul>
            ) : null}

            <div className={s.composerActions}>
              {canAttach ? (
                <label className={s.attach}>
                  <input
                    type="file"
                    multiple
                    className="sr-only"
                    accept="image/*,video/*,.pdf,.zip,.log,.txt,.csv"
                    onChange={(e) => {
                      setStaged((c) => [...c, ...Array.from(e.target.files ?? [])]);

                      // Cleared so choosing the same file twice in a row still fires
                      // a change event the second time.
                      e.target.value = '';
                    }}
                  />
                  📎 Attach
                </label>
              ) : null}

              {isInternal ? (
                <span className={s.warning}>Staff only — the requester cannot see this</span>
              ) : (
                <span className={s.hint}>Visible to the requester</span>
              )}

              <Button
                type="submit"
                size="sm"
                variant={isInternal ? 'secondary' : 'primary'}
                loading={addComment.isPending}
                disabled={!body.trim()}
              >
                {staged.length > 0
                  ? `${isInternal ? 'Add note' : 'Send reply'} with ${staged.length} file${staged.length === 1 ? '' : 's'}`
                  : isInternal ? 'Add note' : 'Send reply'}
              </Button>
            </div>
          </form>
        )}
      </CardBody>
    </Card>
  );
}
