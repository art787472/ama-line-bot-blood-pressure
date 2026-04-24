import { useQuery } from '@tanstack/react-query';
import { fetchRecords, fetchStats } from './api';
import StatsCards from './components/StatsCards';
import TrendChart from './components/TrendChart';
import RecordsTable from './components/RecordsTable';

export default function App() {
  const recordsQ = useQuery({ queryKey: ['records'], queryFn: fetchRecords });
  const statsQ = useQuery({ queryKey: ['stats', 30], queryFn: () => fetchStats(30) });

  return (
    <main className="mx-auto max-w-5xl p-6 space-y-6">
      <header className="flex items-center justify-between">
        <h1 className="text-2xl font-semibold text-slate-800">血壓記錄</h1>
        <span className="text-sm text-slate-500">最近 30 天</span>
      </header>

      {statsQ.data && <StatsCards stats={statsQ.data} />}

      {recordsQ.isLoading && <p className="text-slate-500">載入中…</p>}
      {recordsQ.error && <p className="text-red-500">讀取失敗</p>}

      {recordsQ.data && recordsQ.data.length > 0 && (
        <>
          <TrendChart records={recordsQ.data} />
          <RecordsTable records={recordsQ.data} />
        </>
      )}

      {recordsQ.data && recordsQ.data.length === 0 && (
        <div className="rounded-lg border border-dashed border-slate-300 bg-white p-8 text-center text-slate-500">
          還沒有任何紀錄。請在 LINE 傳送血壓計照片給 bot。
        </div>
      )}
    </main>
  );
}
