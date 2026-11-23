// Port of Aegis.Application.Assistant: keyword intents (English and Hungarian) answered from the user's own data.

import { AssistantLink, AssistantReply, ChatTurn } from '../api/types';
import { Actor, fullName, humanize, OPEN_REQUEST, REQUEST_STATES } from './domain';
import { Db, requestContext } from './store';

export type Intent =
  | 'Unknown' | 'Help' | 'MyRequests' | 'Approvals' | 'WhoIsManager' | 'FindPerson' | 'MyTrainings' | 'MyAccess' | 'HowToRequest' | 'MyTickets' | 'NewTicket' | 'SystemOwner';

const RULES: [Intent, string[], string[]?][] = [
  ['Approvals', ['approve', 'approval', 'jovahagy', 'engedelyez', 'decide', 'dontes']],
  ['HowToRequest', ['how', 'hogyan', 'hogy kell', 'how do i', 'mikent'], ['request', 'kerj', 'igeny', 'access', 'jogosultsag']],
  ['MyRequests', ['my request', 'requests', 'kereseim', 'kerelmeim', 'igenyeim', 'nyitott keres', 'request status']],
  ['MyTrainings', ['training', 'course', 'trening', 'kepzes', 'tanfolyam', 'oktatas']],
  ['MyAccess', ['my access', 'what access', 'permissions', 'jogaim', 'jogosultsagaim', 'hozzafereseim', 'mihez ferek']],
  ['NewTicket', ['report a problem', 'new ticket', 'open a ticket', 'hibat jelent', 'uj jegy', 'bejelent', 'nem mukodik', "doesn't work", 'does not work', 'broken']],
  ['MyTickets', ['ticket', 'incident', 'jegy', 'hibajegy', 'bejelentesem']],
  ['Help', ['help', 'segits', 'segitseg', 'mit tudsz', 'what can you', 'hello', 'hi', 'szia']],
];

