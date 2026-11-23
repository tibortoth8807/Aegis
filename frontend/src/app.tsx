import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { httpApi } from './api/http';
import { AegisApi, ApiError, Counters, Me, Persona } from './api/types';
import { demoApi, DemoApi } from './demo/api';

// ------------------------------------------------------------------ routing (hash based: works on GitHub Pages)

export function useRoute(): [string, (to: string) => void, string] {
  const read = () => location.hash.replace(/^#/, '') || '/';
  // the full route including the query, so "?status=…" changes re-render the page too
  const [route, setRoute] = useState(read);
  useEffect(() => {
    const on = () => {
      setRoute(read());
      window.scrollTo(0, 0);
    };
    window.addEventListener('hashchange', on);
    return () => window.removeEventListener('hashchange', on);
  }, []);
  const navigate = useCallback((to: string) => {
    location.hash = to;
  }, []);
  return [route.split('?')[0], navigate, route];
}

/** Matches "/requests/:id" against "/requests/12" → { id: "12" }. */
export function match(pattern: string, path: string): Record<string, string> | null {
  const p = pattern.split('/');
  const a = path.split('/');
  if (p.length !== a.length) return null;
  const params: Record<string, string> = {};
  for (let i = 0; i < p.length; i++) {
    if (p[i].startsWith(':')) params[p[i].slice(1)] = decodeURIComponent(a[i]);
    else if (p[i] !== a[i]) return null;
  }
  return params;
}

// ------------------------------------------------------------------ app context

export interface Toast { id: number; text: string; kind: 'ok' | 'error' }

export interface AppState {
  api: AegisApi;
  demo: DemoApi | null;
  user: string | null;
  setUser(number: string): void;
  personas: Persona[];
  me: Me | null;
  counters: Counters | null;
  /** Call after a change: refreshes the counters and every page that listens. */
  changed(): void;
  version: number;
  navigate(to: string): void;
  toast(text: string, kind?: Toast['kind']): void;
  fail(e: unknown): void;
  has(role: string): boolean;
}

export const AppContext = createContext<AppState>(null as unknown as AppState);
export const useApp = () => useContext(AppContext);

const USER_KEY = 'aegis-user';
const storedUser = () => {
  try { return localStorage.getItem(USER_KEY); } catch { return null; }
};

export function useAppState(navigate: (to: string) => void): AppState & { toasts: Toast[] } {
  const [user, setUserState] = useState<string | null>(storedUser);
  const [personas, setPersonas] = useState<Persona[]>([]);
  const [me, setMe] = useState<Me | null>(null);
  const [counters, setCounters] = useState<Counters | null>(null);
  const [version, setVersion] = useState(0);
  const [toasts, setToasts] = useState<Toast[]>([]);

  const userRef = useRef(user);
  userRef.current = user;
  const { api, demo } = useMemo(() => {
    const url = import.meta.env?.VITE_API_URL;
    if (url) return { api: httpApi(url, () => userRef.current), demo: null };
    const d = demoApi(() => userRef.current);
    return { api: d as AegisApi, demo: d };
  }, []);

  const toast = useCallback((text: string, kind: Toast['kind'] = 'ok') => {
    const id = Date.now() + Math.random();
    setToasts((t) => [...t, { id, text, kind }]);
    setTimeout(() => setToasts((t) => t.filter((x) => x.id !== id)), kind === 'error' ? 7000 : 3500);
  }, []);
  const fail = useCallback((e: unknown) => {
    const err = e as ApiError;
    const fields = err.errors ? Object.values(err.errors).flat() : [];
    toast(fields.length ? fields.join(' ') : err.message || 'Something went wrong.', 'error');
  }, [toast]);

  useEffect(() => {
    api.personas().then(setPersonas).catch(fail);
  }, [api, fail]);

  useEffect(() => {
    if (!user && personas.length) setUser(personas[0].number);
  }, [personas, user]);

  useEffect(() => {
    if (!user) return;
    let live = true;
    Promise.all([api.me(), api.counters()]).then(([m, c]) => {
      if (!live) return;
      setMe(m);
      setCounters(c);
    }).catch((e) => {
      if ((e as ApiError).status === 401) setUserState(null);
      else fail(e);
    });
    return () => { live = false; };
  }, [api, user, version, fail]);

  const setUser = (number: string) => {
    try { localStorage.setItem(USER_KEY, number); } catch { /* storage blocked */ }
    setMe(null);
    setUserState(number);
    setVersion((v) => v + 1);
  };

  return {
    api, demo, user, setUser, personas, me, counters, version, navigate, toast, fail, toasts,
    changed: () => setVersion((v) => v + 1),
    has: (role: string) => !!me && (me.roles.includes(role) || me.roles.includes('Admin')),
  };
}

/** Loads data for a page; reloads when the dependencies or the app version change. */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[]): { data: T | null; error: ApiError | null; loading: boolean; reload(): void } {
  const { version, user } = useApp();
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [loading, setLoading] = useState(true);
  const [tick, setTick] = useState(0);
  useEffect(() => {
    let live = true;
    setLoading(true);
    load().then((d) => {
      if (!live) return;
      setData(d);
      setError(null);
    }).catch((e) => live && setError(e as ApiError)).finally(() => live && setLoading(false));
    return () => { live = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, version, user, tick]);
  return { data, error, loading, reload: () => setTick((t) => t + 1) };
}

/** A query parameter of the current hash route ("#/employees?status=Preboarding"). */
export const queryParam = (key: string) => new URLSearchParams(location.hash.split('?')[1] ?? '').get(key);
