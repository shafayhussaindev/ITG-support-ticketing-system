import { useEffect, useMemo, useState } from 'react';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '@/contexts/AuthContext';
import { useTheme } from '@/contexts/ThemeContext';
import { useToast } from '@/contexts/ToastContext';
import { ErrorBoundary } from '@/components/ErrorBoundary';
import { ConfirmDialog } from '@/components/ui';
import { NotificationBell } from './NotificationBell';
import { visibleNavigation } from './navigation';
import { useActiveIndicator, useMotion } from '@/motion/hooks';
import { DURATION, EASE, gsap } from '@/motion/motion';
import s from './AppLayout.module.css';

function initials(fullName = '') {
  return fullName
    .split(' ')
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase())
    .join('');
}

export function AppLayout() {
  const { user, can, logout } = useAuth();
  const { theme, toggle } = useTheme();
  const toast = useToast();
  const navigate = useNavigate();
  const location = useLocation();

  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [confirmSignOut, setConfirmSignOut] = useState(false);
  const [signingOut, setSigningOut] = useState(false);

  const groups = useMemo(() => visibleNavigation(can), [can]);

  // The marker tracks the path rather than a NavLink callback, because it has to
  // know which item won across the whole sidebar, not per-link.
  const activePath = useMemo(() => {
    const all = groups.flatMap((group) => group.items).map((item) => item.to);
    const exact = all.find((to) => to === location.pathname + location.search)
      ?? all.find((to) => to === location.pathname);

    // Falls back to the longest matching prefix, so /tickets/<id> still marks
    // "All tickets" rather than leaving the sidebar with nothing selected.
    return exact
      ?? all
        .filter((to) => !to.includes('?') && location.pathname.startsWith(to))
        .sort((a, b) => b.length - a.length)[0];
  }, [groups, location.pathname, location.search]);

  const { listRef, markerRef } = useActiveIndicator(activePath);

  // A short cross-fade on route change. Long enough to signal that the page turned,
  // short enough that nobody navigating quickly ever waits for it.
  const mainRef = useMotion(() => {
    gsap.fromTo(
      '[data-route-content]',
      { opacity: 0, y: 6 },
      { opacity: 1, y: 0, duration: DURATION.base, ease: EASE.out, clearProps: 'transform' },
    );
  }, [location.pathname]);

  // Close the mobile drawer on navigation, otherwise it stays over the new page.
  useEffect(() => {
    setSidebarOpen(false);
  }, [location.pathname]);

  const currentTitle = useMemo(() => {
    for (const group of groups) {
      const match = group.items.find((item) => item.to === location.pathname);
      if (match) {
        return match.label;
      }
    }
    return 'Support Ticketing';
  }, [groups, location.pathname]);

  async function handleSignOut() {
    setSigningOut(true);

    try {
      await logout();
      toast.success('Signed out', 'Your session has been ended.');
      navigate('/login', { replace: true });
    } finally {
      setSigningOut(false);
      setConfirmSignOut(false);
    }
  }

  return (
    <div className={s.shell}>
      <a className="skip-link" href="#main-content">
        Skip to main content
      </a>

      <aside
        className={`${s.sidebar} ${sidebarOpen ? s.sidebarOpen : ''}`}
        aria-label="Main navigation"
      >
        <div className={s.brand}>
          <span className={s.brandMark} aria-hidden="true">
            ST
          </span>
          <span className={s.brandText}>Support Desk</span>
        </div>

        <nav className={s.nav} ref={listRef}>
          {/*
            One marker for the whole sidebar, moved to whichever item is active. Two
            separate highlights fading in and out would read as two things; a single
            element travelling reads as what it is — the same marker relocating.
          */}
          <span className={s.navMarker} ref={markerRef} aria-hidden="true" />

          {groups.map((group) => (
            <div key={group.label} className={s.navGroup}>
              <p className={s.navGroupLabel}>{group.label}</p>

              {group.items.map((item) => (
                <NavLink
                  key={item.to}
                  to={item.to}
                  data-active={item.to === activePath ? 'true' : undefined}
                  className={({ isActive }) =>
                    [s.navLink, isActive ? s.navLinkActive : ''].filter(Boolean).join(' ')
                  }
                >
                  <span className={s.navIcon} aria-hidden="true">
                    {item.icon}
                  </span>
                  <span>{item.label}</span>
                  {!item.available ? <span className={s.navBadge}>Planned</span> : null}
                </NavLink>
              ))}
            </div>
          ))}
        </nav>
      </aside>

      {sidebarOpen ? (
        <button
          type="button"
          className={s.scrim}
          aria-label="Close navigation"
          onClick={() => setSidebarOpen(false)}
        />
      ) : null}

      <header className={s.topbar}>
        <div className={s.topbarLeft}>
          <button
            type="button"
            className={`${s.iconButton} ${s.menuButton}`}
            onClick={() => setSidebarOpen((open) => !open)}
            aria-label="Toggle navigation"
            aria-expanded={sidebarOpen}
          >
            ☰
          </button>
          <h1 className={s.pageTitle}>{currentTitle}</h1>
        </div>

        <div className={s.topbarRight}>
          <NotificationBell />

          <button
            type="button"
            className={s.iconButton}
            onClick={toggle}
            aria-label={`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`}
            title={`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`}
          >
            {theme === 'dark' ? '☀' : '☾'}
          </button>

          <button
            type="button"
            className={s.userChip}
            onClick={() => navigate('/profile')}
            aria-label="Open my profile"
          >
            <span className={s.avatar} aria-hidden="true">
              {initials(user?.fullName)}
            </span>
            <span className={s.userMeta}>
              <span className={s.userName}>{user?.fullName}</span>
              <span className={s.userRole}>{user?.roles?.join(', ')}</span>
            </span>
          </button>

          <button
            type="button"
            className={s.iconButton}
            onClick={() => setConfirmSignOut(true)}
            aria-label="Sign out"
            title="Sign out"
          >
            ⏻
          </button>
        </div>
      </header>

      <main className={s.main} id="main-content" tabIndex={-1} ref={mainRef}>
        <div className={s.content} data-route-content>
          {/* Keyed on the path so a thrown error clears when the user navigates away. */}
          <ErrorBoundary key={location.pathname}>
            <Outlet />
          </ErrorBoundary>
        </div>
      </main>

      <ConfirmDialog
        open={confirmSignOut}
        title="Sign out?"
        message="You will need to sign in again to return to the support desk."
        confirmLabel="Sign out"
        variant="danger"
        loading={signingOut}
        onConfirm={handleSignOut}
        onCancel={() => setConfirmSignOut(false)}
      />
    </div>
  );
}
