import { describe, expect, it } from 'vitest';
import { buildPositions, currentPosition } from './RolesPage';

/**
 * The role form asks "where does this sit?" and turns the answer into a rank.
 *
 * Rank is an ordering hint with no bearing on authorization, but it still has to
 * produce a sane ladder: an administrator who says "between Manager and Team Lead"
 * must get a role that appears between Manager and Team Lead, and must be able to keep
 * saying it without the gap running out.
 */

/** The seeded ladder, highest authority first, as the API returns it. */
const LADDER = [
  { id: 'g', name: 'Super Admin', rank: 70 },
  { id: 'f', name: 'Administrator', rank: 60 },
  { id: 'e', name: 'Manager', rank: 50 },
  { id: 'd', name: 'Team Lead', rank: 40 },
  { id: 'c', name: 'Technical Specialist', rank: 30 },
  { id: 'b', name: 'Support Agent', rank: 20 },
  { id: 'a', name: 'Requester', rank: 10 },
];

describe('buildPositions', () => {
  it('offers one more place than there are roles', () => {
    // Seven roles make eight gaps: above the top, between each pair, below the bottom.
    expect(buildPositions(LADDER, null)).toHaveLength(LADDER.length + 1);
  });

  it('names the neighbours rather than showing a number', () => {
    const labels = buildPositions(LADDER, null).map((p) => p.label);

    expect(labels[0]).toBe('Above Super Admin — the most authority');
    expect(labels[1]).toBe('Between Super Admin and Administrator');
    expect(labels.at(-1)).toBe('Below Requester — the least authority');

    // The point of the change: no option leaks the stored integer.
    labels.forEach((label) => expect(label).not.toMatch(/\d/));
  });

  it('places a role strictly between the two it was put between', () => {
    const between = buildPositions(LADDER, null)
      .find((p) => p.label === 'Between Manager and Team Lead');

    expect(between.rank).toBeGreaterThan(40);
    expect(between.rank).toBeLessThan(50);
  });

  it('never produces a negative rank at the bottom', () => {
    // Repeatedly pushing to the bottom halves the lowest rank rather than subtracting,
    // so it approaches zero without crossing it.
    let lowest = { id: 'a', name: 'Requester', rank: 1 };

    for (let i = 0; i < 10; i += 1) {
      const bottom = buildPositions([lowest], null).at(-1);
      expect(bottom.rank).toBeGreaterThanOrEqual(0);
      lowest = { ...lowest, rank: bottom.rank };
    }
  });

  it('excludes the role being moved from its own neighbours', () => {
    const labels = buildPositions(LADDER, LADDER[2]).map((p) => p.label);

    expect(labels.join(' ')).not.toContain('Manager');
    expect(labels).toContain('Between Administrator and Team Lead');
  });

  it('copes with an empty ladder', () => {
    expect(buildPositions([], null)).toEqual([{ label: 'The only role', rank: 10 }]);
  });
});

describe('currentPosition', () => {
  it('opens a new role at the bottom', () => {
    const positions = buildPositions(LADDER, null);
    expect(currentPosition(positions, null)).toBe(positions.length - 1);
  });

  it('opens an existing role where it already sits', () => {
    const manager = LADDER[2];
    const positions = buildPositions(LADDER, manager);

    // Manager is rank 50, so between Administrator (60) and Team Lead (40).
    expect(positions[currentPosition(positions, manager)].label)
      .toBe('Between Administrator and Team Lead');
  });

  it('opens the highest role at the top', () => {
    const superAdmin = LADDER[0];
    const positions = buildPositions(LADDER, superAdmin);

    expect(positions[currentPosition(positions, superAdmin)].label)
      .toBe('Above Administrator — the most authority');
  });
});
