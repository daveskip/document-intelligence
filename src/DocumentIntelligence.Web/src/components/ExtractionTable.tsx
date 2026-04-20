import { useState } from 'react'
import { ChevronUp, ChevronDown } from 'lucide-react'

interface ExtractionTableProps {
  fieldName: string
  rows: Record<string, unknown>[]
}

// Keys whose values should be formatted as USD currency.
const CURRENCY_KEYS = new Set([
  'proceeds', 'costorotherbasis', 'gainlossamount', 'wages', 'federaltaxwithheld',
  'statetaxwithheld', 'socialsecuritytax', 'medicaretax', 'totalincome',
  'deductions', 'credits', 'refunds', 'amount', 'cost', 'basis', 'gain', 'loss',
  'price', 'value', 'total', 'subtotal', 'tax',
])

function isCurrencyKey(key: string): boolean {
  const normalized = key.toLowerCase().replace(/[^a-z]/g, '')
  return CURRENCY_KEYS.has(normalized) ||
    [...CURRENCY_KEYS].some(k => normalized.includes(k))
}

function formatCell(key: string, value: unknown): { display: string; numeric: number | null; isNegative: boolean } {
  if (value === null || value === undefined) return { display: '—', numeric: null, isNegative: false }

  if (typeof value === 'number') {
    const isNeg = value < 0
    if (isCurrencyKey(key)) {
      const display = new Intl.NumberFormat('en-US', {
        style: 'currency', currency: 'USD', minimumFractionDigits: 2,
      }).format(value)
      return { display, numeric: value, isNegative: isNeg }
    }
    // Large unit counts — compact above 1M, otherwise locale-formatted
    const display = Math.abs(value) >= 1_000_000
      ? new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 4 }).format(value)
      : new Intl.NumberFormat('en-US', { maximumFractionDigits: 8 }).format(value)
    return { display, numeric: value, isNegative: isNeg }
  }

  if (typeof value === 'object') {
    return { display: JSON.stringify(value), numeric: null, isNegative: false }
  }

  return { display: String(value), numeric: null, isNegative: false }
}

function headerLabel(key: string): string {
  return key
    .replace(/([A-Z])/g, ' $1')
    .replace(/^./, c => c.toUpperCase())
    .trim()
}

type SortDir = 'asc' | 'desc' | null

export function ExtractionTable({ fieldName, rows }: ExtractionTableProps) {
  const [sortKey, setSortKey] = useState<string | null>(null)
  const [sortDir, setSortDir] = useState<SortDir>(null)

  if (rows.length === 0) return null

  // Derive ordered column list: union of all keys across all rows.
  const columns = [...new Set(rows.flatMap(r => Object.keys(r)))]

  // Numeric totals for footer.
  const totals: Record<string, number | null> = {}
  for (const col of columns) {
    const values = rows.map(r => {
      const v = r[col]
      return typeof v === 'number' ? v : null
    })
    if (values.every(v => v !== null)) {
      totals[col] = (values as number[]).reduce((a, b) => a + b, 0)
    } else {
      totals[col] = null
    }
  }

  const handleSort = (col: string) => {
    if (sortKey === col) {
      setSortDir(d => d === 'asc' ? 'desc' : d === 'desc' ? null : 'asc')
      if (sortDir === 'desc') setSortKey(null)
    } else {
      setSortKey(col)
      setSortDir('asc')
    }
  }

  const sorted = [...rows].sort((a, b) => {
    if (!sortKey || !sortDir) return 0
    const av = a[sortKey]
    const bv = b[sortKey]
    if (typeof av === 'number' && typeof bv === 'number') {
      return sortDir === 'asc' ? av - bv : bv - av
    }
    return sortDir === 'asc'
      ? String(av ?? '').localeCompare(String(bv ?? ''))
      : String(bv ?? '').localeCompare(String(av ?? ''))
  })

  const label = fieldName.replace(/([A-Z])/g, ' $1').replace(/^./, c => c.toUpperCase()).trim()

  return (
    <div className="col-span-full">
      <p className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-2">{label}</p>
      <div className="overflow-x-auto rounded-lg border border-gray-200">
        <table className="min-w-full text-sm">
          <thead className="bg-gray-50 border-b border-gray-200">
            <tr>
              {columns.map(col => (
                <th
                  key={col}
                  onClick={() => handleSort(col)}
                  className="px-3 py-2 text-left text-xs font-semibold text-gray-600 uppercase tracking-wide cursor-pointer select-none whitespace-nowrap hover:bg-gray-100"
                >
                  <span className="flex items-center gap-1">
                    {headerLabel(col)}
                    {sortKey === col && sortDir === 'asc' && <ChevronUp className="h-3 w-3" />}
                    {sortKey === col && sortDir === 'desc' && <ChevronDown className="h-3 w-3" />}
                  </span>
                </th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100 bg-white">
            {sorted.map((row, i) => (
              <tr key={i} className="hover:bg-gray-50">
                {columns.map(col => {
                  const { display, isNegative } = formatCell(col, row[col])
                  const isGainLoss = col.toLowerCase().includes('gain') || col.toLowerCase().includes('loss')
                  const textColor = isGainLoss
                    ? isNegative ? 'text-red-600 font-medium' : 'text-green-600 font-medium'
                    : 'text-gray-900'
                  return (
                    <td key={col} className={`px-3 py-2 whitespace-nowrap ${textColor}`}>
                      {display}
                    </td>
                  )
                })}
              </tr>
            ))}
          </tbody>
          {/* Totals footer — only shown if all rows have a numeric value for that column */}
          <tfoot className="bg-gray-50 border-t border-gray-200 font-semibold">
            <tr>
              {columns.map((col, idx) => {
                const total = totals[col]
                if (total === null) {
                  return <td key={col} className="px-3 py-2 text-xs text-gray-400">{idx === 0 ? 'Totals' : ''}</td>
                }
                const { display, isNegative } = formatCell(col, total)
                const isGainLoss = col.toLowerCase().includes('gain') || col.toLowerCase().includes('loss')
                const textColor = isGainLoss
                  ? isNegative ? 'text-red-600' : 'text-green-600'
                  : 'text-gray-900'
                return (
                  <td key={col} className={`px-3 py-2 text-sm whitespace-nowrap ${textColor}`}>
                    {display}
                  </td>
                )
              })}
            </tr>
          </tfoot>
        </table>
      </div>
    </div>
  )
}
