import { useQuery, useQueryClient } from '@tanstack/react-query';
import { sessionGateway } from '../../integrations/auth/session-gateway';
import { createAppClient, setAdmissionIdentityScope } from '../../integrations/http/api-client';

export function useSession() {
  const queryClient = useQueryClient();
  const session = useQuery({
    queryKey: ['app', 'session'],
    queryFn: sessionGateway.getSession,
    staleTime: 5 * 60 * 1000,
    retry: false
  });

  const logout = async () => {
    // The app and the account/OAuth broker share the same account cookie.
    queryClient.clear();
    setAdmissionIdentityScope(undefined);
    try {
      await createAppClient().request('/auth/logout', { method: 'POST' });
    } finally {
      window.location.assign('/');
    }
  };

  const user = session.data?.data?.user;
  const isAuthenticated = session.data?.data?.authenticated ?? false;

  const canManagePermissionGroups = user?.permissions?.includes('tool.permission_groups.manage') ?? false;

  const can = (permission: string) => {
    if (!user) return false;
    return user.permissions?.includes(permission) ||
      ((permission === 'settings.view' || permission === 'admin.view') && canManagePermissionGroups);
  };

  const isAdmin = canManagePermissionGroups;

  return {
    session,
    user,
    isAuthenticated,
    reportTemplatesEnabled: isAuthenticated && (session.data?.data?.reportTemplatesEnabled ?? false),
    can,
    isAdmin,
    logout
  };
}
