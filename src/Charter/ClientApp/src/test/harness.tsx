import type { ReactElement } from 'react';
import { render } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { ApiProvider } from '@/api/ApiProvider';
import type { CharterApi } from '@/api/client';
import { ViewerProvider } from '@/app/ViewerProvider';
import { makeInstance, makeViewer } from '@/api/mock/fixtures';
import type { MockPersona } from '@/api/mock/fixtures';

/**
 * Test plumbing for components that live below `ApiProvider` and `ViewerProvider`.
 *
 * The point of `createTestApi` is that unimplemented methods **throw with the method's name**
 * rather than returning `undefined`. A component that reaches for an endpoint the test did not
 * anticipate should fail loudly; silently resolving `undefined` turns a wiring mistake into a
 * mysterious render.
 */

const NOW = Date.parse('2026-06-07T12:00:00.000Z');

function unimplemented(name: string): never {
  throw new Error(`Test API: ${name} was called but the test did not provide it`);
}

export function createTestApi(partial: Partial<CharterApi> = {}, persona: MockPersona = 'engineer') {
  const base: CharterApi = {
    getInstance: () => Promise.resolve(makeInstance(NOW)),
    getViewer: () => Promise.resolve(makeViewer(persona, NOW)),
    updatePreferences: (patch) =>
      Promise.resolve({ ...makeViewer(persona, NOW).preferences, ...patch }),
    completeRequesterOnboarding: () => unimplemented('completeRequesterOnboarding'),
    listProjects: () => Promise.resolve([]),
    listRequests: () => Promise.resolve([]),
    getRequest: () => unimplemented('getRequest'),
    createRequest: () => unimplemented('createRequest'),
    sendRefinementMessage: () => unimplemented('sendRefinementMessage'),
    approveSpec: () => unimplemented('approveSpec'),
    requestSpecChanges: () => unimplemented('requestSpecChanges'),
    submitFeedback: () => unimplemented('submitFeedback'),
    cancelRequest: () => unimplemented('cancelRequest'),
    rebuildArtifact: () => unimplemented('rebuildArtifact'),
    listPendingApprovals: () => Promise.resolve([]),
    getTranscript: () => unimplemented('getTranscript'),
    getFileDiff: () => unimplemented('getFileDiff'),
    approveSession: () => Promise.resolve(),
    steerSession: () => Promise.resolve(),
    reviseSession: () => Promise.resolve(),
    takeOverSession: () => Promise.resolve(),
    listRunners: () => unimplemented('listRunners'),
    createPairingToken: () => unimplemented('createPairingToken'),
    revokeAgent: () => Promise.resolve(),
    getSetupChecklist: () => Promise.resolve(null),
    dismissSetupChecklist: () => unimplemented('dismissSetupChecklist'),
    subscribeToRequest: () => () => {},
  };

  return { ...base, ...partial };
}

export function renderWithProviders(ui: ReactElement, api: CharterApi) {
  return render(
    <ApiProvider api={api}>
      <MemoryRouter>
        <ViewerProvider>{ui}</ViewerProvider>
      </MemoryRouter>
    </ApiProvider>,
  );
}
