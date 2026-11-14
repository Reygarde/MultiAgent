import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { expect, it, vi } from 'vitest'
import { CodeBlock, Markdown } from './Markdown'

it('renders code as text without executing embedded HTML or loading remote images', () => {
  const { container } = render(<Markdown>{'```csharp\nreturn 42;\n```\n<script>alert(1)</script>\n\n![secret](https://example.invalid/track)'}</Markdown>)
  expect(container.querySelector('code')).toHaveTextContent('return 42;')
  expect(container.querySelector('script')).toBeNull()
  expect(container.querySelector('img')).toBeNull()
  expect(screen.getByRole('button', { name: 'Copier le code' })).toBeVisible()
})

it('highlights source files without interpreting their content as HTML', () => {
  const source = 'public class Demo { string Value = "<script>alert(1)</script>"; }'
  const { container } = render(<CodeBlock code={source} language="src/App/Demo.cs" />)
  expect(container.querySelector('code')).toHaveTextContent(source)
  expect(container.querySelector('.hljs-keyword')).not.toBeNull()
  expect(container.querySelector('script')).toBeNull()
})

it('shows unknown file types as plain text', () => {
  const { container } = render(<CodeBlock code="hello world" language="file.unknown" />)
  expect(container.querySelector('code')).toHaveTextContent('hello world')
})

it('copies a code block with the clipboard API', async () => {
  const user = userEvent.setup()
  const write = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue()
  render(<CodeBlock code="return 42;" language="csharp" />)
  await user.click(screen.getByRole('button', { name: 'Copier le code' }))
  expect(write).toHaveBeenCalledWith('return 42;')
  expect(screen.getByText('Copié')).toBeVisible()
})
