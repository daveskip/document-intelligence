import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
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

  useDashboardSignalR()

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
          className="bg-blue-600 hover:bg-blue-700 text-white text-sm font-medium rounded-md px-4 py-2"
        >
          Upload document
        </Link>
      </div>

      {isLoading && (
        <div className="text-sm text-gray-500 py-8 text-center">Loading…</div>
      )}

      {error && (
        <div className="text-sm text-red-600 py-8 text-center">Failed to load documents.</div>
      )}

      {data && data.items.length === 0 && (
        <div className="text-sm text-gray-500 py-12 text-center">
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
                    <td className="px-6 py-4 text-sm text-gray-900 max-w-xs truncate">{doc.fileName}</td>
                    <td className="px-6 py-4 text-sm text-gray-500">{formatBytes(doc.fileSize)}</td>
                    <td className="px-6 py-4">
                      <StatusBadge status={doc.statusLabel} />
                    </td>
                    <td className="px-6 py-4 text-sm text-gray-500">
                      {new Date(doc.uploadedAt).toLocaleString()}
                    </td>
                    <td className="px-6 py-4 text-right">
                      <Link
                        to={`/documents/${doc.id}`}
                        className="text-sm text-blue-600 hover:underline"
                      >
                        View
                      </Link>
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
                  className="border border-gray-300 rounded-md px-3 py-1 disabled:opacity-40"
                >
                  Previous
                </button>
                <button
                  onClick={() => setPage((p) => p + 1)}
                  disabled={!data.hasNextPage}
                  className="border border-gray-300 rounded-md px-3 py-1 disabled:opacity-40"
                >
                  Next
                </button>
              </div>
            </div>
          )}
        </>
      )}
    </div>
  )
}
