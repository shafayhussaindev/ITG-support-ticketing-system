import { useEffect, useId, useRef } from 'react';
import s from './ui.module.css';

/* ------------------------------------------------------------------ Button */

export function Button({
  variant = 'primary',
  size = 'md',
  fullWidth = false,
  loading = false,
  disabled = false,
  children,
  className = '',
  ...rest
}) {
  const sizeClass = { sm: s.sizeSm, md: s.sizeMd, lg: s.sizeLg }[size] ?? s.sizeMd;

  return (
    <button
      className={[s.button, s[variant], sizeClass, fullWidth ? s.fullWidth : '', className]
        .filter(Boolean)
        .join(' ')}
      disabled={disabled || loading}
      // Tells assistive technology the control is busy rather than simply disabled.
      aria-busy={loading || undefined}
      {...rest}
    >
      {loading ? <Spinner size={14} /> : null}
      {children}
    </button>
  );
}

/* ------------------------------------------------------------------- Field */

/**
 * A labelled input wired for accessibility: the label is associated by id, the
 * error is announced, and aria-invalid marks the control itself.
 */
export function Field({
  label,
  error,
  hint,
  required = false,
  type = 'text',
  inputRef,
  ...rest
}) {
  const id = useId();
  const errorId = `${id}-error`;
  const hintId = `${id}-hint`;

  const describedBy = [error ? errorId : null, hint ? hintId : null].filter(Boolean).join(' ');

  return (
    <div className={s.field}>
      <label className={s.label} htmlFor={id}>
        {label}
        {required ? (
          <span className={s.required} aria-hidden="true">
            *
          </span>
        ) : null}
      </label>

      <input
        id={id}
        ref={inputRef}
        type={type}
        className={[s.input, error ? s.inputInvalid : ''].filter(Boolean).join(' ')}
        aria-invalid={error ? 'true' : undefined}
        aria-describedby={describedBy || undefined}
        aria-required={required || undefined}
        {...rest}
      />

      {hint ? (
        <span id={hintId} className={s.hint}>
          {hint}
        </span>
      ) : null}

      {error ? (
        <span id={errorId} className={s.error} role="alert">
          {error}
        </span>
      ) : null}
    </div>
  );
}

/* -------------------------------------------------------------------- Card */

export function Card({ children, className = '', ...rest }) {
  return (
    <section className={`${s.card} ${className}`} {...rest}>
      {children}
    </section>
  );
}

export function CardHeader({ title, subtitle, actions }) {
  return (
    <header className={s.cardHeader}>
      <div>
        <h2 className={s.cardTitle}>{title}</h2>
        {subtitle ? <p className={s.cardSubtitle}>{subtitle}</p> : null}
      </div>
      {actions ? <div>{actions}</div> : null}
    </header>
  );
}

export function CardBody({ children, className = '' }) {
  return <div className={`${s.cardBody} ${className}`}>{children}</div>;
}

/* ------------------------------------------------------------------- Badge */

export function Badge({ tone = 'neutral', dot = false, children }) {
  const toneClass =
    {
      neutral: s.badgeNeutral,
      info: s.badgeInfo,
      success: s.badgeSuccess,
      warning: s.badgeWarning,
      danger: s.badgeDanger,
      primary: s.badgePrimary,
    }[tone] ?? s.badgeNeutral;

  return (
    <span className={`${s.badge} ${toneClass}`}>
      {dot ? <span className={s.badgeDot} aria-hidden="true" /> : null}
      {children}
    </span>
  );
}

/* ------------------------------------------------------- Spinner, Skeleton */

export function Spinner({ size = 16, label }) {
  return (
    <>
      <span
        className={s.spinner}
        style={{ width: size, height: size }}
        aria-hidden="true"
      />
      {label ? <span className="sr-only">{label}</span> : null}
    </>
  );
}

export function Skeleton({ width = '100%', height = 14, radius, style }) {
  return (
    <span
      className={s.skeleton}
      style={{ display: 'block', width, height, borderRadius: radius, ...style }}
      aria-hidden="true"
    />
  );
}

/* ------------------------------------------------------------------ States */

export function EmptyState({ icon = '○', title, message, actions }) {
  return (
    <div className={s.state}>
      <span className={s.stateIcon} aria-hidden="true">
        {icon}
      </span>
      <p className={s.stateTitle}>{title}</p>
      {message ? <p className={s.stateMessage}>{message}</p> : null}
      {actions ? <div className={s.stateActions}>{actions}</div> : null}
    </div>
  );
}

export function ErrorState({ error, onRetry, title = 'Something went wrong' }) {
  const detail = error?.detail ?? error?.message ?? 'An unexpected error occurred.';

  return (
    <div className={s.state} role="alert">
      <span className={s.stateIcon} aria-hidden="true">
        ⚠
      </span>
      <p className={s.stateTitle}>{title}</p>
      <p className={s.stateMessage}>{detail}</p>

      {onRetry ? (
        <div className={s.stateActions}>
          <Button variant="secondary" size="sm" onClick={onRetry}>
            Try again
          </Button>
        </div>
      ) : null}

      {/* Surfaced so a user can quote it to support and it can be found in the logs. */}
      {error?.correlationId ? (
        <p className={s.stateCode}>Reference: {error.correlationId}</p>
      ) : null}
    </div>
  );
}

export function LoadingState({ label = 'Loading' }) {
  return (
    <div className={s.state}>
      <Spinner size={22} label={label} />
      <p className={s.stateMessage}>{label}…</p>
    </div>
  );
}

/* ------------------------------------------------------------------ Dialog */

export function ConfirmDialog({
  open,
  title,
  message,
  confirmLabel = 'Confirm',
  cancelLabel = 'Cancel',
  variant = 'primary',
  loading = false,
  onConfirm,
  onCancel,
}) {
  const confirmRef = useRef(null);
  const titleId = useId();

  // Move focus into the dialog on open, and let Escape close it. Without this a
  // keyboard user's focus stays behind the backdrop with no way out.
  useEffect(() => {
    if (!open) {
      return undefined;
    }

    confirmRef.current?.focus();

    function onKeyDown(event) {
      if (event.key === 'Escape' && !loading) {
        onCancel?.();
      }
    }

    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [open, loading, onCancel]);

  if (!open) {
    return null;
  }

  return (
    <div
      className={s.backdrop}
      onMouseDown={(event) => {
        if (event.target === event.currentTarget && !loading) {
          onCancel?.();
        }
      }}
    >
      <div className={s.dialog} role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <h2 className={s.dialogTitle} id={titleId}>
          {title}
        </h2>
        {message ? <p className={s.dialogMessage}>{message}</p> : null}

        <div className={s.dialogActions}>
          <Button variant="secondary" size="sm" onClick={onCancel} disabled={loading}>
            {cancelLabel}
          </Button>
          <Button ref={confirmRef} variant={variant} size="sm" onClick={onConfirm} loading={loading}>
            {confirmLabel}
          </Button>
        </div>
      </div>
    </div>
  );
}
