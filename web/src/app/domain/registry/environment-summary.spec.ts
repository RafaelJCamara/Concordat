import { describe, expect, it } from 'vitest';
import {
  bindingCount,
  contractCount,
  type EnforcementTier,
  type EnvironmentSummary,
} from './environment-summary';

function summary(enforcement: readonly EnforcementTier[]): EnvironmentSummary {
  return {
    subjects: { total: 0, active: 0, deprecated: 0, retired: 0 },
    versions: { total: 0, active: 0, awaitingApproval: 0, rejected: 0, dismissed: 0 },
    newestSubjects: [],
    breakingChanges: [],
    enforcement,
    violations: { distinct: 0, recent: 0, windowHours: 24 },
  };
}

const TIERS: readonly EnforcementTier[] = [
  { mode: 'OFF', contracts: 1, publishBindings: 2, consumeBindings: 0 },
  { mode: 'MONITOR', contracts: 2, publishBindings: 1, consumeBindings: 3 },
  { mode: 'ENFORCE', contracts: 4, publishBindings: 5, consumeBindings: 1 },
];

describe('contractCount', () => {
  it('adds the contracts of every mode, the ones that govern nothing included', () => {
    // A contract set to OFF is still a contract: the "no contracts" empty state must not claim
    // an environment is bare when it holds some that are switched off.
    expect(contractCount(summary(TIERS))).toBe(7);
  });

  it('is zero when nothing is set to any mode', () => {
    expect(contractCount(summary(TIERS.map((t) => ({ ...t, contracts: 0 }))))).toBe(0);
  });
});

describe('bindingCount', () => {
  it('adds publish and consume bindings across every mode', () => {
    expect(bindingCount(summary(TIERS))).toBe(12);
  });
});
