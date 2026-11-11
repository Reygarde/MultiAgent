import { useState } from 'react'
import { Check, Copy } from 'lucide-react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'

export function CodeBlock({ code, language = 'text' }: { code: string; language?: string }) {
  const [copied, setCopied] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function copy() {
    try {
      await navigator.clipboard.writeText(code)
      setCopied(true)
      setError(null)
    } catch {
      setError('Copie indisponible. Sélectionnez le code manuellement.')
    }
  }
  return <div className="code-block">
    <div className="code-toolbar"><span>{language}</span>
      <button type="button" onClick={() => void copy()} aria-label="Copier le code">
        {copied ? <Check size={14} /> : <Copy size={14} />}{copied ? 'Copié' : 'Copier'}
      </button>
    </div>
    {error && <p role="alert" className="error-inline">{error}</p>}
    <pre><code>{code}</code></pre>
  </div>
}

export function Markdown({ children }: { children: string }) {
  return <div className="markdown"><ReactMarkdown remarkPlugins={[remarkGfm]} components={{
    pre: ({ children: content }) => <>{content}</>,
    code: ({ className, children: content }) => {
      const code = String(content).replace(/\n$/, '')
      return className || code.includes('\n')
        ? <CodeBlock code={code} language={className?.replace('language-', '')} />
        : <code className="inline-code">{content}</code>
    },
    img: ({ alt }) => <span className="muted">[Image externe non chargée : {alt}]</span>,
    a: ({ href, children: content }) => <a href={href} target="_blank" rel="noopener noreferrer">{content}</a>,
  }}>{children}</ReactMarkdown></div>
}
