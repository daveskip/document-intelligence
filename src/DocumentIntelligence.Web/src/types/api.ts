/**
 * API contract types.
 *
 * These types mirror the C# records in DocumentIntelligence.Contracts and are the
 * authoritative shape consumed by all API calls and SignalR notifications in this app.
 *
 * GENERATED WORKFLOW — replace manual definitions with schema-derived types:
 *   1. Start the API service:
 *        dotnet run --project src/DocumentIntelligence.ApiService
 *   2. Regenerate:
 *        npm run generate-api
 *   3. Replace the exports below with:
 *        import type { components } from './generated'
 *        export type DocumentDto          = components['schemas']['DocumentDto']
 *        export type DocumentDetailDto    = components['schemas']['DocumentDetailDto']
 *        export type ExtractionResultDto  = components['schemas']['ExtractionResultDto']
 *        export type PagedResult<T>       = ... (generic — map manually if needed)
 *        export type AuthResponse         = components['schemas']['AuthResponse']
 *        export type DocumentStatusNotification = components['schemas']['DocumentStatusNotification']
 *
 * src/types/generated.ts is git-ignored and must be regenerated after contract changes.
 */

export type DocumentStatus = 'Pending' | 'Processing' | 'Completed' | 'Failed'

export interface DocumentDto {
  id: string
  fileName: string
  contentType: string
  fileSize: number
  status: number
  statusLabel: DocumentStatus
  uploadedAt: string
  errorMessage?: string
}

export interface ExtractionResultDto {
  id: string
  extractedJson: string
  confidenceScore: number
  modelVersion: string
  processedAt: string
}

export interface DocumentDetailDto extends DocumentDto {
  extractionResult?: ExtractionResultDto
}

export interface PagedResult<T> {
  items: T[]
  totalCount: number
  page: number
  pageSize: number
  totalPages: number
  hasNextPage: boolean
  hasPreviousPage: boolean
}

export interface AuthResponse {
  accessToken: string
  refreshToken: string
  accessTokenExpiry: string
  user: { id: string; email: string; displayName: string }
}

export interface DocumentStatusNotification {
  documentId: string
  status: number
  statusLabel: DocumentStatus
  errorMessage?: string
  extractionSummary?: {
    confidenceScore: number
    modelVersion: string
    processedAt: string
  }
}
