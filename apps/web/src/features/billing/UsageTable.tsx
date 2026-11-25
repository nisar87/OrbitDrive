import { formatSize, type UsageDay } from '../../billing';

/** Daily counts for the last 30 days: a number of files and a size per day. There are no names or times of individual transfers anywhere in this data. */
export function UsageTable({ history, caption }: { history: UsageDay[]; caption: string }) {
  const shown = history.filter((d) => d.files > 0 || d.bytes > 0);
  const total = history.reduce((sum, d) => ({ files: sum.files + d.files, bytes: sum.bytes + d.bytes }), { files: 0, bytes: 0 });
  return (
    <>
      <p className="hint">{caption}: {total.files} file{total.files === 1 ? '' : 's'}, {formatSize(total.bytes)} in the last {history.length} days.</p>
      {shown.length === 0 ? <p className="empty">No transfers in this period.</p> : (
        <table>
          <caption className="sr-only">{caption}</caption>
          <thead><tr><th scope="col">Day</th><th scope="col">Files</th><th scope="col">Size</th></tr></thead>
          <tbody>{shown.map((d) => <tr key={d.date}><th scope="row">{d.date}</th><td>{d.files}</td><td>{formatSize(d.bytes)}</td></tr>)}</tbody>
        </table>
      )}
    </>
  );
}
