import React, { createContext, useContext, useState, useCallback } from 'react'
import type { AuthResponse } from '../types/api'

interface AuthUser {
  id: string
  email: string
  displayName: string
}

interface AuthContextValue {
  user: AuthUser | null
  isAuthenticated: boolean
  login: (response: AuthResponse) => void
  logout: () => void
}

const AuthContext = createContext<AuthContextValue | null>(null)

function loadUser(): AuthUser | null {
  try {
    const raw = sessionStorage.getItem('user')
    return raw ? JSON.parse(raw) : null
  } catch {
    return null
  }
}

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(loadUser)

  const login = useCallback((response: AuthResponse) => {
    sessionStorage.setItem('accessToken', response.accessToken)
    sessionStorage.setItem('user', JSON.stringify(response.user))
    localStorage.setItem('refreshToken', response.refreshToken)
    setUser(response.user)
  }, [])

  const logout = useCallback(() => {
    sessionStorage.removeItem('accessToken')
    sessionStorage.removeItem('user')
    localStorage.removeItem('refreshToken')
    setUser(null)
  }, [])

  return (
    <AuthContext.Provider value={{ user, isAuthenticated: !!user, login, logout }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used within AuthProvider')
  return ctx
}
