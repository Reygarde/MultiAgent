import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { expect, it, vi } from 'vitest'
import { CodeBlock, Markdown } from './Markdown'

it('renders code as text without executing embedded HTML or loading remote images', () => {
  const { container } = render(<Markdown>{'```csharp\nreturn 42;\n```\n<script>alert(1)</script>\n\n![secret](https://example.invalid/track)'}</Markdown>)
  expect(screen.getByText('return 42;')).toBeVisible()
  expect(container.querySelector('script')).toBeNull()
  expect(container.querySelector('img')).toBeNull()
  expect(screen.getByRole('button', { name: 'Copier le code' })).toBeVisible()
})

it('copies a code block with the clipboard API', async () => {
  const user = userEvent.setup()
  const write = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue()
  render(<CodeBlock code="return 42;" language="csharp" />)
  await user.click(screen.getByRole('button', { name: 'Copier le code' }))
  expect(write).toHaveBeenCalledWith('return 42;')
  expect(screen.getByText('Copié')).toBeVisible()
})
