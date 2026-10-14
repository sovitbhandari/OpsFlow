import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo, useState, type ReactNode } from "react";
import { api, type CurrentUser } from "./api";
import { AuthContext, type AuthState } from "./useAuth";
const orgKey = "opsflow.organizationId";

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [organizationId, setOrganizationIdState] = useState<string | null>(sessionStorage.getItem(orgKey));
  const me = useQuery({
    queryKey: ["me"],
    queryFn: async () => {
      try {
        return await api<CurrentUser>("/api/auth/me");
      } catch {
        return null;
      }
    },
    retry: false,
  });

  const organization = useMemo(() => {
    const orgs = me.data?.organizations ?? [];
    return orgs.find((item) => item.id === organizationId) ?? orgs[0];
  }, [me.data, organizationId]);

  const value: AuthState = {
    user: me.data ?? undefined,
    loading: me.isLoading,
    organization,
    setOrganizationId: (id: string) => {
      sessionStorage.setItem(orgKey, id);
      setOrganizationIdState(id);
    },
    refresh: async () => {
      await queryClient.invalidateQueries({ queryKey: ["me"] });
    },
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

