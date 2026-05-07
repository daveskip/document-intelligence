import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { ShieldCheck, User, Loader2, AlertCircle, ChevronLeft, ChevronRight, Users } from 'lucide-react'
import api from '../lib/api'
import { useAuth } from '../context/AuthContext'
import type { AdminUserDto, PagedResult } from '../types/api'

const PAGE_SIZE = 20

export default function UserManagementPage() {
  const [page, setPage] = useState(1)
  const queryClient = useQueryClient()
  const { user: currentUser } = useAuth()

  const { data, isLoading, error } = useQuery({
    queryKey: ['admin', 'users', page],
    queryFn: async () => {
      const { data } = await api.get<PagedResult<AdminUserDto>>(
        `/admin/users?page=${page}&pageSize=${PAGE_SIZE}`,
      )
      return data
    },
  })

  const roleMutation = useMutation({
    mutationFn: ({ userId, role }: { userId: string; role: string }) =>
      api.put(`/admin/users/${userId}/role`, { role }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'users'] }),
  })

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="flex items-center gap-2 text-xl font-semibold text-gray-900">
            <ShieldCheck className="h-5 w-5 text-blue-600" />
            User Management
          </h1>
          <p className="mt-1 text-sm text-gray-500">Manage user roles across the platform.</p>
        </div>
        {data && (
          <span className="flex items-center gap-1.5 text-sm text-gray-500">
            <Users className="h-4 w-4" />
            {data.totalCount} {data.totalCount === 1 ? 'user' : 'users'}
          </span>
        )}
      </div>

      {isLoading && (
        <div className="flex items-center justify-center gap-2 text-sm text-gray-500 py-12">
          <Loader2 className="h-4 w-4 animate-spin" />
          Loading users…
        </div>
      )}

      {error && (
        <div className="flex items-center justify-center gap-2 text-sm text-red-600 py-12">
          <AlertCircle className="h-4 w-4" />
          Failed to load users.
        </div>
      )}

      {data && data.items.length === 0 && (
        <div className="text-sm text-gray-500 py-12 text-center">No users found.</div>
      )}

      {data && data.items.length > 0 && (
        <>
          <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
            <table className="min-w-full divide-y divide-gray-200">
              <thead className="bg-gray-50">
                <tr>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">User</th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Member since</th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Role</th>
                  <th className="px-6 py-3" />
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200">
                {data.items.map((u) => {
                  const isCurrentUser = u.id === currentUser?.id
                  const isAdmin = u.roles.includes('Admin')
                  const isPending = roleMutation.isPending && roleMutation.variables?.userId === u.id

                  return (
                    <tr key={u.id} className="hover:bg-gray-50">
                      <td className="px-6 py-4">
                        <div className="text-sm font-medium text-gray-900">{u.displayName}</div>
                        <div className="text-xs text-gray-500">{u.email}</div>
                      </td>
                      <td className="px-6 py-4 text-sm text-gray-500">
                        {new Date(u.createdAt).toLocaleDateString()}
                      </td>
                      <td className="px-6 py-4">
                        {isAdmin ? (
                          <span className="inline-flex items-center gap-1 px-2.5 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                            <ShieldCheck className="h-3 w-3" />
                            Admin
                          </span>
                        ) : (
                          <span className="inline-flex items-center gap-1 px-2.5 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-700">
                            <User className="h-3 w-3" />
                            User
                          </span>
                        )}
                      </td>
                      <td className="px-6 py-4 text-right">
                        {isCurrentUser ? (
                          <span className="text-xs text-gray-400 italic">You</span>
                        ) : (
                          <button
                            onClick={() =>
                              roleMutation.mutate({ userId: u.id, role: isAdmin ? 'User' : 'Admin' })
                            }
                            disabled={isPending || roleMutation.isPending}
                            className={`flex items-center gap-1.5 text-sm rounded-md px-3 py-1 border disabled:opacity-40 ml-auto ${
                              isAdmin
                                ? 'border-gray-300 text-gray-700 hover:bg-gray-50'
                                : 'border-blue-200 text-blue-700 hover:bg-blue-50'
                            }`}
                          >
                            {isPending ? (
                              <Loader2 className="h-3.5 w-3.5 animate-spin" />
                            ) : isAdmin ? (
                              <User className="h-3.5 w-3.5" />
                            ) : (
                              <ShieldCheck className="h-3.5 w-3.5" />
                            )}
                            {isAdmin ? 'Remove Admin' : 'Make Admin'}
                          </button>
                        )}
                      </td>
                    </tr>
                  )
                })}
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
