import { useParams, Link, useNavigate } from 'react-router-dom'
import { useEffect, useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Trash2, Loader2, AlertCircle, ChevronDown, ScanText } from 'lucide-react'
import api from '../lib/api'
import { StatusBadge } from '../components/StatusBadge'
import { ExtractionTable } from '../components/ExtractionTable'
import { useDocumentSignalR } from '../hooks/useDocumentSignalR'
import type { DocumentDetailDto } from '../types/api'

function useAuthenticatedFileUrl(documentId: string | undefined, contentType: string | undefined, enabled: boolean) {
  const [blobUrl, setBlobUrl] = useState<string | null>(null)

  useEffect(() => {
    if (!documentId || !contentType || !enabled) return
    let revoked = false
    let url: string | null = null

    api.get(`/documents/${documentId}/file`, { responseType: 'blob' })
      .then((res) => {
        if (revoked) return
        url = URL.createObjectURL(new Blob([res.data], { type: contentType }))
        setBlobUrl(url)
      })
      .catch(() => { if (!revoked) setBlobUrl(null) })

    return () => {
      revoked = true
      if (url) {
        URL.revokeObjectURL(url)
        setBlobUrl(null)
      }
    }
  }, [documentId, contentType, enabled])

  return blobUrl
}

function ExtractedFields({ json }: { json: string }) {
  try {
    const data = JSON.parse(json)
    const metadata = data._metadata
    const fields = Object.entries(data).filter(([k]) => k !== '_metadata')

    const isTable = ([, v]: [string, unknown]) =>
      Array.isArray(v) && v.length > 0 && typeof v[0] === 'object' && v[0] !== null

    const scalarFields = fields.filter(([k, v]) => k !== 'extracted_data' && !isTable([k, v]))
    const tableFields  = fields.filter(([k, v]) => k !== 'extracted_data' && isTable([k, v]))
    const extractedData = fields.find(([k]) => k === 'extracted_data')

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

        {/* Key value fields */}
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 mb-4">
          {scalarFields.map(([key, value]) => (
            <div key={key} className="border border-gray-200 rounded-lg p-3">
              <p className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-1">
                {key.replace(/([A-Z])/g, ' $1').trim()}
              </p>
              {value === null || value === undefined
                ? <p className="text-sm text-gray-400 italic">—</p>
                : typeof value === 'boolean'
                  ? <p className="text-sm text-gray-900">{value ? 'Yes' : 'No'}</p>
                  : typeof value === 'object'
                    ? <pre className="text-xs text-gray-700 bg-gray-50 rounded p-2 overflow-auto whitespace-pre-wrap">{JSON.stringify(value, null, 2)}</pre>
                    : <p className="text-sm text-gray-900">{String(value)}</p>
              }
            </div>
          ))}
        </div>

        {/* Tables */}
        {tableFields.map(([key, value]) => (
          <div key={key} className="mb-4">
            <ExtractionTable fieldName={key} rows={value as Record<string, unknown>[]} />
          </div>
        ))}

        {/* extracted_data */}
        {extractedData && (() => {
          const [key, value] = extractedData
          return (
            <div className="border border-gray-200 rounded-lg p-3">
              <p className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-1">
                {key.replace(/([A-Z])/g, ' $1').trim()}
              </p>
              {value === null || value === undefined
                ? <p className="text-sm text-gray-400 italic">—</p>
                : typeof value === 'object'
                  ? <pre className="text-xs text-gray-700 bg-gray-50 rounded p-2 overflow-auto whitespace-pre-wrap">{JSON.stringify(value, null, 2)}</pre>
                  : <p className="text-sm text-gray-900">{String(value)}</p>
              }
            </div>
          )
        })()}
      </div>
    )
  } catch {
    return <pre className="text-xs text-gray-700 bg-gray-50 p-4 rounded-lg overflow-auto">{json}</pre>
  }
}