const MANAGER_EN = /(?:who(?:'s| is)\s+the\s+manager\s+of|manager\s+of|who\s+manages)\s+(?<name>[\p{L} .'-]+?)\s*\??$/iu;
const MANAGER_HU = /^(?:ki\s+(?:a\s+|az\s+)?)?(?<name>[\p{L} .'-]+?)\s+(?:vezetoje|fonoke|managere)/iu;
const OWNER_OF = /(?:who\s+owns|owner\s+of|ki\s+a\s+gazdaja|gazdaja)\s+(?:the\s+|a\s+|az\s+)?(?<name>[\p{L}\d .-]+?)\s*(?:app|application|rendszer|alkalmazas)?\s*\??$/iu;
const FIND_PERSON = /(?:who\s+is|find|search|keresd meg|ki az a|ki az|ki)\s+(?<name>[\p{L} .'-]{3,}?)\s*\??$/iu;

/** Lower case without accents ("Jóváhagyás" → "jovahagyas"). */
export const fold = (s: string) => s.trim().toLowerCase().normalize('NFD').replace(/\p{Mn}/gu, '').normalize('NFC');

function containsWord(text: string, keyword: string) {
  for (let i = text.indexOf(keyword); i >= 0; i = text.indexOf(keyword, i + 1)) {
    const startOk = i === 0 || !/[\p{L}\p{N}]/u.test(text[i - 1]);
    const end = i + keyword.length;
    const endOk = keyword.length >= 4 || end === text.length || !/[\p{L}\p{N}]/u.test(text[end]);
    if (startOk && endOk) return true;
  }
  return false;
}

const clean = (name: string) => name.trim().replace(/[?.!]+$/, '').trim();

export function recognize(message: string): { intent: Intent; subject?: string } {
  const text = fold(message);
  if (!text) return { intent: 'Help' };
  let m = MANAGER_EN.exec(text) ?? MANAGER_HU.exec(text);
  if (m?.groups) return { intent: 'WhoIsManager', subject: clean(m.groups.name) };
  m = OWNER_OF.exec(text);
  if (m?.groups) return { intent: 'SystemOwner', subject: clean(m.groups.name) };
  for (const [intent, anyOf, also] of RULES) {
    if (!anyOf.some((k) => containsWord(text, k))) continue;
    if (also && !also.some((k) => containsWord(text, k))) continue;
    return { intent };
  }
  m = FIND_PERSON.exec(text);
  if (m?.groups) return { intent: 'FindPerson', subject: clean(m.groups.name) };
  return { intent: 'Unknown' };
}

const SUGGESTIONS = ['What are my open requests?', 'What is waiting for my approval?', 'Which trainings are due?', 'How do I request access?'];
const bullets = (lines: string[]) => lines.map((l) => '• ' + l).join('\n');
const reply = (text: string, links: AssistantLink[] = [], suggestions: string[] = []): AssistantReply => ({ text, links, suggestions, source: 'built-in' });

export function answer(db: Db, me: Actor, message: string, _history: ChatTurn[], now: Date): AssistantReply {
  const r = recognize(message.slice(0, 1000));
  const emp = (id: number | null) => db.employees.find((e) => e.id === id);
  const roleName = (id: number) => {
    const role = db.roles.find((x) => x.id === id)!;
    return `${db.systems.find((s) => s.id === role.systemId)!.name} / ${role.name}`;
  };
  switch (r.intent) {
    case 'Help':
      return reply("Hi! I'm the Aegis assistant. Ask me about your access requests, approvals waiting for you, your trainings, your tickets - or who someone is and who their manager is.", [], SUGGESTIONS);
    case 'MyRequests': {
      const open = db.requests.filter((x) => (x.requesterId === me.id || x.beneficiaryId === me.id) && OPEN_REQUEST.includes(x.status));
      return open.length === 0
        ? reply('You have no open access requests.', [{ label: 'Request access', route: '/requests/new' }])
        : reply(`You have ${open.length} open request${open.length === 1 ? '' : 's'}:\n` + bullets(open.slice(0, 8).map((x) => `${x.number} ${roleName(x.roleId)} - ${humanize(x.status)}`)), [{ label: 'My requests', route: '/requests' }]);
    }
    case 'Approvals': {
      const waiting = db.requests.filter((x) => ['PendingManager', 'PendingOwner', 'PendingSecurity'].includes(x.status)
        && x.requesterId !== me.id && x.beneficiaryId !== me.id && !x.approverIds.includes(me.id) && REQUEST_STATES[x.status].canDecide(x, requestContext(db, x), me));
      return waiting.length === 0
        ? reply('Nothing is waiting for your approval. 🎉')
        : reply(`${waiting.length} request${waiting.length === 1 ? ' is' : 's are'} waiting for your decision:\n` + bullets(waiting.slice(0, 8).map((x) => `${x.number} ${fullName(emp(x.beneficiaryId)!)}: ${roleName(x.roleId)}`)), [{ label: 'Open approvals', route: '/approvals' }]);
    }
    case 'MyTrainings': {
      const due = db.assignments.filter((a) => a.employeeId === me.id && ['Assigned', 'InProgress', 'Overdue', 'Expired'].includes(a.status)).sort((a, b) => a.dueOn.localeCompare(b.dueOn));
      return due.length === 0
        ? reply('You have no open trainings - all done.', [{ label: 'My trainings', route: '/trainings' }])
        : reply('Your open trainings:\n' + bullets(due.map((a) => `${db.courses.find((c) => c.id === a.courseId)!.title} - due ${a.dueOn}${a.status === 'Overdue' || a.status === 'Expired' ? ` (${a.status.toLowerCase()})` : ''}`)), [{ label: 'My trainings', route: '/trainings' }]);
    }
    case 'MyAccess': {
      const n = now.toISOString();
      const access = db.grants.filter((g) => g.employeeId === me.id && !g.endedAt && g.validUntil > n);
      return access.length === 0
        ? reply('You have no access granted through Aegis yet.', [{ label: 'Request access', route: '/requests/new' }])
        : reply('Your current access:\n' + bullets(access.map((g) => `${roleName(g.roleId)} until ${g.validUntil.slice(0, 10)}`).sort()), [{ label: 'My profile', route: '/me' }]);
    }
    case 'HowToRequest':
      return reply('1. Open **Request access** and pick the application and role.\n2. Explain why you need it (at least 15 characters).\n3. Your manager approves, then the application owner; privileged roles also need a security officer.\n4. Access is set up automatically for most applications and is valid for the role\'s validity period.',
        [{ label: 'Request access', route: '/requests/new' }]);
    case 'MyTickets': {
      const open = db.incidents.filter((i) => i.reporterId === me.id && ['New', 'InProgress', 'OnHold'].includes(i.status));
      return open.length === 0
        ? reply('You have no open tickets.', [{ label: 'Report a problem', route: '/tickets/new' }])
        : reply('Your open tickets:\n' + bullets(open.map((i) => `${i.number} ${i.title} - ${humanize(i.status)}`)), [{ label: 'My tickets', route: '/tickets' }]);
    }
    case 'NewTicket':
      return reply('Sorry to hear that. Open a ticket with a short description and the support team will pick it up - P1 tickets within 4 hours.', [{ label: 'Report a problem', route: '/tickets/new' }]);
    case 'WhoIsManager':
    case 'FindPerson': {
      const words = fold(r.subject ?? '').split(' ').filter(Boolean);
      const hits = words.length === 0 ? [] : db.employees.filter((e) => e.status !== 'Left')
        .filter((e) => { const hay = fold(fullName(e)).split(' '); return words.every((w) => hay.some((h) => h.startsWith(w))); })
        .sort((a, b) => a.lastName.localeCompare(b.lastName)).slice(0, 10);
      if (hits.length === 0) return reply(`I couldn't find anyone called "${r.subject}".`, [{ label: 'Employee directory', route: '/employees' }]);
      const link = (e: (typeof hits)[number]) => ({ label: fullName(e), route: `/employees/${e.id}` });
      if (hits.length > 1 && r.intent === 'WhoIsManager')
        return reply(`Several people match "${r.subject}":\n` + bullets(hits.slice(0, 5).map((h) => `${fullName(h)} - ${h.jobTitle}`)) + '\nWhich one do you mean?', hits.slice(0, 5).map(link));
      const p = hits[0];
      const manager = emp(p.managerId);
      const team = db.teams.find((t) => t.id === p.teamId)?.name;
      const text = r.intent === 'WhoIsManager'
        ? (manager ? `${fullName(p)}'s manager is **${fullName(manager)}**.` : `${fullName(p)} has no manager in Aegis.`)
        : `**${fullName(p)}** - ${p.jobTitle}${team ? `, ${team}` : ''}. Manager: ${manager ? fullName(manager) : '–'}. E-mail: ${p.email}.` +
          (hits.length > 1 ? `\n(${hits.length - 1} more match${hits.length === 2 ? 'es' : ''} in the directory.)` : '');
      return reply(text, hits.slice(0, 3).map(link));
    }
    case 'SystemOwner': {
      const f = fold(r.subject ?? '');
      const systems = db.systems.filter((s) => fold(s.code) === f || fold(s.name).includes(f));
      return systems.length === 0
        ? reply(`I don't know an application called "${r.subject}".`, [{ label: 'Applications', route: '/applications' }])
        : reply(systems.map((s) => `**${s.name}** is owned by ${fullName(emp(s.ownerId)!)}.`).join('\n'), [{ label: 'Applications', route: '/applications' }]);
    }
    default:
      return reply("I'm not sure what you mean. I can tell you about your requests, approvals, trainings, access and tickets, find colleagues and their managers, and show you how to request access.", [], SUGGESTIONS);
  }
}
