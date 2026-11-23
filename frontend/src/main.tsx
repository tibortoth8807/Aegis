import { StrictMode, useEffect } from 'react';
import { createRoot } from 'react-dom/client';
import { AppContext, match, useApp, useAppState, useRoute } from './app';
import { Chat } from './components/Chat';
import { Shell } from './components/Shell';
import { Loading, Toasts } from './components/ui';
import { AdminTools, Exports, Import } from './pages/Admin';
import { Applications, Organisation } from './pages/Catalog';
import { EmployeeForm, Employees, EmployeeView } from './pages/Employees';
import { Home } from './pages/Home';
import { NewRequest, Requests, RequestView } from './pages/Requests';
import { NewTicket, Tickets, TicketView } from './pages/Tickets';
import { Trainings } from './pages/Trainings';
import './styles.css';

function Page({ path }: { path: string }) {
  let m: Record<string, string> | null;
  if (path === '/') return <Home />;
  if (path === '/employees') return <Employees />;
  if (path === '/employees/new') return <EmployeeForm id={null} />;
  if ((m = match('/employees/:id/edit', path))) return <EmployeeForm id={Number(m.id)} />;
  if ((m = match('/employees/:id', path))) return <EmployeeView id={Number(m.id)} />;
  if (path === '/me') return <MeRedirect />;
  if (path === '/requests') return <Requests />;
  if (path === '/requests/new') return <NewRequest />;
  if ((m = match('/requests/:id', path))) return <RequestView id={Number(m.id)} />;
  if (path === '/approvals') return <Requests approvals />;
  if (path === '/applications') return <Applications />;
  if (path === '/organisation') return <Organisation />;
  if (path === '/trainings') return <Trainings />;
  if (path === '/tickets') return <Tickets />;
  if (path === '/tickets/new') return <NewTicket />;
  if ((m = match('/tickets/:id', path))) return <TicketView id={Number(m.id)} />;
  if (path === '/exports') return <Exports />;
  if (path === '/import') return <Import />;
  if (path === '/admin') return <AdminTools />;
  return (
    <div className="page">
      <h1>This page does not exist</h1>
      <p><a href="#/">Back to the start page</a></p>
    </div>
  );
}

function MeRedirect() {
  const { me, navigate } = useApp();
  useEffect(() => { if (me) navigate(`/employees/${me.id}`); }, [me, navigate]);
  return <Loading />;
}

function App() {
  const [path, navigate, route] = useRoute();
  const state = useAppState(navigate);
  return (
    <AppContext.Provider value={state}>
      <Shell path={path}>
        {state.me ? <Page key={route + state.user} path={path} /> : <div className="page"><Loading /></div>}
      </Shell>
      <Chat />
      <Toasts items={state.toasts} />
    </AppContext.Provider>
  );
}

createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>);
