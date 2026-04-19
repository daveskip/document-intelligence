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

    let stopped = false

    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/documents', {
        accessTokenFactory: () => sessionStorage.getItem('accessToken') ?? '',
      })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.None)
      .build()

    connection.on('DocumentStatusChanged', handleStatusChanged)

    connection
      .start()
      .then(() => {
        if (!stopped) return connection.invoke('JoinDocumentGroup', documentId)
      })
      .catch((err) => {
        if (!stopped) console.error('SignalR connection error:', err)
      })

    connectionRef.current = connection

    return () => {
      stopped = true
      if (connection.state === signalR.HubConnectionState.Connected) {
        connection.invoke('LeaveDocumentGroup', documentId).catch(() => {}).finally(() => connection.stop())
      } else {
        connection.stop()
      }
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
    let stopped = false

    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/documents', {
        accessTokenFactory: () => sessionStorage.getItem('accessToken') ?? '',
      })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.None)
      .build()

    connection.on('DocumentStatusChanged', handleStatusChanged)
    connection.start().catch((err) => {
      if (!stopped) console.error('SignalR error:', err)
    })

    return () => {
      stopped = true
      connection.stop()
    }
  }, [handleStatusChanged])
}
