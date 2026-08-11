import { lazy, Suspense } from 'react';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router';
import { ApiProvider } from '@/api/ApiProvider';
import { ViewerProvider } from '@/app/ViewerProvider';
import { useViewer } from '@/app/viewer-context';
import { AppShell } from '@/components/AppShell';
import { Skeleton } from '@/components/ui/Skeleton';
import { AcceptInvitationPage } from '@/pages/AcceptInvitationPage';
import { ApprovalsPage } from '@/pages/ApprovalsPage';
import { ForgotPasswordPage } from '@/pages/ForgotPasswordPage';
import { NewRequestPage } from '@/pages/NewRequestPage';
import { NotFoundPage } from '@/pages/NotFoundPage';
import { ProjectsPage } from '@/pages/ProjectsPage';
import { RequestDetailPage } from '@/pages/RequestDetailPage';
import { RequestListPage } from '@/pages/RequestListPage';
import { ResetPasswordPage } from '@/pages/ResetPasswordPage';
import { RunnersPage } from '@/pages/RunnersPage';
import { SettingsPage } from '@/pages/SettingsPage';
import { SetupPage } from '@/pages/SetupPage';
import { SignInPage } from '@/pages/SignInPage';
import { WelcomePage } from '@/pages/WelcomePage';

/**
 * §9's wizard, split out of the entry chunk.
 *
 * Same reasoning as Monaco (§3, §12): a requester never opens repo onboarding, and everything it
 * pulls in — the scope tree, the smoke-test view, the merge-gate assessment — would otherwise be
 * downloaded by every person who only ever files requests. Engineers pay for it on the one click
 * that needs it.
 */
const RepositoriesPage = lazy(async () => ({
  default: (await import('@/pages/RepositoriesPage')).RepositoriesPage,
}));

const RepoOnboardingPage = lazy(async () => ({
  default: (await import('@/pages/RepoOnboardingPage')).RepoOnboardingPage,
}));

/**
 * §30.4: a requester who has not been through onboarding is sent through it once.
 *
 * This is a courtesy, not a gate — `/welcome` is skippable from its first screen, and every route
 * behind it enforces its own access server-side. Routing has never been an authorisation mechanism
 * in this app.
 */
function OnboardingGate() {
  const { viewer } = useViewer();
  const needsOnboarding =
    viewer.capabilities.canFileRequests && viewer.requesterOnboardingCompletedAt === undefined;

  return needsOnboarding ? <Navigate replace to="/welcome" /> : <AppShell />;
}

/**
 * Everything that needs a session, behind the one component that asks for one.
 *
 * `ViewerProvider` calls `GET /api/me` and sends the browser to `/sign-in` on a 401 or to `/setup`
 * on the 503 an unclaimed instance answers with (§30.1). The routes above it — first run, sign-in,
 * the two one-time links — sit outside it because nobody can present a cookie before they have one.
 *
 * **This is not the authorisation boundary.** The server refuses every one of these endpoints
 * without a valid cookie regardless of what the router drew; this only decides which screen a person
 * without one is looking at.
 */
function SignedIn() {
  return (
    <ViewerProvider>
      <OnboardingGate />
    </ViewerProvider>
  );
}

export default function App() {
  return (
    <ApiProvider>
      <BrowserRouter>
        <Routes>
          {/* Reachable without a session. §30.1's setup route, §21's sign-in, and the two paths the
              emails Charter sends already point at. */}
          <Route element={<SetupPage />} path="/setup" />
          <Route element={<SignInPage />} path="/sign-in" />
          <Route element={<ForgotPasswordPage />} path="/forgot-password" />
          <Route element={<AcceptInvitationPage />} path="/accept-invitation" />
          <Route element={<ResetPasswordPage />} path="/reset-password" />

          <Route
            element={
              <ViewerProvider>
                <WelcomePage />
              </ViewerProvider>
            }
            path="/welcome"
          />

          <Route element={<SignedIn />}>
            <Route element={<Navigate replace to="/requests" />} index />
            <Route element={<RequestListPage />} path="requests" />
            <Route element={<NewRequestPage />} path="requests/new" />
            <Route element={<RequestDetailPage />} path="requests/:id" />
            <Route element={<ProjectsPage />} path="projects" />
            <Route element={<ApprovalsPage />} path="approvals" />
            <Route element={<SettingsPage />} path="settings" />
            <Route
              element={
                <Suspense fallback={<Skeleton className="h-64 w-full" />}>
                  <RepositoriesPage />
                </Suspense>
              }
              path="settings/repositories"
            />
            <Route
              element={
                <Suspense fallback={<Skeleton className="h-64 w-full" />}>
                  <RepoOnboardingPage />
                </Suspense>
              }
              path="settings/repositories/:id"
            />
            <Route element={<RunnersPage />} path="settings/runners" />
            <Route element={<NotFoundPage />} path="*" />
          </Route>
        </Routes>
      </BrowserRouter>
    </ApiProvider>
  );
}
