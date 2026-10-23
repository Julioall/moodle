import { ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';

import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { MoodleLogo } from '@/components/ui/moodle-logo';
import { sessionGateway } from '../../integrations/auth/session-gateway';
import { AppHttpError } from '../../integrations/http/api-client';
import { AuthPage } from './AuthPage';

export function AuthGate({ children }: { children: ReactNode }) {
  const session = useQuery({ queryKey: ['app', 'session'], queryFn: sessionGateway.getSession, retry: false });

  if (session.isLoading) {
    return <div className="min-h-screen flex items-center justify-center bg-background p-4"><MoodleLogo className="w-64 animate-pulse" /></div>;
  }

  const sessionStatus = session.error instanceof AppHttpError
    ? session.error.status
    : typeof session.error === 'object' && session.error !== null && 'status' in session.error
      ? Number((session.error as { status?: unknown }).status)
      : undefined;

  if (session.error && sessionStatus !== 401) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-background p-4">
        <Card className="w-full max-w-md">
          <CardContent className="space-y-4 p-6">
            <div className="flex flex-col items-center gap-2">
              <MoodleLogo className="h-auto w-64 max-w-full" />
            </div>
            <h1 className="text-xl font-semibold">Sessão indisponível</h1>
            <p className="text-sm text-muted-foreground">Não foi possível validar sua sessão.</p>
            <Button variant="outline" onClick={() => void session.refetch()}>Tentar novamente</Button>
          </CardContent>
        </Card>
      </div>
    );
  }

  if (!session.data?.data.authenticated) return <AuthPage />;
  return <>{children}</>;
}
