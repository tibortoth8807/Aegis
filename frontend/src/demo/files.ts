// File formats of the demo backend - ports of Aegis.Application.Exports / Import: CSV and Excel writers, LDIF and
// CSV directory readers, and the directory import itself.

import { ImportReport } from '../api/types';
import { Ctx, Employee, fullName, hire, validation } from './domain';
import { Db, nextId } from './store';

// ------------------------------------------------------------------ tables → CSV / XLSX

export type Cell = string | number | boolean | null | undefined;
export interface Table { title: string; columns: string[]; rows: Cell[][] }

const text = (v: Cell) => (v === null || v === undefined ? '' : typeof v === 'boolean' ? (v ? 'yes' : 'no') : String(v));

export function csv(t: Table, sep = ';'): Uint8Array {
  const esc = (s: string) => {
    if (s.length > 0 && '=+-@'.includes(s[0]) && Number.isNaN(Number(s))) s = "'" + s;   // formula injection
    return s.includes(sep) || /["\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  const lines = [t.columns.map(esc).join(sep), ...t.rows.map((r) => r.map((v) => esc(text(v))).join(sep))];
  const body = new TextEncoder().encode(lines.join('\r\n') + '\r\n');
  const out = new Uint8Array(body.length + 3);
  out.set([0xef, 0xbb, 0xbf]);
  out.set(body, 3);
  return out;
}

const xml = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&apos;');

export const columnName = (index: number) => {
  let s = '';
  for (index++; index > 0; index = Math.floor((index - 1) / 26)) s = String.fromCharCode(65 + ((index - 1) % 26)) + s;
  return s;
};

/** Same minimal SpreadsheetML as the C# XlsxWriter: frozen, coloured header, auto-filter, column widths. */
export function xlsx(t: Table): Uint8Array {
  const sheetName = xml(t.title.replace(/[[\]:*?/\\]/g, '').slice(0, 31));
  const widths = t.columns.map((c, i) => Math.min(60, Math.max(8, Math.max(c.length, ...t.rows.map((r) => text(r[i]).length)) + 2)));
  const cell = (v: Cell, ref: string, header: boolean) => {
    if (v === null || v === undefined) return '';
    const s = header ? ' s="1"' : '';
    if (typeof v === 'number' && !header) return `<c r="${ref}"${s}><v>${v}</v></c>`;
    return `<c r="${ref}" t="inlineStr"${s}><is><t xml:space="preserve">${xml(text(v))}</t></is></c>`;
  };
  const row = (cells: Cell[], r: number, header: boolean) => `<row r="${r}">${cells.map((v, c) => cell(v, columnName(c) + r, header)).join('')}</row>`;
  const sheet =
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">' +
    '<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>' +
    `<cols>${widths.map((w, i) => `<col min="${i + 1}" max="${i + 1}" width="${w}" customWidth="1"/>`).join('')}</cols>` +
    `<sheetData>${row(t.columns, 1, true)}${t.rows.map((r, i) => row(r, i + 2, false)).join('')}</sheetData>` +
    (t.columns.length ? `<autoFilter ref="A1:${columnName(t.columns.length - 1)}${t.rows.length + 1}"/>` : '') +
    '</worksheet>';
  const files: [string, string][] = [
    ['[Content_Types].xml', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>'],
    ['_rels/.rels', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>'],
    ['xl/_rels/workbook.xml.rels', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>'],
    ['xl/workbook.xml', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="${sheetName}" sheetId="1" r:id="rId1"/></sheets></workbook>`],
    ['xl/styles.xml', '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1F3A5F"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>'],
    ['xl/worksheets/sheet1.xml', sheet],
  ];
  return zip(files.map(([name, content]) => [name, new TextEncoder().encode(content)]));
}

// minimal ZIP writer (stored, no compression) - enough for the browser demo
const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();
export function crc32(b: Uint8Array) {
  let c = 0xffffffff;
  for (let i = 0; i < b.length; i++) c = CRC_TABLE[(c ^ b[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

export function zip(entries: [string, Uint8Array][]): Uint8Array {
  const chunks: Uint8Array[] = [];
  const central: Uint8Array[] = [];
  let offset = 0;
  for (const [name, data] of entries) {
    const nameBytes = new TextEncoder().encode(name);
    const crc = crc32(data);
    const local = new DataView(new ArrayBuffer(30));
    local.setUint32(0, 0x04034b50, true);
    local.setUint16(4, 20, true);
    local.setUint16(6, 0x0800, true);   // UTF-8 names
    local.setUint16(8, 0, true);        // stored
    local.setUint32(14, crc, true);
    local.setUint32(18, data.length, true);
    local.setUint32(22, data.length, true);
    local.setUint16(26, nameBytes.length, true);
    chunks.push(new Uint8Array(local.buffer), nameBytes, data);
    const dir = new DataView(new ArrayBuffer(46));
    dir.setUint32(0, 0x02014b50, true);
    dir.setUint16(4, 20, true);
    dir.setUint16(6, 20, true);
    dir.setUint16(8, 0x0800, true);
    dir.setUint32(16, crc, true);
    dir.setUint32(20, data.length, true);
    dir.setUint32(24, data.length, true);
    dir.setUint16(28, nameBytes.length, true);
    dir.setUint32(42, offset, true);
    central.push(new Uint8Array(dir.buffer), nameBytes);
    offset += 30 + nameBytes.length + data.length;
  }
  const dirSize = central.reduce((n, c) => n + c.length, 0);
  const end = new DataView(new ArrayBuffer(22));
  end.setUint32(0, 0x06054b50, true);
  end.setUint16(8, entries.length, true);
  end.setUint16(10, entries.length, true);
  end.setUint32(12, dirSize, true);
  end.setUint32(16, offset, true);
  const parts = [...chunks, ...central, new Uint8Array(end.buffer)];
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) { out.set(p, at); at += p.length; }
  return out;
}

// ------------------------------------------------------------------ directory files → records

export interface DirectoryRecord {
  employeeNumber: string; firstName: string; lastName: string; email: string; jobTitle: string; managerNumber: string | null;
  team: string | null; location: string | null; country: string | null; external: boolean; company: string | null; disabled: boolean;
}

export const managerNumber = (dn: string | null | undefined) => {
  if (!dn?.trim()) return null;
  const rdn = dn.split(',')[0];
  const eq = rdn.indexOf('=');
  return eq < 0 ? null : rdn.slice(eq + 1).trim();
};

export function parseLdif(ldif: string): DirectoryRecord[] {
  const lines: string[] = [];
  for (const raw of ldif.replace(/\r\n/g, '\n').split('\n')) {
    if (raw.startsWith(' ') && lines.length && lines[lines.length - 1].length) lines[lines.length - 1] += raw.slice(1);
    else lines.push(raw.trimEnd());
  }
  const entries: Map<string, string[]>[] = [];
  let current = new Map<string, string[]>();
  for (const line of [...lines, '']) {
    if (!line) { if (current.size) entries.push(current); current = new Map(); continue; }
    if (line.startsWith('#')) continue;
    const colon = line.indexOf(':');
    if (colon <= 0) throw validation('file', `Not an LDIF line: "${line}"`);
    const name = line.slice(0, colon).trim().toLowerCase();
    if (name === 'version') continue;
    let value: string;
    if (line[colon + 1] === ':') {
      try { value = new TextDecoder().decode(Uint8Array.from(atob(line.slice(colon + 2).trim()), (c) => c.charCodeAt(0))); }
      catch { throw validation('file', `Invalid base64 value for ${name}.`); }
    } else value = line.slice(colon + 1).trim();
    current.set(name, [...(current.get(name) ?? []), value]);
  }
  return entries.filter((e) => e.has('employeenumber') || e.has('uid')).map((e) => {
    const get = (a: string) => e.get(a)?.[0] ?? null;
    const uac = Number(get('useraccountcontrol') ?? 0);
    return {
      employeeNumber: get('employeenumber') ?? get('uid') ?? '', firstName: get('givenname') ?? '', lastName: get('sn') ?? '', email: get('mail') ?? '',
      jobTitle: get('title') ?? '', managerNumber: managerNumber(get('manager')), team: get('department') ?? get('ou'), location: get('l'), country: get('c'),
      external: get('employeetype')?.toLowerCase() === 'external', company: get('o'), disabled: get('nsaccountlock')?.toLowerCase() === 'true' || (uac & 2) === 2,
    };
  });
}

function csvRows(text: string, sep: string): string[][] {
  const rows: string[][] = [];
  let row: string[] = [];
  let field = '';
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"' && text[i + 1] === '"') { field += '"'; i++; }
      else if (c === '"') quoted = false;
      else field += c;
    } else if (c === '"') quoted = true;
    else if (c === sep) { row.push(field); field = ''; }
    else if (c === '\n') { row.push(field.replace(/\r$/, '')); rows.push(row); row = []; field = ''; }
    else field += c;
  }
  if (field || row.length) { row.push(field.replace(/\r$/, '')); rows.push(row); }
  return rows;
}

export function parseCsv(csvText: string): DirectoryRecord[] {
  const text = csvText.replace(/^﻿/, '');
  const first = text.split('\n')[0];
  const sep = (first.match(/;/g)?.length ?? 0) > (first.match(/,/g)?.length ?? 0) ? ';' : ',';
  const rows = csvRows(text, sep).filter((r) => r.length > 1 || r[0]?.length);
  if (!rows.length) return [];
  const header = rows[0].map((h) => h.trim().toLowerCase().replace(/[ _]/g, ''));
  const missing = ['employeenumber', 'firstname', 'lastname', 'email'].filter((r) => !header.includes(r));
  if (missing.length) throw validation('file', `Missing column(s): ${missing.join(', ')}.`);
  const cell = (r: string[], c: string) => {
    const i = header.indexOf(c);
    return i >= 0 && i < r.length && r[i].trim() ? r[i].trim() : null;
  };
  return rows.slice(1).map((r) => ({
    employeeNumber: cell(r, 'employeenumber') ?? '', firstName: cell(r, 'firstname') ?? '', lastName: cell(r, 'lastname') ?? '', email: cell(r, 'email') ?? '',
    jobTitle: cell(r, 'jobtitle') ?? '', managerNumber: cell(r, 'managernumber'), team: cell(r, 'team'), location: cell(r, 'location'), country: cell(r, 'country'),
    external: cell(r, 'type')?.toLowerCase() === 'external', company: cell(r, 'company'), disabled: ['true', 'yes', '1'].includes(cell(r, 'disabled') ?? ''),
  }));
}

// ------------------------------------------------------------------ the import (port of DirectoryImportService)

export function directoryImport(db: Db, fileName: string, content: string, dryRun: boolean, ctx: Ctx, today: string): ImportReport {
  const ldif = fileName.toLowerCase().endsWith('.ldif') || /^\s*(dn|version):/.test(content);
  const records = ldif ? parseLdif(content) : parseCsv(content);
  const changes: ImportReport['changes'] = [];
  const warnings: string[] = [];
  let created = 0, updated = 0, unchanged = 0;
  const existing = new Map(db.employees.map((e) => [e.employeeNumber.toUpperCase(), e] as const));
  const seen = new Set<string>();
  const importCtx = { ...ctx, actor: { ...ctx.actor, name: `${ctx.actor.name} (import)` }, comment: `Imported from ${fileName}` };

  for (const r of records) {
    const number = r.employeeNumber.trim().toUpperCase();
    if (!number || seen.has(number)) {
      warnings.push(!number ? `${r.firstName} ${r.lastName}: no employee number - skipped.` : `${number} appears twice - second entry skipped.`);
      continue;
    }
    seen.add(number);
    if (r.disabled) {
      const gone = existing.get(number);
      if (gone && gone.status !== 'Left' && gone.status !== 'Offboarding') warnings.push(`${number} ${fullName(gone)} is disabled in the directory but active here - check whether they left.`);
      continue;
    }
    const team = r.team ? db.teams.find((t) => t.name.toLowerCase() === r.team!.trim().toLowerCase()) : undefined;
    if (r.team && !team) warnings.push(`${number}: unknown team "${r.team}" - left empty.`);
    let location = r.location ? db.locations.find((l) => l.name.toLowerCase() === r.location!.trim().toLowerCase()) : undefined;
    if (r.location && !location) {
      location = { id: dryRun ? -1 : nextId(db, 'locations'), name: r.location.trim(), countryCode: (r.country ?? '').toUpperCase() };
      if (!dryRun) db.locations.push(location);
    }
    const nationality = r.country && r.country.length === 2 && db.countries.some((c) => c.code === r.country!.toUpperCase()) ? r.country.toUpperCase() : null;

    let e = existing.get(number);
    if (!e) {
      if (r.external) { warnings.push(`${number} ${r.firstName} ${r.lastName} is external - register externals manually.`); continue; }
      const fresh: Employee = {
        id: 0, employeeNumber: number, firstName: r.firstName.trim(), lastName: r.lastName.trim(), email: r.email.trim().toLowerCase(), jobTitle: r.jobTitle.trim(),
        type: 'Internal', externalCompany: null, nationalityCode: nationality, locationId: location?.id ?? null, teamId: team?.id ?? null, managerId: null,
        startDate: today, endDate: null, portalRoles: ['Employee'], source: 'ldap', status: 'Active', history: [], version: 1,
      };
      existing.set(number, fresh);
      changes.push({ employeeNumber: number, name: fullName(fresh), kind: 'created', details: fresh.jobTitle + (r.team ? `, ${r.team}` : '') });
      created++;
      if (!dryRun) {
        hire(fresh, today, importCtx);
        fresh.id = nextId(db, 'employees');
        db.employees.push(fresh);
      }
      continue;
    }
    const diff: string[] = [];
    const target = e;
    const set = <K extends keyof Employee>(label: string, key: K, value: Employee[K], shown?: [string | null, string | null]) => {
      if (target[key] === value) return;
      diff.push(`${label}: ${shown?.[0] ?? target[key] ?? '–'} → ${shown?.[1] ?? value ?? '–'}`);
      if (!dryRun) target[key] = value;
    };
    set('first name', 'firstName', r.firstName.trim());
    set('last name', 'lastName', r.lastName.trim());
    set('e-mail', 'email', r.email.trim().toLowerCase());
    set('job title', 'jobTitle', r.jobTitle.trim());
    if (team) set('team', 'teamId', team.id, [db.teams.find((t) => t.id === target.teamId)?.name ?? null, team.name]);
    if (location) set('location', 'locationId', location.id, [db.locations.find((l) => l.id === target.locationId)?.name ?? null, location.name]);
    if (nationality) set('nationality', 'nationalityCode', nationality);
    if (diff.length) { changes.push({ employeeNumber: number, name: fullName(e), kind: 'updated', details: diff.join('; ') }); updated++; }
    else unchanged++;
    e = undefined;
  }

  for (const r of records.filter((r) => !r.disabled && r.managerNumber)) {
    const e = existing.get(r.employeeNumber.trim().toUpperCase());
    if (!e) continue;
    const m = existing.get(r.managerNumber!.trim().toUpperCase());
    if (!m) { warnings.push(`${e.employeeNumber}: manager ${r.managerNumber} is not known - reporting line not changed.`); continue; }
    if (m === e || (e.managerId !== null && e.managerId === m.id && m.id !== 0)) continue;
    const text = `manager → ${fullName(m)}`;
    const line = changes.findIndex((c) => c.employeeNumber === e.employeeNumber);
    if (line >= 0) changes[line] = { ...changes[line], details: `${changes[line].details}; ${text}` };
    else { changes.push({ employeeNumber: e.employeeNumber, name: fullName(e), kind: 'updated', details: text }); updated++; unchanged--; }
    if (!dryRun) e.managerId = m.id;
  }

  for (const e of db.employees.filter((e) => e.source === 'ldap' && !seen.has(e.employeeNumber) && e.status !== 'Left'))
    warnings.push(`${e.employeeNumber} ${fullName(e)} is no longer in the directory - check whether they left.`);

  return { source: fileName, dryRun, read: records.length, created, updated, unchanged, changes, warnings };
}
