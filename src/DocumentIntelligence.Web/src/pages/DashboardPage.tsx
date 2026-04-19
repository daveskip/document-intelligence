import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { Upload, Eye, Trash2, ChevronLeft, ChevronRight, Loader2, AlertCircle, Inbox, FileText } from 'lucide-react'
import api from '../lib/api'
import { StatusBadge } from '../components/StatusBadge'
import { useDashboardSignalR } from '../hooks/useDocumentSignalR'
import type { DocumentDto, PagedResult } from '../types/api'

const PAGE_SIZE = 10

function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

export default function DashboardPage() {
  const [page, setPage] = useState(1)
  const queryClient = useQueryClient()

  useDashboardSignalR()

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.delete(`/documents/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['documents'] }),
  })

  const { data, isLoading, error } = useQuery({
    queryKey: ['documents', page],
    queryFn: async () => {
      const { data } = await api.get<PagedResult<DocumentDto>>(
        `/documents?page=${page}&pageSize=${PAGE_SIZE}`,
      )
      return data
    },
  })

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-xl font-semibold text-gray-900">Documents</h1>
        <Link
          to="/upload"
          className="flex items-center gap-2 bg-blue-600 hover:bg-blue-700 text-white text-sm font-medium rounded-md px-4 py-2"
        >
          <Upload className="h-4 w-4" />
          Upload document
        </Link>
      </div>

      {isLoading && (
        <div className="flex items-center justify-center gap-2 text-sm text-gray-500 py-8">
          <Loader2 className="h-4 w-4 animate-spin" />
          Loading…
        </div>
      )}

      {error && (
        <div className="flex items-center justify-center gap-2 text-sm text-red-600 py-8">
          <AlertCircle className="h-4 w-4" />
          Failed to load documents.
        </div>
      )}

      {data && data.items.length === 0 && (
        <div className="text-sm text-gray-500 py-12 text-center">
          <Inbox className="h-10 w-10 text-gray-300 mx-auto mb-2" />
          No documents yet.{' '}
          <Link to="/upload" className="text-blue-600 hover:underline">
            Upload one
          </Link>
          .
        </div>
      )}

      {data && data.items.length > 0 && (
        <>
          <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
            <table className="min-w-full divide-y divide-gray-200">
              <thead className="bg-gray-50">
                <tr>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">File</th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Size</th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Status</th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Uploaded</th>
                  <th className="px-6 py-3" />
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200">
                {data.items.map((doc) => (
                  <tr key={doc.id} className="hover:bg-gray-50">
                    <td className="px-6 py-4 text-sm text-gray-900 max-w-xs">
                      <span className="flex items-center gap-1.5 truncate">
                        <FileText className="h-4 w-4 text-gray-400 shrink-0" />
                        {doc.fileName}
                      </span>
                    </td>
                    <td className="px-6 py-4 text-sm text-gray-500">{formatBytes(doc.fileSize)}</td>
                    <td className="px-6 py-4">
                      <StatusBadge status={doc.statusLabel} />
                    </td>
                    <td className="px-6 py-4 text-sm text-gray-500">
                      {new Date(doc.uploadedAt).toLocaleString()}
                    </td>
                    <td className="px-6 py-4 text-right">
                      <div className="flex items-center justify-end gap-3">
                        <Link
                          to={`/documents/${doc.id}`}
                          className="flex items-center gap-1 text-sm text-blue-600 hover:underline"
                        >
                          <Eye className="h-4 w-4" />
                          View
                        </Link>
                        <button
                          onClick={() => {
                            if (confirm(`Delete "${doc.fileName}"?`)) {
                              deleteMutation.mutate(doc.id)
                            }
                          }}
                          disabled={deleteMutation.isPending}
                          className="flex items-center gap-1 text-sm text-red-600 hover:underline disabled:opacity-40"
                        >
                          <Trash2 className="h-4 w-4" />
                          Delete
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {(data.hasPreviousPage || data.hasNextPage) && (
            <div className="mt-4 flex items-center justify-between text-sm text-gray-500">
              <span>
                Page {data.page} of {data.totalPages} ({data.totalCount} total)
              </span>
              <div className="flex gap-2">
                <button
                  onClick={() => setPage((p) => p - 1)}
                  disabled={!data.hasPreviousPage}
                  className="flex items-center gap-1 border border-gray-300 rounded-md px-3 py-1 disabled:opacity-40"
                >
                  <ChevronLeft className="h-4 w-4" />
                  Previous
                </button>
                <button
                  onClick={() => setPage((p) => p + 1)}
                  disabled={!data.hasNextPage}
                  className="flex items-center gap-1 border border-gray-300 rounded-md px-3 py-1 disabled:opacity-40"
                >
                  Next
                  <ChevronRight className="h-4 w-4" />
                </button>
              </div>
            </div>
          )}
        </>
      )}
    </div>
  )
}
