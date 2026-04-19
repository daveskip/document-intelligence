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
