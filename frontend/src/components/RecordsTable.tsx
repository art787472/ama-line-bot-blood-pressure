import {
  Card, Title, Table, TableHead, TableHeaderCell, TableBody, TableRow, TableCell, Badge,
} from '@tremor/react';
import type { Record } from '../api';

function level(systolic: number, diastolic: number) {
  if (systolic >= 140 || diastolic >= 90) return { label: '偏高', color: 'red' as const };
  if (systolic >= 130 || diastolic >= 85) return { label: '正常高值', color: 'amber' as const };
  return { label: '正常', color: 'emerald' as const };
}

export default function RecordsTable({ records }: { records: Record[] }) {
  return (
    <Card>
      <Title>紀錄列表</Title>
      <Table className="mt-4">
        <TableHead>
          <TableRow>
            <TableHeaderCell>時間</TableHeaderCell>
            <TableHeaderCell>收縮壓</TableHeaderCell>
            <TableHeaderCell>舒張壓</TableHeaderCell>
            <TableHeaderCell>脈搏</TableHeaderCell>
            <TableHeaderCell>狀態</TableHeaderCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {records.map((r) => {
            const l = level(r.systolic, r.diastolic);
            return (
              <TableRow key={r.id}>
                <TableCell>
                  {new Date(r.measuredAt).toLocaleString('zh-TW', { hour12: false })}
                </TableCell>
                <TableCell>{r.systolic}</TableCell>
                <TableCell>{r.diastolic}</TableCell>
                <TableCell>{r.pulse ?? '—'}</TableCell>
                <TableCell>
                  <Badge color={l.color}>{l.label}</Badge>
                </TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </Card>
  );
}
