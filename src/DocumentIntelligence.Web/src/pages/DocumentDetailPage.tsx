import { useParams, Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import api from '../lib/api'
import { StatusBadge } from '../components/StatusBadge'
import { useDocumentSignalR } from '../hooks/useDocumentSignalR'
import type { DocumentDetailDto } from '../types/api'

function ExtractedFields({ json }: { json: string }) {
  try {
    const data = JSON.parse(json)
    const metadata = data._metadata
    const fields = Object.entries(data).filter(([k]) => k !== '_metadata')

    return (
      <div>
        {metadata && (
          <div className="mb-4 p-3 bg-blue-50 rounded-lg border border-blue-100 text-sm">
            <p><span className="font-medium">Document type:</span> {metadata.documentType ?? 'Unknown'}</p>
            <p><span className="font-medium">Pages analyzed:</span> {metadata.pageCount ?? 'N/A'}</p>
            {metadata.extractionNotes && (
              <p><span className="font-medium">Notes:</span> {metadata.extractionNotes}</p>
            )}
          </div>
        )}
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          {fields.map(([key, value]) => (
            <div key={key} className="border border-gray-200 rounded-lg p-3">
              <p className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-1">
                {key.replace(/([A-Z])/g, ' $1').trim()}
              </p>
              <p className="text-sm text-gray-900">
                {value === null || value === undefined
                  ? <span className="text-gray-400 italic">—</span>
                  : typeof value === 'boolean'
                    ? (value ? 'Yes' : 'No')
                    : String(value)}
              </p>
            </div>
          ))}
        </div>
      </div>
    )
  } catch {
    return <pre className="text-xs text-gray-700 bg-gray-50 p-4 rounded-lg overflow-auto">{json}</pre>
  }
}

export default function DocumentDetailPage() {
  const { id } = useParams<{ id: string }>()

  useDocumentSignalR(id ?? null)

  const { data, isLoading, error } = useQuery({
    queryKey: ['document', id],
    queryFn: async () => {
      const { data } = await api.get<DocumentDetailDto>(`/documents/${id}`)
      return data
    },
    enabled: !!id,
    refetchInterval: (query) => {
      const status = query.state.data?.statusLabel
      return status === 'Pending' || status === 'Processing' ? 5000 : false
    },
  })

  if (isLoading) return <div className="text-sm text-gray-500 py-8 text-center">Loading…</div>
  if (error || !data) return <div className="text-sm text-red-600 py-8 text-center">Document not found.</div>

  return (
    <div className="max-w-3xl">
      <div className="flex items-center gap-2 mb-1">
        <Link to="/dashboard" className="text-sm text-blue-600 hover:underline">← Documents</Link>
      </div>

      <div className="flex items-start justify-between mb-6">
        <div>
          <h1 className="text-xl font-semibold text-gray-900">{data.fileName}</h1>
          <p className="text-sm text-gray-500 mt-1">
            Uploaded {new Date(data.uploadedAt).toLocaleString()}
          </p>
        </div>
        <StatusBadge status={data.statusLabel} />
      </div>

      {data.statusLabel === 'Failed' && data.errorMessage && (
        <div className="mb-6 rounded-md bg-red-50 border border-red-200 text-red-700 px-4 py-3 text-sm">
          <strong>Processing error:</strong> {data.errorMessage}
        </div>
      )}

      {(data.statusLabel === 'Pending' || data.statusLabel === 'Processing') && (
        <div className="mb-6 rounded-md bg-blue-50 border border-blue-200 text-blue-700 px-4 py-3 text-sm flex items-center gap-2">
          <svg className="animate-spin h-4 w-4" viewBox="0 0 24 24" fill="none">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8H4z" />
          </svg>
          Document is being processed by Gemma 4…
        </div>
      )}

      {data.extractionResult && (
        <div>
          <div className="flex items-center justify-between mb-3">
            <h2 className="text-base font-semibold text-gray-900">Extracted fields</h2>
            <div className="text-xs text-gray-400">
              Model: {data.extractionResult.modelVersion} ·{' '}
              Confidence: {(data.extractionResult.confidenceScore * 100).toFixed(0)}%
            </div>
          </div>
          <ExtractedFields json={data.extractionResult.extractedJson} />
        </div>
      )}
    </div>
  )
}
