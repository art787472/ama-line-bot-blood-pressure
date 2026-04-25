import { Card, Title, LineChart } from '@tremor/react';
import type { Record } from '../api';

export default function TrendChart({ records }: { records: Record[] }) {
  const data = [...records]
    .sort((a, b) => a.measuredAt.localeCompare(b.measuredAt))
    .map((r) => {
      const d = new Date(r.measuredAt);
      const date = `${d.getMonth() + 1}/${d.getDate()} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
      return {
        date,
        收縮壓: r.systolic,
        舒張壓: r.diastolic,
        脈搏: r.pulse ?? undefined,
      };
    });

  return (
    <Card>
      <Title>血壓趨勢</Title>
      <LineChart
        className="mt-4 h-72"
        data={data}
        index="date"
        categories={['收縮壓', '舒張壓', '脈搏']}
        colors={['red', 'blue', 'amber']}
        yAxisWidth={40}
      />
    </Card>
  );
}
