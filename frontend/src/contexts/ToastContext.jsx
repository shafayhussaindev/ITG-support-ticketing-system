import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import styles from './ToastContext.module.css';

const ToastContext = createContext(null);

const DEFAULT_DURATION = 5000;

export function ToastProvider({ children }) {
  const [toasts, setToasts] = useState([]);
  const timers = useRef(new Map());

  const dismiss = useCallback((id) => {
    setToasts((current) => current.filter((toast) => toast.id !== id));

    const timer = timers.current.get(id);
    if (timer) {
      clearTimeout(timer);
      timers.current.delete(id);
    }
  }, []);

  const push = useCallback(
    ({ title, description, variant = 'info', duration = DEFAULT_DURATION }) => {
      const id = globalThis.crypto?.randomUUID?.() ?? String(Date.now() + Math.random());

      setToasts((current) => [...current, { id, title, description, variant }]);

      // Errors stay until dismissed. A message explaining why something failed
      // should not disappear while the reader is still working out what to do.
      if (duration > 0 && variant !== 'error') {
        timers.current.set(id, setTimeout(() => dismiss(id), duration));
      }

      return id;
    },
    [dismiss],
  );

  useEffect(() => {
    const pending = timers.current;
    return () => {
      for (const timer of pending.values()) {
        clearTimeout(timer);
      }
      pending.clear();
    };
  }, []);

  const value = useMemo(
    () => ({
      push,
      dismiss,
      success: (title, description) => push({ title, description, variant: 'success' }),
      error: (title, description) => push({ title, description, variant: 'error' }),
      info: (title, description) => push({ title, description, variant: 'info' }),
      warning: (title, description) => push({ title, description, variant: 'warning' }),
    }),
    [push, dismiss],
  );

  return (
    <ToastContext.Provider value={value}>
      {children}

      {/*
        aria-live="polite" announces new toasts to a screen reader without
        interrupting whatever it is currently reading.
      */}
      <div className={styles.region} role="region" aria-label="Notifications">
        <ol className={styles.list} aria-live="polite" aria-atomic="false">
          {toasts.map((toast) => (
            <li key={toast.id} className={`${styles.toast} ${styles[toast.variant]}`}>
              <div className={styles.body}>
                <p className={styles.title}>{toast.title}</p>
                {toast.description ? <p className={styles.description}>{toast.description}</p> : null}
              </div>
              <button
                type="button"
                className={styles.close}
                onClick={() => dismiss(toast.id)}
                aria-label={`Dismiss: ${toast.title}`}
              >
                ×
              </button>
            </li>
          ))}
        </ol>
      </div>
    </ToastContext.Provider>
  );
}

export function useToast() {
  const context = useContext(ToastContext);

  if (!context) {
    throw new Error('useToast must be used inside a ToastProvider.');
  }

  return context;
}
