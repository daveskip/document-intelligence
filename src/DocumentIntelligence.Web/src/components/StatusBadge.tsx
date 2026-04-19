import type { LucideIcon } from 'lucide-react'
import { Clock, Loader2, CheckCircle2, XCircle } from 'lucide-react'
import type { DocumentStatus } from '../types/api'

const statusConfig: Record<DocumentStatus, { label: string; classes: string; Icon: LucideIcon; spin?: boolean }> = {
  Pending:    { label: 'Pending',    classes: 'bg-yellow-100 text-yellow-800', Icon: Clock },
  Processing: { label: 'Processing', classes: 'bg-blue-100 text-blue-800',     Icon: Loader2, spin: true },
  Completed:  { label: 'Completed',  classes: 'bg-green-100 text-green-800',   Icon: CheckCircle2 },
  Failed:     { label: 'Failed',     classes: 'bg-red-100 text-red-800',       Icon: XCircle },
}

export function StatusBadge({ status }: { status: DocumentStatus }) {
  const config = statusConfig[status] ?? statusConfig.Pending
  const { Icon } = config
  return (
    <span className={`inline-flex items-center gap-1 px-2.5 py-0.5 rounded-full text-xs font-medium ${config.classes}`}>
      <Icon className={`h-3 w-3 ${config.spin ? 'animate-spin' : ''}`} />
      {config.label}
    </span>
  )
}
