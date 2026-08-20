import { useEffect, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { notificationKeys, notificationService } from '@/services/slaService';
import { useToast } from '@/contexts/ToastContext';
import { formatRelative } from '@/utils/datetime';
import s from './NotificationBell.module.css';

const SEVERITY_CLASS = {
  Info: 'info',
  Success: 'success',
  Warning: 'warning',
  Critical: 'critical',
};

export function NotificationBell() {
  const [open, setOpen] = useState(false);
  const containerRef = useRef(null);
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const toast = useToast();

  // Which popups have already interrupted this session. A ref rather than state
  // because changing it must not re-render — and it deliberately does not persist:
  // a reload is a new session, and an unread breach is worth saying again.
  const shown = useRef(new Set());

  const { data } = useQuery({
    queryKey: notificationKeys.mine,
    queryFn: () => notificationService.list({ take: 15 }),
    // Polled rather than pushed for now. SignalR delivers the nudge once the client
    // subscribes; this keeps the count honest even if the socket drops.
    //
    // Thirty seconds rather than sixty: this is now how a breach reaches the person
    // holding the ticket, and a minute of silence on a missed deadline is a long time.
    refetchInterval: 30_000,
  });

  // Interrupts the person the ticket belongs to. Supervisors receive the same events
  // with ShowAsPopup false and find them in the bell, because their job is the pattern
  // rather than the individual ticket — interrupting them for each one would teach
  // them to dismiss all of them.
  useEffect(() => {
    const items = data?.items ?? [];

    for (const item of items) {
      if (!item.showAsPopup || item.isRead || shown.current.has(item.id)) {
        continue;
      }

      shown.current.add(item.id);

      const raise = item.severity === 'Critical' ? toast.error
        : item.severity === 'Warning' ? toast.warning
          : toast.info;

      raise(item.title, item.body);
    }
  }, [data, toast]);

  const markRead = useMutation({
    mutationFn: (ids) => (ids ? notificationService.markRead(ids) : notificationService.markAllRead()),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: notificationKeys.mine }),
  });

  // Close on an outside click or Escape, the two ways a keyboard or mouse user
  // expects to dismiss a popover.
  useEffect(() => {
    if (!open) {
      return undefined;
    }

    function onPointerDown(event) {
      if (containerRef.current && !containerRef.current.contains(event.target)) {
        setOpen(false);
      }
    }

    function onKeyDown(event) {
      if (event.key === 'Escape') {
        setOpen(false);
      }
    }

    document.addEventListener('mousedown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);

    return () => {
      document.removeEventListener('mousedown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  const unread = data?.unreadCount ?? 0;
  const items = data?.recent ?? [];

  function openNotification(notification) {
    if (!notification.isRead) {
      markRead.mutate([notification.id]);
    }

    setOpen(false);

    if (notification.link) {
      navigate(notification.link);
    }
  }

  return (
    <div className={s.wrap} ref={containerRef}>
      <button
        type="button"
        className={s.bell}
        onClick={() => setOpen((value) => !value)}
        aria-expanded={open}
        aria-haspopup="true"
        aria-label={unread > 0 ? `Notifications, ${unread} unread` : 'Notifications'}
      >
        <span aria-hidden="true">🔔</span>
        {unread > 0 ? (
          <span className={s.badge}>{unread > 99 ? '99+' : unread}</span>
        ) : null}
      </button>

      {open ? (
        <div className={s.panel} role="dialog" aria-label="Notifications">
          <header className={s.panelHead}>
            <span className={s.panelTitle}>Notifications</span>
            {unread > 0 ? (
              <button type="button" className={s.markAll} onClick={() => markRead.mutate(null)}>
                Mark all read
              </button>
            ) : null}
          </header>

          {items.length === 0 ? (
            <p className={s.empty}>Nothing yet. SLA warnings and escalations will appear here.</p>
          ) : (
            <ul className={s.list}>
              {items.map((notification) => (
                <li key={notification.id}>
                  <button
                    type="button"
                    className={`${s.item} ${notification.isRead ? '' : s.unread}`}
                    onClick={() => openNotification(notification)}
                  >
                    <span
                      className={`${s.dot} ${s[SEVERITY_CLASS[notification.severity] ?? 'info']}`}
                      aria-hidden="true"
                    />
                    <span className={s.itemBody}>
                      <span className={s.itemTitle}>{notification.title}</span>
                      <span className={s.itemText}>{notification.body}</span>
                      <span className={s.itemTime}>{formatRelative(notification.createdAtUtc)}</span>
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      ) : null}
    </div>
  );
}
