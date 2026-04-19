import { useEffect, useRef, useCallback } from 'react'
import * as signalR from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import type { DocumentStatusNotification } from '../types/api'

export function useDocumentSignalR(documentId: string | null) {
  const queryClient = useQueryClient()
  const connectionRef = useRef<signalR.HubConnection | null>(null)

  const handleStatusChanged = useCallback(
    (notification: DocumentStatusNotification) => {
      // Update the document in list cache
      queryClient.invalidateQueries({ queryKey: ['documents'] })
      // Update the individual document cache
      queryClient.invalidateQueries({ queryKey: ['document', notification.documentId] })
    },
    [queryClient],
  )

  useEffect(() => {
    if (!documentId) return

    const token = sessionStorage.getItem('accessToken')

    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/documents', {
        accessTokenFactory: () => token ?? '',
      })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build()

    connection.on('DocumentStatusChanged', handleStatusChanged)

    connection
      .start()
      .then(() => connection.invoke('JoinDocumentGroup', documentId))
      .catch((err) => console.error('SignalR connection error:', err))

    connectionRef.current = connection

    return () => {
      connection
        .invoke('LeaveDocumentGroup', documentId)
        .catch(() => {})
        .finally(() => connection.stop())
    }
  }, [documentId, handleStatusChanged])
}

export function useDashboardSignalR() {
  const queryClient = useQueryClient()

  const handleStatusChanged = useCallback(
    (_notification: DocumentStatusNotification) => {
      queryClient.invalidateQueries({ queryKey: ['documents'] })
    },
    [queryClient],
  )

  useEffect(() => {
    const token = sessionStorage.getItem('accessToken')

    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/documents', {
        accessTokenFactory: () => token ?? '',
      })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build()

    connection.on('DocumentStatusChanged', handleStatusChanged)
    connection.start().catch((err) => console.error('SignalR error:', err))

    return () => {
      connection.stop()
    }
  }, [handleStatusChanged])
}
