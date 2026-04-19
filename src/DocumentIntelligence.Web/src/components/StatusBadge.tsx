import type { DocumentStatus } from '../types/api'

const statusConfig: Record<DocumentStatus, { label: string; classes: string }> = {
  Pending: { label: 'Pending', classes: 'bg-yellow-100 text-yellow-800' },
  Processing: { label: 'Processing', classes: 'bg-blue-100 text-blue-800 animate-pulse' },
  Completed: { label: 'Completed', classes: 'bg-green-100 text-green-800' },
  Failed: { label: 'Failed', classes: 'bg-red-100 text-red-800' },
}

export function StatusBadge({ status }: { status: DocumentStatus }) {
  const config = statusConfig[status] ?? statusConfig.Pending
  return (
    <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${config.classes}`}>
      {config.label}
    </span>
  )
}
