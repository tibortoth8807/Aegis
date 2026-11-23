// The API's data contracts (mirror the C# records in Aegis.Application).

export interface Page<T> {
  items: T[];
  total: number;
  pageNumber: number;
  pageSize: number;
}

export interface PageQuery {
  page?: number;
  pageSize?: number;
  search?: string;
  sort?: string;
  desc?: boolean;
}

export interface PersonRef {
  id: number;
  name: string;
  employeeNumber: string;
}

export interface HistoryEntry {
  at: string;
  actor: string;
  action: string;
  from: string;
  to: string;
  comment: string | null;
}

export interface Me {
  id: number;
  name: string;
  employeeNumber: string;
  jobTitle: string;
  roles: string[];
}

export interface Counters {
  myOpenRequests: number;
  awaitingMyApproval: number;
  myOpenTickets: number;
  ticketQueue: number;
  myTrainingsDue: number;
  teamTrainingsOverdue: number;
  activeEmployees: number;
  joiners: number;
  leavers: number;
  externals: number;
  activeGrants: number;
  privilegedExpiringSoon: number;
}

export interface Persona {
  role: string;
  number: string;
}

export interface EmployeeListItem {
  id: number;
  employeeNumber: string;
  fullName: string;
  email: string;
  jobTitle: string;
  type: string;
  status: string;
  team: string | null;
  location: string | null;
  manager: string | null;
  nationality: string | null;
  startDate: string;
  endDate: string | null;
}

export interface EmployeeFilter {
  status?: string;
  type?: string;
  teamId?: number;
  locationId?: number;
  managerId?: number;
  nationality?: string;
}

export interface Grant {
  id: number;
  system: string;
  role: string;
  privileged: boolean;
  grantedAt: string;
  validUntil: string;
  requestId: number;
}

export interface AssignmentBrief {
  id: number;
  course: string;
  status: string;
  dueOn: string;
  validUntil: string | null;
}

export interface EmployeeDetail {
  id: number;
  employeeNumber: string;
  firstName: string;
  lastName: string;
  email: string;
  jobTitle: string;
  type: string;
  externalCompany: string | null;
  status: string;
  nationalityCode: string | null;
  locationId: number | null;
  location: string | null;
  teamId: number | null;
  team: string | null;
  manager: PersonRef | null;
  startDate: string;
  endDate: string | null;
  portalRoles: string[];
  source: string;
  version: number;
  directReports: PersonRef[];
  access: Grant[];
  trainings: AssignmentBrief[];
  history: HistoryEntry[];
  allowedActions: string[];
}

export interface EmployeeInput {
  employeeNumber: string;
  firstName: string;
  lastName: string;
  email: string;
  jobTitle: string;
  type: string;
  externalCompany: string | null;
  nationalityCode: string | null;
  locationId: number | null;
  teamId: number | null;
  managerId: number | null;
  startDate: string;
  endDate: string | null;
  portalRoles?: string[] | null;
  version?: number | null;
}

export interface CountryDto { code: string; name: string; eea: boolean; employees: number }
export interface LocationDto { id: number; name: string; countryCode: string; employees: number }
export interface TeamDto { id: number; name: string; departmentId: number; department: string; lead: PersonRef | null; members: number }
export interface DepartmentDto { id: number; code: string; name: string; parentId: number | null; teams: TeamDto[] }
export interface Lookups { countries: CountryDto[]; locations: LocationDto[]; departments: DepartmentDto[]; managers: PersonRef[] }

export interface RoleDto {
  id: number;
  name: string;
  description: string;
  privileged: boolean;
  validityDays: number;
  provisioningGroup: string | null;
  activeGrants: number;
}

export interface SystemDto {
  id: number;
  code: string;
  name: string;
  description: string;
  owner: PersonRef | null;
  provisioning: string;
  requiredTrainingId: number | null;
  requiredTraining: string | null;
  roles: RoleDto[];
}

export interface RequestListItem {
  id: number;
  number: string;
  status: string;
  system: string;
  role: string;
  privileged: boolean;
  requester: string;
  beneficiary: string;
  createdAt: string;
  validUntil: string | null;
  awaitsMe: boolean;
}

export interface RequestDetail {
  id: number;
  number: string;
  status: string;
  requester: PersonRef;
  beneficiary: PersonRef;
  systemId: number;
  system: string;
  roleId: number;
  role: string;
  privileged: boolean;
  justification: string;
  createdAt: string;
  submittedAt: string | null;
  decidedAt: string | null;
  validUntil: string | null;
  steps: string[];
  currentStep: number;
  history: HistoryEntry[];
  allowedActions: string[];
}

export interface NewRequest {
  roleId: number;
  justification: string;
  beneficiaryId?: number | null;
  submit?: boolean;
}

export interface RequestFilter {
  scope?: 'mine' | 'approvals' | 'team' | 'all';
  status?: string;
  systemId?: number;
}

