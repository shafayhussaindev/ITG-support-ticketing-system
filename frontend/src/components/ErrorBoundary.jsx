import { Component } from 'react';
import { Button, Card, CardBody } from './ui';

/**
 * Catches render-time exceptions so one broken panel does not blank the whole
 * application. Placed around the routed area and around individually risky
 * widgets such as charts.
 */
export class ErrorBoundary extends Component {
  constructor(props) {
    super(props);
    this.state = { error: null };
  }

  static getDerivedStateFromError(error) {
    return { error };
  }

  componentDidCatch(error, info) {
    // In a deployed environment this is where the error would be forwarded to the
    // monitoring backend along with the correlation id.
    console.error('Unhandled UI error:', error, info?.componentStack);
  }

  reset = () => this.setState({ error: null });

  render() {
    const { error } = this.state;
    const { children, fallbackTitle = 'This section could not be displayed' } = this.props;

    if (!error) {
      return children;
    }

    return (
      <Card>
        <CardBody>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--s-3)' }}>
            <h2 style={{ fontSize: 'var(--fs-lg)', fontWeight: 600 }}>{fallbackTitle}</h2>
            <p style={{ fontSize: 'var(--fs-sm)', color: 'var(--c-text-2)' }}>
              The page hit an unexpected problem. Reloading usually clears it. If it keeps
              happening, send this message to your administrator.
            </p>
            <pre
              style={{
                margin: 0,
                padding: 'var(--s-3)',
                background: 'var(--c-surface-3)',
                borderRadius: 'var(--r-md)',
                fontSize: 'var(--fs-xs)',
                fontFamily: 'var(--font-mono)',
                whiteSpace: 'pre-wrap',
                overflowWrap: 'anywhere',
                color: 'var(--c-text-2)',
              }}
            >
              {String(error?.message ?? error)}
            </pre>
            <div style={{ display: 'flex', gap: 'var(--s-2)' }}>
              <Button size="sm" onClick={this.reset}>
                Try again
              </Button>
              <Button size="sm" variant="secondary" onClick={() => globalThis.location.reload()}>
                Reload page
              </Button>
            </div>
          </div>
        </CardBody>
      </Card>
    );
  }
}
