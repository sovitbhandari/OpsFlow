import { createContext, useContext } from "react";
import type { CurrentUser, Organization } from "./api";

export interface AuthState {
  user?: CurrentUser;
  loading: boolean;
  organization?: Organization;
  setOrganizationId: (id: string) => void;
  refresh: () => Promise<void>;
}

export const AuthContext = createContext<AuthState | null>(null);

export function useAuth() {
  const value = useContext(AuthContext);
  if (!value) throw new Error("AuthProvider is missing.");
  return value;
}
