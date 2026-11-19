import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { OllamaFallbackModels } from './OllamaFallbackModels'

describe('OllamaFallbackModels', () => {
  it('proposes only installed Developer defaults, excluding the primary model', () => {
    const changed = vi.fn()
    render(<OllamaFallbackModels label="Secours Developer" models={['qwen2.5-coder:7b', 'qwen3.5:9b', 'other:3b']}
      primary="qwen2.5-coder:7b" onChange={changed} />)
    fireEvent.click(screen.getByText('Proposer les secours Developer installés'))
    expect(changed).toHaveBeenCalledWith(['qwen3.5:9b'])
  })

  it('preserves order and lets users reorder or remove an unavailable fallback', () => {
    const changed = vi.fn()
    render(<OllamaFallbackModels label="Secours Developer" models={['qwen3.5:9b']} primary="qwen2.5-coder:7b"
      value={['missing:14b', 'qwen3.5:9b']} onChange={changed} />)
    expect(screen.getByText(/indisponible, ignoré/)).toBeVisible()
    fireEvent.click(screen.getByLabelText('Monter qwen3.5:9b'))
    expect(changed).toHaveBeenLastCalledWith(['qwen3.5:9b', 'missing:14b'])
    fireEvent.click(screen.getByLabelText('Retirer missing:14b'))
    expect(changed).toHaveBeenLastCalledWith(['qwen3.5:9b'])
  })
})
