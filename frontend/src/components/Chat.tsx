import { useEffect, useRef, useState } from 'react';
import { ChatTurn } from '../api/types';
import { useApp } from '../app';

interface Message extends ChatTurn {
  links?: { label: string; route: string }[];
  suggestions?: string[];
  source?: string;
}

/** Light markdown: **bold** and line breaks - nothing else is rendered as HTML. */
function Text({ text }: { text: string }) {
  return (
    <>
      {text.split('\n').map((line, i) => (
        <span key={i} className="line">
          {line.split(/(\*\*[^*]+\*\*)/g).map((part, k) => (part.startsWith('**') ? <strong key={k}>{part.slice(2, -2)}</strong> : <span key={k}>{part}</span>))}
        </span>
      ))}
    </>
  );
}

/** The assistant: always in the bottom-right corner, answers from the user's own data. */
export function Chat() {
  const app = useApp();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [input, setInput] = useState('');
  const [messages, setMessages] = useState<Message[]>([]);
  const end = useRef<HTMLDivElement>(null);
  const field = useRef<HTMLTextAreaElement>(null);

  useEffect(() => { setMessages([]); }, [app.user]);
  useEffect(() => { end.current?.scrollIntoView({ block: 'end' }); }, [messages, open]);
  useEffect(() => { if (open) field.current?.focus(); }, [open]);

  const send = async (text: string) => {
    const q = text.trim();
    if (!q || busy) return;
    setInput('');
    const history = messages.map(({ role, text: t }) => ({ role, text: t }));
    setMessages((m) => [...m, { role: 'user', text: q }]);
    setBusy(true);
    try {
      const r = await app.api.ask(q, history);
      setMessages((m) => [...m, { role: 'assistant', text: r.text, links: r.links, suggestions: r.suggestions, source: r.source }]);
    } catch (e) {
      setMessages((m) => [...m, { role: 'assistant', text: `I could not answer: ${(e as Error).message}` }]);
    } finally {
      setBusy(false);
    }
  };

  const first = app.me?.name.split(' ')[0];
  return (
    <div className={`chat${open ? ' open' : ''}`}>
      {open && (
        <section className="chat-panel" aria-label="Assistant">
          <header>
            <div>
              <strong>Assistant</strong>
              <span>Answers from your own requests, approvals, trainings and tickets</span>
            </div>
            <button type="button" className="icon" aria-label="Close the assistant" onClick={() => setOpen(false)}>×</button>
          </header>
          <div className="chat-log" aria-live="polite">
            {messages.length === 0 && (
              <div className="msg assistant">
                <Text text={`Hi${first ? ` ${first}` : ''}! Ask me in English or Hungarian - for example:`} />
                <div className="suggest">
                  {['What is waiting for my approval?', 'Mik a nyitott kéréseim?', 'Ki Fekete Anna vezetője?', 'Who owns the CRM?'].map((s) => (
                    <button key={s} type="button" onClick={() => send(s)}>{s}</button>
                  ))}
                </div>
              </div>
            )}
            {messages.map((m, i) => (
              <div key={i} className={`msg ${m.role}`}>
                <Text text={m.text} />
                {m.links && m.links.length > 0 && (
                  <div className="links">
                    {m.links.map((l) => <a key={l.route + l.label} href={`#${l.route}`} onClick={() => setOpen(false)}>{l.label}</a>)}
                  </div>
                )}
                {m.suggestions && m.suggestions.length > 0 && (
                  <div className="suggest">{m.suggestions.map((s) => <button key={s} type="button" onClick={() => send(s)}>{s}</button>)}</div>
                )}
              </div>
            ))}
            {busy && <div className="msg assistant typing" aria-label="The assistant is typing"><span /><span /><span /></div>}
            <div ref={end} />
          </div>
          <form className="chat-input" onSubmit={(e) => { e.preventDefault(); void send(input); }}>
            <textarea
              ref={field}
              rows={1}
              value={input}
              placeholder="Type a question"
              aria-label="Your question"
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void send(input); } }}
            />
            <button type="submit" className="btn primary" disabled={busy || !input.trim()}>Ask</button>
          </form>
        </section>
      )}
      <button type="button" className="chat-button" aria-expanded={open} aria-label={open ? 'Close the assistant' : 'Open the assistant'} onClick={() => setOpen(!open)}>
        <svg viewBox="0 0 32 32" width="26" height="26" aria-hidden="true">
          <path d="M5 7.5A3.5 3.5 0 0 1 8.5 4h15A3.5 3.5 0 0 1 27 7.5v10a3.5 3.5 0 0 1-3.5 3.5H14l-6 6v-6h.5A3.5 3.5 0 0 1 5 17.5z" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinejoin="round" />
          <circle cx="11.5" cy="12.5" r="1.6" fill="currentColor" /><circle cx="16" cy="12.5" r="1.6" fill="currentColor" /><circle cx="20.5" cy="12.5" r="1.6" fill="currentColor" />
        </svg>
        <span>Ask</span>
      </button>
    </div>
  );
}
