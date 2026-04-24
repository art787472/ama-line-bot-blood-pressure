const BASE = import.meta.env.VITE_API_BASE_URL || 'http://localhost:8080';
const USER_ID = import.meta.env.VITE_USER_ID || '';

export type Record = {
  id: string;
  userId: string;
  systolic: number;
  diastolic: number;
  pulse: number | null;
  measuredAt: string;
  createdAt: string;
};

export type Stats = {
  count: number;
  avgSystolic: number | null;
  avgDiastolic: number | null;
  avgPulse: number | null;
  maxSystolic: number | null;
  minSystolic: number | null;
};

function qs() {
  return USER_ID ? `?userId=${encodeURIComponent(USER_ID)}` : '';
}

export async function fetchRecords(): Promise<Record[]> {
  const r = await fetch(`${BASE}/api/records${qs()}`);
  if (!r.ok) throw new Error('Failed to fetch records');
  return r.json();
}

export async function fetchStats(days = 30): Promise<Stats> {
  const sep = qs() ? '&' : '?';
  const r = await fetch(`${BASE}/api/stats${qs()}${sep}days=${days}`);
  if (!r.ok) throw new Error('Failed to fetch stats');
  return r.json();
}
