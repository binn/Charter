import { useMemo, type ReactNode } from 'react';
import { ApiContext, resolveApi } from '@/api/api-context';
import type { CharterApi } from '@/api/client';

export function ApiProvider({ api, children }: { api?: CharterApi; children: ReactNode }) {
  const value = useMemo(() => api ?? resolveApi(), [api]);
  return <ApiContext value={value}>{children}</ApiContext>;
}
