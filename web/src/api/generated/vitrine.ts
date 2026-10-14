/**
 * biome-ignore-all lint: código gerado pelo orval a partir de src/api/openapi.json
 * Vitrine.Api | v1 1.0.0
 */
import { http } from "../http.ts";
export interface LoginRequest {
  email: string;
  password: string;
}

export interface MeWorkspace {
  id: string;
  name: string;
  role: string;
}

export interface MeResponse {
  userId: string;
  name: string;
  email: string;
  workspace: MeWorkspace;
}

export interface ProblemDetails {
  /** @nullable */
  type?: string | null;
  /** @nullable */
  title?: string | null;
  /**
   * @nullable
   * @pattern ^-?(?:0|[1-9]\d*)$
   */
  status?: number | string | null;
  /** @nullable */
  detail?: string | null;
  /** @nullable */
  instance?: string | null;
}

export interface RegisterRequest {
  name: string;
  email: string;
  password: string;
}

export interface RegisterResponse {
  userId: string;
  workspaceId: string;
}

export type getMeResponse200 = {
  data: MeResponse;
  status: 200;
};

export type getMeResponse401 = {
  data: ProblemDetails;
  status: 401;
};

export type getMeResponseSuccess = getMeResponse200 & {
  headers: Headers;
};
export type getMeResponseError = getMeResponse401 & {
  headers: Headers;
};

export type getMeResponse = getMeResponseSuccess | getMeResponseError;

export const getGetMeUrl = () => {
  return `/api/v1/me`;
};

export const getMe = async (options?: Parameters<typeof http>[1]): Promise<getMeResponse> => {
  return http<getMeResponse>(getGetMeUrl(), {
    ...options,
    method: "GET",
  });
};

export type getAntiforgeryTokenResponse204 = {
  data: void;
  status: 204;
};

export type getAntiforgeryTokenResponseSuccess = getAntiforgeryTokenResponse204 & {
  headers: Headers;
};

export type getAntiforgeryTokenResponse = getAntiforgeryTokenResponseSuccess;

export const getGetAntiforgeryTokenUrl = () => {
  return `/api/v1/auth/antiforgery`;
};

export const getAntiforgeryToken = async (
  options?: Parameters<typeof http>[1],
): Promise<getAntiforgeryTokenResponse> => {
  return http<getAntiforgeryTokenResponse>(getGetAntiforgeryTokenUrl(), {
    ...options,
    method: "GET",
  });
};

export type registerResponse201 = {
  data: RegisterResponse;
  status: 201;
};

export type registerResponse400 = {
  data: ProblemDetails;
  status: 400;
};

export type registerResponse404 = {
  data: ProblemDetails;
  status: 404;
};

export type registerResponse409 = {
  data: ProblemDetails;
  status: 409;
};

export type registerResponseSuccess = registerResponse201 & {
  headers: Headers;
};
export type registerResponseError = (
  | registerResponse400
  | registerResponse404
  | registerResponse409
) & {
  headers: Headers;
};

export type registerResponse = registerResponseSuccess | registerResponseError;

export const getRegisterUrl = () => {
  return `/api/v1/auth/register`;
};

export const register = async (
  registerRequest: RegisterRequest,
  options?: Parameters<typeof http>[1],
): Promise<registerResponse> => {
  const getHeaders = (
    h?: NonNullable<RequestInit["headers"]>,
  ): Record<string, string | readonly string[]> => {
    if (!h) return {};
    if (h instanceof Headers) return Object.fromEntries(h.entries());
    if (Symbol.iterator in h) {
      return Object.fromEntries(
        Array.from(
          h as Iterable<Iterable<string>>,
          (entry) => Array.from(entry) as [string, string],
        ),
      );
    }
    const headers: Record<string, string | readonly string[]> = {};
    for (const [name, value] of Object.entries<string | readonly string[] | undefined>(h)) {
      if (value !== undefined) headers[name] = value;
    }
    return headers;
  };
  return http<registerResponse>(getRegisterUrl(), {
    ...options,
    method: "POST",
    headers: { "Content-Type": "application/json", ...getHeaders(options?.headers) },
    body: JSON.stringify(registerRequest),
  });
};

export type loginResponse204 = {
  data: void;
  status: 204;
};

export type loginResponse400 = {
  data: ProblemDetails;
  status: 400;
};

export type loginResponse401 = {
  data: ProblemDetails;
  status: 401;
};

export type loginResponse423 = {
  data: ProblemDetails;
  status: 423;
};

export type loginResponseSuccess = loginResponse204 & {
  headers: Headers;
};
export type loginResponseError = (loginResponse400 | loginResponse401 | loginResponse423) & {
  headers: Headers;
};

export type loginResponse = loginResponseSuccess | loginResponseError;

export const getLoginUrl = () => {
  return `/api/v1/auth/login`;
};

export const login = async (
  loginRequest: LoginRequest,
  options?: Parameters<typeof http>[1],
): Promise<loginResponse> => {
  const getHeaders = (
    h?: NonNullable<RequestInit["headers"]>,
  ): Record<string, string | readonly string[]> => {
    if (!h) return {};
    if (h instanceof Headers) return Object.fromEntries(h.entries());
    if (Symbol.iterator in h) {
      return Object.fromEntries(
        Array.from(
          h as Iterable<Iterable<string>>,
          (entry) => Array.from(entry) as [string, string],
        ),
      );
    }
    const headers: Record<string, string | readonly string[]> = {};
    for (const [name, value] of Object.entries<string | readonly string[] | undefined>(h)) {
      if (value !== undefined) headers[name] = value;
    }
    return headers;
  };
  return http<loginResponse>(getLoginUrl(), {
    ...options,
    method: "POST",
    headers: { "Content-Type": "application/json", ...getHeaders(options?.headers) },
    body: JSON.stringify(loginRequest),
  });
};

export type logoutResponse204 = {
  data: void;
  status: 204;
};

export type logoutResponse400 = {
  data: ProblemDetails;
  status: 400;
};

export type logoutResponseSuccess = logoutResponse204 & {
  headers: Headers;
};
export type logoutResponseError = logoutResponse400 & {
  headers: Headers;
};

export type logoutResponse = logoutResponseSuccess | logoutResponseError;

export const getLogoutUrl = () => {
  return `/api/v1/auth/logout`;
};

export const logout = async (options?: Parameters<typeof http>[1]): Promise<logoutResponse> => {
  return http<logoutResponse>(getLogoutUrl(), {
    ...options,
    method: "POST",
  });
};
