import { Card, Metric, Text, Grid } from '@tremor/react';
import type { Stats } from '../api';

const fmt = (n: number | null, digits = 0) =>
  n === null ? '—' : n.toFixed(digits);

export default function StatsCards({ stats }: { stats: Stats }) {
  return (
    <Grid numItemsSm={2} numItemsLg={4} className="gap-4">
      <Card>
        <Text>記錄筆數</Text>
        <Metric>{stats.count}</Metric>
      </Card>
      <Card>
        <Text>平均收縮壓</Text>
        <Metric>{fmt(stats.avgSystolic)}</Metric>
      </Card>
      <Card>
        <Text>平均舒張壓</Text>
        <Metric>{fmt(stats.avgDiastolic)}</Metric>
      </Card>
      <Card>
        <Text>平均脈搏</Text>
        <Metric>{fmt(stats.avgPulse)}</Metric>
      </Card>
    </Grid>
  );
}
