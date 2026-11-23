import { AegisApi, ApiError, DownloadedFile, EmployeeFilter, PageQuery, RequestFilter } from './types';

/** The real backend: the .NET API, signed in with the demo header. */
export function httpApi(baseUrl: string, user: () => string | null): AegisApi {
  const base = baseUrl.replace(/\/$/, '');

  async function send(method: string, path: string, body?: unknown, raw = false): Promise<Response> {
    const headers: Record<string, string> = { Accept: 'application/json' };
    const who = user();
    if (who) headers['X-Aegis-User'] = who;
    let payload: BodyInit | undefined;
    if (body instanceof FormData) payload = body;
    else if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      payload = JSON.stringify(body);
    }
    const r = await fetch(base + path, { method, headers, body: payload });
    if (!r.ok) {
      let p: { title?: string; detail?: string; code?: string; errors?: Record<string, string[]> } = {};
      try { p = await r.json(); } catch { /* not a problem response */ }
      throw new ApiError(r.status, p.code ?? String(r.status), p.title ?? r.statusText, p.detail ?? '', p.errors ?? {});
    }
    if (raw) return r;
    return r;
  }
  const json = async <T>(method: string, path: string, body?: unknown): Promise<T> => {
    const r = await send(method, path, body);
    return r.status === 204 ? (undefined as T) : ((await r.json()) as T);
  };
  const qs = (o: Record<string, unknown>) => {
    const p = new URLSearchParams();
    for (const [k, v] of Object.entries(o)) if (v !== undefined && v !== null && v !== '') p.set(k, String(v));
    const s = p.toString();
    return s ? `?${s}` : '';
  };

  return {
    mode: 'http',
    personas: () => json('GET', '/api/demo/personas'),
    me: () => json('GET', '/api/me'),
    counters: () => json('GET', '/api/me/counters'),
    employees: (q: PageQuery, f: EmployeeFilter) => json('GET', `/api/employees${qs({ ...q, ...f })}`),
    employee: (id) => json('GET', `/api/employees/${id}`),
    createEmployee: (input) => json('POST', '/api/employees', input),
    updateEmployee: (id, input) => json('PUT', `/api/employees/${id}`, input),
    employeeTransition: (id, action, endDate, comment) => json('POST', `/api/employees/${id}/transitions`, { action, endDate, comment }),
    lookups: () => json('GET', '/api/lookups'),
    saveCountry: (code, name, eea) => json('PUT', `/api/countries/${code}`, { name, eea }),
    deleteCountry: (code) => json('DELETE', `/api/countries/${code}`),
    saveTeam: (id, name, departmentId, leadId) =>
      id === null ? json('POST', '/api/teams', { name, departmentId, leadId }) : json('PUT', `/api/teams/${id}`, { name, departmentId, leadId }),
    applications: (search) => json('GET', `/api/applications${qs({ search })}`),
    requests: (q: PageQuery, f: RequestFilter) => json('GET', `/api/requests${qs({ ...q, ...f })}`),
    request: (id) => json('GET', `/api/requests/${id}`),
    createRequest: (input) => json('POST', '/api/requests', input),
    requestTransition: (id, action, comment) => json('POST', `/api/requests/${id}/transitions`, { action, comment }),
    courses: () => json('GET', '/api/courses'),
    assignments: (q, scope, status, courseId) => json('GET', `/api/assignments${qs({ ...q, scope, status, courseId })}`),
    assign: async (courseId, employeeIds) => (await json<{ assigned: number }>('POST', '/api/assignments', { courseId, employeeIds })).assigned,
    assignmentTransition: (id, action, score, comment) => json('POST', `/api/assignments/${id}/transitions`, { action, score, comment }),
    tickets: (q, scope, status) => json('GET', `/api/tickets${qs({ ...q, scope, status })}`),
    ticket: (id) => json('GET', `/api/tickets/${id}`),
    createTicket: (input) => json('POST', '/api/tickets', input),
    ticketTransition: (id, action, comment) => json('POST', `/api/tickets/${id}/transitions`, { action, comment }),
    news: (take = 10) => json('GET', `/api/news${qs({ take })}`),
    publishNews: (title, body, pinned) => json('POST', '/api/news', { title, body, pinned }),
    deleteNews: (id) => json('DELETE', `/api/news/${id}`),
    exports: () => json('GET', '/api/exports'),
    download: async (key, format): Promise<DownloadedFile> => {
      const r = await send('GET', `/api/exports/${key}${qs({ format })}`, undefined, true);
      const disposition = r.headers.get('Content-Disposition') ?? '';
      const fileName = /filename\*?=(?:UTF-8'')?"?([^";]+)/i.exec(disposition)?.[1] ?? `aegis-${key}.${format}`;
      return { fileName: decodeURIComponent(fileName), contentType: r.headers.get('Content-Type') ?? '', bytes: new Uint8Array(await r.arrayBuffer()) };
    },
    importFile: (fileName, content, dryRun) => {
      const form = new FormData();
      form.append('file', new Blob([content], { type: 'text/plain' }), fileName);
      return json('POST', `/api/import/file${qs({ dryRun })}`, form);
    },
    runJobs: () => json('POST', '/api/jobs/daily', {}),
    ask: (message, history) => json('POST', '/api/assistant', { message, history: history.map((t) => ({ role: t.role, text: t.text })) }),
  };
}