export interface CourseDto {
  id: number;
  code: string;
  title: string;
  description: string;
  mandatory: boolean;
  dueDays: number;
  validityMonths: number | null;
  passScore: number;
  assigned: number;
  completed: number;
  overdue: number;
}

export interface AssignmentDto {
  id: number;
  employee: PersonRef;
  courseId: number;
  course: string;
  status: string;
  assignedOn: string;
  dueOn: string;
  completedOn: string | null;
  validUntil: string | null;
  score: number | null;
  attempts: number;
  allowedActions: string[];
}

export interface IncidentListItem {
  id: number;
  number: string;
  title: string;
  priority: string;
  category: string;
  status: string;
  reporter: string;
  assignee: string | null;
  createdAt: string;
  dueAt: string;
  breached: boolean;
}

export interface IncidentDetail {
  id: number;
  number: string;
  title: string;
  description: string;
  priority: string;
  category: string;
  status: string;
  reporter: PersonRef;
  assignee: PersonRef | null;
  createdAt: string;
  dueAt: string;
  resolvedAt: string | null;
  resolution: string | null;
  history: HistoryEntry[];
  allowedActions: string[];
}

export interface NewIncident { title: string; description: string; priority: string; category: string }

export interface NewsDto { id: number; title: string; body: string; publishedAt: string; author: string; pinned: boolean }

export interface ExportInfo { key: string; title: string; description: string; roles: string[] }

export interface ImportChange { employeeNumber: string; name: string; kind: string; details: string }
export interface ImportReport {
  source: string;
  dryRun: boolean;
  read: number;
  created: number;
  updated: number;
  unchanged: number;
  changes: ImportChange[];
  warnings: string[];
}

export interface JobReport {
  activated: number;
  left: number;
  accessExpired: number;
  provisioned: number;
  trainingsOverdue: number;
  trainingsExpired: number;
  ticketsClosed: number;
}

export interface ChatTurn { role: 'user' | 'assistant'; text: string }
export interface AssistantLink { label: string; route: string }
export interface AssistantReply { text: string; links: AssistantLink[]; suggestions: string[]; source: string }

export interface DownloadedFile { fileName: string; contentType: string; bytes: Uint8Array }

/** Problem details (RFC 9457) as the API returns them. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    readonly title: string,
    detail: string,
    readonly errors: Record<string, string[]> = {},
  ) {
    super(detail || title);
  }
}

/** Everything the UI needs from the backend - implemented over HTTP and by the in-browser demo. */
export interface AegisApi {
  readonly mode: 'http' | 'demo';
  personas(): Promise<Persona[]>;
  me(): Promise<Me>;
  counters(): Promise<Counters>;
  employees(q: PageQuery, f: EmployeeFilter): Promise<Page<EmployeeListItem>>;
  employee(id: number): Promise<EmployeeDetail>;
  createEmployee(input: EmployeeInput): Promise<EmployeeDetail>;
  updateEmployee(id: number, input: EmployeeInput): Promise<EmployeeDetail>;
  employeeTransition(id: number, action: string, endDate?: string | null, comment?: string | null): Promise<EmployeeDetail>;
  lookups(): Promise<Lookups>;
  saveCountry(code: string, name: string, eea: boolean): Promise<CountryDto>;
  deleteCountry(code: string): Promise<void>;
  saveTeam(id: number | null, name: string, departmentId: number, leadId: number | null): Promise<TeamDto>;
  applications(search?: string): Promise<SystemDto[]>;
  requests(q: PageQuery, f: RequestFilter): Promise<Page<RequestListItem>>;
  request(id: number): Promise<RequestDetail>;
  createRequest(input: NewRequest): Promise<RequestDetail>;
  requestTransition(id: number, action: string, comment?: string | null): Promise<RequestDetail>;
  courses(): Promise<CourseDto[]>;
  assignments(q: PageQuery, scope: string, status?: string, courseId?: number): Promise<Page<AssignmentDto>>;
  assign(courseId: number, employeeIds: number[]): Promise<number>;
  assignmentTransition(id: number, action: string, score?: number | null, comment?: string | null): Promise<AssignmentDto>;
  tickets(q: PageQuery, scope: string, status?: string): Promise<Page<IncidentListItem>>;
  ticket(id: number): Promise<IncidentDetail>;
  createTicket(input: NewIncident): Promise<IncidentDetail>;
  ticketTransition(id: number, action: string, comment?: string | null): Promise<IncidentDetail>;
  news(take?: number): Promise<NewsDto[]>;
  publishNews(title: string, body: string, pinned: boolean): Promise<NewsDto>;
  deleteNews(id: number): Promise<void>;
  exports(): Promise<ExportInfo[]>;
  download(key: string, format: 'xlsx' | 'csv'): Promise<DownloadedFile>;
  importFile(fileName: string, content: string, dryRun: boolean): Promise<ImportReport>;
  runJobs(): Promise<JobReport>;
  ask(message: string, history: ChatTurn[]): Promise<AssistantReply>;
}
