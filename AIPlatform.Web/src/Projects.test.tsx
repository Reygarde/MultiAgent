import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Projects } from './Projects'
import { api } from './api'
import { mission } from './test/fixtures'

vi.mock('./api', () => ({
  api: {
    files: vi.fn(), file: vi.fn(), renameProject: vi.fn(), deleteProject: vi.fn(),
    exportProject: vi.fn(), analysis: vi.fn(),
  },
  errorMessage: (error: unknown) => error instanceof Error ? error.message : 'Error',
}))

const project = {
  ...mission, hasWorkspace: true, status: 'Verified' as const, projectName: 'Projet personnalisé',
  projectDescription: 'Un outil de suivi.', createdAt: '2026-11-10T09:00:00Z',
  updatedAt: '2026-11-10T09:30:00Z', verification: [
    { stage: 'Build', status: 'Succeeded', exitCode: 0, log: '', totalTests: null },
  ],
}
const older = {
  ...project, id: '22222222-2222-2222-2222-222222222222', projectName: null,
  task: 'Créer une API de bibliothèque', createdAt: '2026-11-09T09:00:00Z',
  updatedAt: '2026-11-09T09:00:00Z', status: 'GeneratedUnverified' as const,
  verification: [],
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.files).mockResolvedValue([])
  vi.mocked(api.file).mockResolvedValue({ path: '', content: '' })
})

describe('Projects', () => {
  it('keeps custom project names, sorts by creation date and searches descriptive metadata', async () => {
    render(<Projects missions={[older, project]} selectedId={null} onSelect={vi.fn()} refresh={vi.fn()} />)
    const titles = [...document.querySelectorAll('.project-entry strong')].map(node => node.textContent)
    expect(titles).toEqual(['Projet personnalisé', 'Créer une API de bibliothèque'])
    expect(screen.getAllByText('Compilation et tests réussis')).toHaveLength(2)
    await userEvent.type(screen.getByRole('searchbox', { name: 'Rechercher un projet' }), 'bibliothèque')
    expect(screen.getAllByText('Créer une API de bibliothèque')).toHaveLength(2)
    expect(screen.queryByText('Projet personnalisé')).not.toBeInTheDocument()
  })

  it('opens the associated mission without exposing its identifier in the main view', async () => {
    const onOpenMission = vi.fn()
    render(<Projects missions={[project]} selectedId={project.id} onSelect={vi.fn()}
      onOpenMission={onOpenMission} refresh={vi.fn()} />)
    expect(screen.getByRole('heading', { name: 'Projet personnalisé' })).toBeVisible()
    expect(screen.getByText(project.id)).not.toBeVisible()
    await userEvent.click(screen.getByRole('button', { name: 'Ouvrir la mission associée' }))
    expect(onOpenMission).toHaveBeenCalledWith(project.id)
  })
})
