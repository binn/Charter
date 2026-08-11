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
    getSetupStatus: () => Promise.resolve({ setupRequired: false }),
    completeSetup: () => unimplemented('completeSetup'),
    getAuthProviders: () =>
      Promise.resolve({ providers: [{ name: 'password', style: 'credential' }], selfServicePasswordReset: true }),
    signIn: () => unimplemented('signIn'),
    signOut: () => Promise.resolve(),
    forgotPassword: () => unimplemented('forgotPassword'),
    resetPassword: () => unimplemented('resetPassword'),
    acceptInvitation: () => unimplemented('acceptInvitation'),
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
    listRepos: () => Promise.resolve([]),
    connectRepo: () => unimplemented('connectRepo'),
    getRepoOnboarding: () => unimplemented('getRepoOnboarding'),
    startRecon: () => unimplemented('startRecon'),
    confirmScope: () => unimplemented('confirmScope'),
    getSmokeTest: () => Promise.resolve(null),
    publishPrimer: () => unimplemented('publishPrimer'),
    getRepoAccess: () => unimplemented('getRepoAccess'),
    setRepoAccess: () => unimplemented('setRepoAccess'),
    listMembers: () => unimplemented('listMembers'),
    setMemberRole: () => unimplemented('setMemberRole'),
    getAuditLog: () => unimplemented('getAuditLog'),
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

/**
 * The same tree minus `ViewerProvider`, for the pages you reach *without* a session: first run,
 * sign-in, and the two one-time links. Those pages must not depend on a viewer — nobody has one yet
 * — so rendering them under one would hide exactly the mistake worth catching.
 *
 * `route` carries the query string, because `?token=…` is the entire input to two of these pages.
 */
export function renderPublicPage(ui: ReactElement, api: CharterApi, route = '/') {
  return render(
    <ApiProvider api={api}>
      <MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter>
    </ApiProvider>,
  );
}
