import { useLocation, useNavigate } from 'react-router-dom';
import { Badge, Button, Card, CardBody } from '@/components/ui';

/**
 * Shown for a navigation destination whose backend does not exist yet.
 *
 * The alternative — a screen full of placeholder rows and buttons that do nothing —
 * is worse than useless: it makes an unfinished system look finished, and it wastes
 * a tester's time proving that a mock button does not work. This page states what is
 * missing, which phase delivers it, and what the API will look like.
 */
export function NotImplementedPage({ title, phase, description, endpoints = [] }) {
  const location = useLocation();
  const navigate = useNavigate();

  return (
    <Card>
      <CardBody>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--s-4)', maxWidth: '70ch' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--s-3)', flexWrap: 'wrap' }}>
            <h2 style={{ fontSize: 'var(--fs-xl)', fontWeight: 700 }}>{title}</h2>
            <Badge tone="warning">Not built yet</Badge>
            {phase ? <Badge tone="neutral">{phase}</Badge> : null}
          </div>

          <p style={{ fontSize: 'var(--fs-md)', color: 'var(--c-text-2)' }}>{description}</p>

          <p style={{ fontSize: 'var(--fs-sm)', color: 'var(--c-text-3)' }}>
            The route <code style={{ fontFamily: 'var(--font-mono)' }}>{location.pathname}</code> is
            reserved and appears in the navigation so the shape of the finished product is
            visible. Nothing on this page calls an API, because the endpoints below do not
            exist yet.
          </p>

          {endpoints.length > 0 ? (
            <div>
              <p
                style={{
                  fontSize: 'var(--fs-xs)',
                  fontWeight: 700,
                  textTransform: 'uppercase',
                  letterSpacing: '0.6px',
                  color: 'var(--c-text-3)',
                  marginBottom: 'var(--s-2)',
                }}
              >
                Planned endpoints
              </p>
              <ul
                style={{
                  margin: 0,
                  padding: 0,
                  listStyle: 'none',
                  display: 'flex',
                  flexDirection: 'column',
                  gap: 4,
                }}
              >
                {endpoints.map((endpoint) => (
                  <li
                    key={endpoint}
                    style={{
                      fontFamily: 'var(--font-mono)',
                      fontSize: 'var(--fs-xs)',
                      color: 'var(--c-text-2)',
                      background: 'var(--c-surface-3)',
                      padding: '4px 8px',
                      borderRadius: 'var(--r-sm)',
                    }}
                  >
                    {endpoint}
                  </li>
                ))}
              </ul>
            </div>
          ) : null}

          <div>
            <Button variant="secondary" size="sm" onClick={() => navigate('/dashboard')}>
              Back to dashboard
            </Button>
          </div>
        </div>
      </CardBody>
    </Card>
  );
}
