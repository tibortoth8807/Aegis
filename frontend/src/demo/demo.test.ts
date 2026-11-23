import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ApiError } from '../api/types';
import { demoApi } from './api';
import { recognize } from './assistant';
import { ASSIGNMENT_STATES, EMPLOYEE_STATES, INCIDENT_STATES, REQUEST_STATES } from './domain';
import { columnName, csv, parseCsv, parseLdif } from './files';
import { createSeededDb, demoData } from './store';

const P = demoData.personas;
const fresh = (who: { current: string }) => demoApi(() => who.current, createSeededDb(new Date()));
const code = async (p: Promise<unknown>) => {
  try { await p; } catch (e) { return `${(e as ApiError).status} ${(e as ApiError).code}`; }
  return 'ok';
};

test('the same intents as the C# recognizer (shared test vectors)', () => {
  const vectors: [string, string][] = [
    ['What are my open requests?', 'MyRequests'], ['Mik a nyitott kéréseim?', 'MyRequests'], ['Is anything waiting for my approval?', 'Approvals'],
    ['Mit kell jóváhagynom?', 'Approvals'], ['How do I request access to the CRM?', 'HowToRequest'], ['Hogyan igényelhetek jogosultságot?', 'HowToRequest'],
    ['Which trainings are due?', 'MyTrainings'], ['Milyen tréningjeim vannak?', 'MyTrainings'], ['What access do I have?', 'MyAccess'],
    ['The VPN does not work', 'NewTicket'], ['status of my tickets', 'MyTickets'], ['hi', 'Help'], ['Tell me about the history of Rome', 'Unknown'],
  ];
  for (const [m, intent] of vectors) assert.equal(recognize(m).intent, intent, m);
  assert.deepEqual(recognize('Who is the manager of Anna Kiss?'), { intent: 'WhoIsManager', subject: 'anna kiss' });
  assert.deepEqual(recognize('Ki Kiss Anna vezetője?'), { intent: 'WhoIsManager', subject: 'kiss anna' });
  assert.deepEqual(recognize('manager of Bob'), { intent: 'WhoIsManager', subject: 'bob' });
  assert.deepEqual(recognize('Who owns the CRM?'), { intent: 'SystemOwner', subject: 'crm' });
});

test('allowed actions are derived from the overridden methods', () => {
  assert.deepEqual(REQUEST_STATES.Draft.allowedActions, ['cancel', 'submit']);
  assert.deepEqual(REQUEST_STATES.PendingManager.allowedActions, ['approve', 'cancel', 'reject']);
  assert.deepEqual(REQUEST_STATES.Active.allowedActions, ['expire', 'revoke']);
  assert.ok(REQUEST_STATES.Rejected.isFinal);
  assert.deepEqual(EMPLOYEE_STATES.Preboarding.allowedActions, ['activate', 'give-notice']);
  assert.deepEqual(ASSIGNMENT_STATES.Overdue.allowedActions, ['complete', 'start', 'waive']);
  assert.deepEqual(INCIDENT_STATES.Resolved.allowedActions, ['close', 'reopen']);
});

test('the seed replays through every workflow without breaking a rule', () => {
  const db = createSeededDb(new Date('2026-11-23T09:00:00Z'));
  assert.equal(db.employees.length, demoData.employees.length);
  assert.equal(db.requests.length, demoData.requests.length);
  assert.ok(db.requests.some((r) => r.status === 'Active') && db.requests.some((r) => r.status === 'PendingSecurity'));
});

test('access request end to end, with the four-eyes rules', async () => {
  const who = { current: P.employee };
  const api = fresh(who);
  const me = await api.me();
  const profile = await api.employee(me.id);
  const git = (await api.applications()).find((a) => a.code === 'GIT')!;
  const dev = git.roles.find((r) => r.name === 'Developer')!;

  assert.equal(await code(api.createRequest({ roleId: dev.id, justification: 'pls' })), '400 validation');
  const r = await api.createRequest({ roleId: dev.id, justification: 'Contributing to the analytics dashboards' });
  assert.equal(r.status, 'PendingManager');
  assert.equal(await code(api.createRequest({ roleId: dev.id, justification: 'Contributing to the analytics dashboards' })), '422 request.duplicate');
  assert.equal(await code(api.requestTransition(r.id, 'approve')), '403 forbidden');

  who.current = profile.manager!.employeeNumber;
  assert.ok((await api.requests({}, { scope: 'approvals' })).items.some((x) => x.id === r.id && x.awaitsMe));
  assert.equal((await api.requestTransition(r.id, 'approve')).status, 'PendingOwner');
  who.current = git.owner!.employeeNumber;
  const done = await api.requestTransition(r.id, 'approve', 'ok');
  assert.equal(done.status, 'Active');
  assert.deepEqual(done.history.map((h) => h.action), ['provision', 'approve', 'approve', 'submit', 'create']);
  who.current = P.employee;
  assert.ok((await api.employee(me.id)).access.some((g) => g.role === 'Developer'));
  assert.equal(await code(api.requestTransition(r.id, 'cancel')), '409 state.transition');
});

test('required trainings, exports by role, jobs and time travel', async () => {
  const who = { current: P.employee };
  const api = fresh(who);
  const cloud = (await api.applications()).find((a) => a.code === 'CLD')!;
  assert.equal(await code(api.createRequest({ roleId: cloud.roles[0].id, justification: 'Need to look at the production logs' })), '422 request.training_required');
  assert.equal(await code(api.download('admins', 'xlsx')), '403 forbidden');
  who.current = P.security;
  const f = await api.download('admins', 'xlsx');
  assert.equal(f.bytes[0], 0x50);
  who.current = P.admin;
  assert.equal((await api.runJobs()).activated, 0);
  api.travel(40);
  const later = await api.runJobs();
  assert.equal(later.activated, 2);
  assert.equal(later.left, 1);
  assert.ok(later.trainingsOverdue > 0);
});

test('csv writer and directory readers behave like the C# ones', () => {
  const text = new TextDecoder().decode(csv({ title: 'People', columns: ['Name', 'Age', 'Start', 'Note'], rows: [['Kiss Anna', 42, '2024-03-01', 'says "hi"; twice'], ['=HYPERLINK("x")', 7, null, null]] }).slice(3));
  assert.deepEqual(text.split('\r\n').slice(0, 3), ['Name;Age;Start;Note', 'Kiss Anna;42;2024-03-01;"says ""hi""; twice"', `"'=HYPERLINK(""x"")";7;;`]);
  assert.deepEqual([0, 25, 26, 701, 702].map(columnName), ['A', 'Z', 'AA', 'ZZ', 'AAA']);
  const ldif = 'version: 1\n\ndn: uid=E10002,ou=people,dc=aegis,dc=example\nuid: E10002\nemployeeNumber: E10002\ngivenName: Anna\nsn:: S2lzcyDDgWdp\ntitle: Senior Analyst, Customer\n  Insights\nmanager: uid=E10001,ou=people,dc=aegis,dc=example\n\ndn: cn=g,ou=groups\ncn: g\n';
  const [anna] = parseLdif(ldif);
  assert.equal(anna.lastName, 'Kiss Ági');
  assert.equal(anna.jobTitle, 'Senior Analyst, Customer Insights');
  assert.equal(anna.managerNumber, 'E10001');
  const rows = parseCsv('﻿lastName;firstName;employeeNumber;email;jobTitle;team;type\r\nKiss;Anna;E10002;a@x;"Analyst; senior";Sales;internal\r\n');
  assert.equal(rows[0].jobTitle, 'Analyst; senior');
});
