import { BrowserRouter, Navigate, Route, Routes } from 'react-router';
import { ApiProvider } from '@/api/ApiProvider';
import { ViewerProvider } from '@/app/ViewerProvider';
import { useViewer } from '@/app/viewer-context';
import { AppShell } from '@/components/AppShell';
import { ApprovalsPage } from '@/pages/ApprovalsPage';
import { NewRequestPage } from '@/pages/NewRequestPage';
import { NotFoundPage } from '@/pages/NotFoundPage';
import { ProjectsPage } from '@/pages/ProjectsPage';
import { RequestDetailPage } from '@/pages/RequestDetailPage';
import { RequestListPage } from '@/pages/RequestListPage';
import { SettingsPage } from '@/pages/SettingsPage';
import { WelcomePage } from '@/pages/WelcomePage';

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

export default function App() {
  return (
    <ApiProvider>
      <BrowserRouter>
        <ViewerProvider>
          <Routes>
            <Route element={<WelcomePage />} path="/welcome" />

            <Route element={<OnboardingGate />}>
              <Route element={<Navigate replace to="/requests" />} index />
              <Route element={<RequestListPage />} path="requests" />
              <Route element={<NewRequestPage />} path="requests/new" />
              <Route element={<RequestDetailPage />} path="requests/:id" />
              <Route element={<ProjectsPage />} path="projects" />
              <Route element={<ApprovalsPage />} path="approvals" />
              <Route element={<SettingsPage />} path="settings" />
              <Route element={<NotFoundPage />} path="*" />
            </Route>
          </Routes>
        </ViewerProvider>
      </BrowserRouter>
    </ApiProvider>
  );
}
