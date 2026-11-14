import { useState } from 'react'
import { Check, Copy } from 'lucide-react'
import { createLowlight } from 'lowlight'
import csharp from 'highlight.js/lib/languages/csharp'
import css from 'highlight.js/lib/languages/css'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import markdown from 'highlight.js/lib/languages/markdown'
import plaintext from 'highlight.js/lib/languages/plaintext'
import typescript from 'highlight.js/lib/languages/typescript'
import xml from 'highlight.js/lib/languages/xml'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'

const highlighter = createLowlight({ csharp, css, javascript, json, markdown, plaintext, typescript, xml })
type HighlightNode = ReturnType<typeof highlighter.highlight>['children'][number]

function renderToken(node: HighlightNode, index: number): React.ReactNode {
  if (node.type === 'text') return node.value
  if (node.type === 'element') {
    const classes = node.properties.className
    return <span key={index} className={Array.isArray(classes) ? classes.join(' ') : String(classes ?? '')}>
      {node.children.map(renderToken)}
    </span>
  }
  throw new Error(`Type de nœud de coloration inattendu : ${node.type}`)
}

function highlightLanguage(language: string) {
  const value = language.toLowerCase()
  const extension = value.split('.').pop() ?? value
  const aliases: Record<string, string> = {
    cs: 'csharp', 'c#': 'csharp', csproj: 'xml', slnx: 'xml', html: 'xml',
    js: 'javascript', jsx: 'javascript', ts: 'typescript', tsx: 'typescript',
    md: 'markdown', text: 'plaintext',
  }
  const name = aliases[extension] ?? extension
  return highlighter.registered(name) ? name : 'plaintext'
}

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
    <pre><code>{highlighter.highlight(highlightLanguage(language), code).children.map(renderToken)}</code></pre>
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