export default function DocumentDetailPage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  useDocumentSignalR(id ?? null)

  const deleteMutation = useMutation({
    mutationFn: () => api.delete(`/documents/${id}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['documents'] })
      navigate('/dashboard')
    },
  })

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

  const [docExpanded, setDocExpanded] = useState(false)
  const fileUrl = useAuthenticatedFileUrl(data ? id : undefined, data?.contentType, docExpanded)

  if (isLoading) return <div className="text-sm text-gray-500 py-8 text-center">Loading…</div>
  if (error || !data) return <div className="text-sm text-red-600 py-8 text-center">Document not found.</div>

  const isPdf = data.contentType === 'application/pdf'
  const isImage = data.contentType.startsWith('image/')

  return (
    <div className="max-w-4xl">
      <div className="flex items-center gap-2 mb-1">
        <Link to="/dashboard" className="flex items-center gap-1 text-sm text-blue-600 hover:underline">
          <ArrowLeft className="h-4 w-4" />
          Documents
        </Link>
      </div>

      <div className="flex items-start justify-between mb-6">
        <div>
          <h1 className="text-xl font-semibold text-gray-900">{data.fileName}</h1>
          <p className="text-sm text-gray-500 mt-1">
            Uploaded {new Date(data.uploadedAt).toLocaleString()}
          </p>
        </div>
        <div className="flex items-center gap-3">
          <StatusBadge status={data.statusLabel} />
          <button
            onClick={() => {
              if (confirm(`Delete "${data.fileName}"?`)) {
                deleteMutation.mutate()
              }
            }}
            disabled={deleteMutation.isPending}
            className="flex items-center gap-1.5 text-sm text-red-600 border border-red-200 rounded-md px-3 py-1 hover:bg-red-50 disabled:opacity-40"
          >
            <Trash2 className="h-4 w-4" />
            {deleteMutation.isPending ? 'Deleting…' : 'Delete'}
          </button>
        </div>
      </div>

      {data.statusLabel === 'Failed' && data.errorMessage && (
        <div className="mb-6 rounded-md bg-red-50 border border-red-200 text-red-700 px-4 py-3 text-sm flex items-center gap-2">
          <AlertCircle className="h-4 w-4 shrink-0" />
          <span><strong>Processing error:</strong> {data.errorMessage}</span>
        </div>
      )}

      {(data.statusLabel === 'Pending' || data.statusLabel === 'Processing') && (
        <div className="mb-6 rounded-md bg-blue-50 border border-blue-200 text-blue-700 px-4 py-3 text-sm flex items-center gap-2">
          <Loader2 className="animate-spin h-4 w-4" />
          Document is being processed by Gemma 4…
        </div>
      )}

      {data.extractionResult && (
        <div>
          <div className="flex items-center justify-between mb-3">
            <h2 className="flex items-center gap-2 text-base font-semibold text-gray-900">
              <ScanText className="h-4 w-4 text-gray-500" />
              Extracted fields
            </h2>
            <div className="text-xs text-gray-400">
              Model: {data.extractionResult.modelVersion} ·{' '}
              Confidence: {(data.extractionResult.confidenceScore * 100).toFixed(0)}%
            </div>
          </div>
          <ExtractedFields json={data.extractionResult.extractedJson} />
        </div>
      )}

      <div className="mt-8 border border-gray-200 rounded-xl overflow-hidden">
        <button
          onClick={() => setDocExpanded((v) => !v)}
          className="w-full flex items-center justify-between px-4 py-3 bg-gray-50 hover:bg-gray-100 text-left"
        >
          <h2 className="text-base font-semibold text-gray-900">Document</h2>
          <ChevronDown
            className={`h-4 w-4 text-gray-500 transition-transform ${docExpanded ? 'rotate-180' : ''}`}
          />
        </button>
        {docExpanded && (
          <div className="p-4">
            {!fileUrl && (
              <div className="text-sm text-gray-400 py-4 text-center">Loading document…</div>
            )}
            {fileUrl && isPdf && (
              <iframe
                src={fileUrl}
                title={data.fileName}
                className="w-full rounded-lg border border-gray-200"
                style={{ height: '80vh' }}
              />
            )}
            {fileUrl && isImage && (
              <img
                src={fileUrl}
                alt={data.fileName}
                className="max-w-full rounded-lg border border-gray-200"
              />
            )}
          </div>
        )}
      </div>
    </div>
  )
}
