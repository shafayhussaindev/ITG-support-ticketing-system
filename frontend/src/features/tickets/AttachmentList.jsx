import { useState } from 'react';
import { Badge } from '@/components/ui';
import { tokenStore } from '@/services/tokenStore';
import s from './AttachmentList.module.css';

/** Bytes in the units a person reads, rather than the ones a computer stores. */
export function formatSize(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5180/api/v1';

export function isImage(type) {
  return typeof type === 'string' && type.startsWith('image/');
}

export function isVideo(type) {
  return typeof type === 'string' && type.startsWith('video/');
}

/**
 * Fetches an attachment as a blob and hands back an object URL.
 *
 * The obvious approach — putting the download URL straight into a src attribute —
 * cannot work here: the access token lives in memory rather than in a cookie, and an
 * img or video element issues its own request with no Authorization header. It would
 * arrive unauthenticated and be refused.
 *
 * That constraint is the point of the design, not an accident of it. A token in a
 * cookie would be sent automatically by every request the browser makes, including
 * ones a hostile page provoked.
 */
async function fetchBlobUrl(ticketId, attachmentId) {
  const response = await fetch(`${BASE_URL}/tickets/${ticketId}/attachments/${attachmentId}`, {
    headers: { Authorization: `Bearer ${tokenStore.getAccessToken()}` },
  });

  if (!response.ok) {
    throw new Error(`The file could not be loaded (${response.status}).`);
  }

  return URL.createObjectURL(await response.blob());
}

/** Loads a preview on demand, so a thread of ten recordings does not fetch ten videos. */
function Preview({ ticketId, attachment }) {
  const [url, setUrl] = useState(null);
  const [state, setState] = useState('idle');

  async function load() {
    setState('loading');

    try {
      setUrl(await fetchBlobUrl(ticketId, attachment.id));
      setState('ready');
    } catch {
      setState('failed');
    }
  }

  if (state === 'ready' && url) {
    return isImage(attachment.contentType) ? (
      <img className={s.preview} src={url} alt={attachment.fileName} />
    ) : (
      // Controls only, no autoplay: a support thread that starts playing sound
      // because somebody scrolled past it is an unwelcome surprise.
      <video className={s.preview} src={url} controls preload="metadata" />
    );
  }

  return (
    <button
      type="button"
      className={s.previewButton}
      onClick={load}
      disabled={state === 'loading'}
    >
      {state === 'loading'
        ? 'Loading…'
        : state === 'failed'
          ? 'Could not load — try downloading it'
          : isVideo(attachment.contentType)
            ? '▶ Play recording'
            : 'Show image'}
    </button>
  );
}

function AttachmentRow({ ticketId, attachment, onDelete, canDelete }) {
  const [downloading, setDownloading] = useState(false);
  const previewable = isImage(attachment.contentType) || isVideo(attachment.contentType);

  async function download() {
    setDownloading(true);

    try {
      const url = await fetchBlobUrl(ticketId, attachment.id);
      const link = document.createElement('a');

      link.href = url;
      link.download = attachment.fileName;
      document.body.appendChild(link);
      link.click();
      link.remove();

      // Revoked at once: an object URL holds the whole file in memory until it is,
      // and a thread of screen recordings would keep every one of them alive.
      URL.revokeObjectURL(url);
    } finally {
      setDownloading(false);
    }
  }

  return (
    <li className={s.item}>
      <div className={s.head}>
        <span className={s.icon} aria-hidden="true">
          {isImage(attachment.contentType) ? '🖼' : isVideo(attachment.contentType) ? '🎬' : '📎'}
        </span>

        <div className={s.meta}>
          <span className={s.name}>{attachment.fileName}</span>
          <span className={s.detail}>
            {formatSize(attachment.sizeBytes)}
            {attachment.uploadedByName ? ` · ${attachment.uploadedByName}` : ''}
          </span>
        </div>

        {attachment.isInternalOnly ? <Badge tone="warning">internal</Badge> : null}

        <div className={s.actions}>
          <button type="button" className={s.action} onClick={download} disabled={downloading}>
            {downloading ? 'Saving…' : 'Download'}
          </button>

          {canDelete ? (
            <button
              type="button"
              className={`${s.action} ${s.danger}`}
              onClick={() => onDelete(attachment.id)}
            >
              Remove
            </button>
          ) : null}
        </div>
      </div>

      {previewable ? <Preview ticketId={ticketId} attachment={attachment} /> : null}
    </li>
  );
}

export function AttachmentList({ ticketId, attachments, onDelete, canDelete = false }) {
  if (!attachments || attachments.length === 0) {
    return null;
  }

  return (
    <ul className={s.list}>
      {attachments.map((attachment) => (
        <AttachmentRow
          key={attachment.id}
          ticketId={ticketId}
          attachment={attachment}
          onDelete={onDelete}
          canDelete={canDelete}
        />
      ))}
    </ul>
  );
}
