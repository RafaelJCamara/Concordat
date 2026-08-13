import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ActiveEnvironmentStore } from './active-environment-store';
import { provideConcordatConfig } from './app-config';

// Small store, large blast radius: every registry read is scoped to whatever this says, so
// the two things worth pinning are that it starts where the deployment said and that a
// switch is visible to everything at once.

function setUp(defaultEnvironment = 'dev') {
  TestBed.configureTestingModule({ providers: [provideConcordatConfig({ defaultEnvironment })] });
  return TestBed.inject(ActiveEnvironmentStore);
}

describe('ActiveEnvironmentStore', () => {
  it('starts on the environment the deployment configured', () => {
    // Not a hardcoded `dev`. The same bundle is served by every deployment, so the cold
    // start has to come from configuration or a Cloud tenant opens on somebody else's idea
    // of a default.
    expect(setUp('staging').name()).toBe('staging');
  });

  it('switches environment', () => {
    const environments = setUp();

    environments.select('prod');

    expect(environments.name()).toBe('prod');
  });

  it('is a single instance, so a switch cannot be half-applied', () => {
    // Root-provided on purpose. If each feature kept its own copy, switching would update
    // whichever screen is open and leave the rest showing another environment's data under
    // this environment's name — a wrong answer presented confidently.
    const first = setUp();
    first.select('prod');

    expect(TestBed.inject(ActiveEnvironmentStore).name()).toBe('prod');
  });
});
