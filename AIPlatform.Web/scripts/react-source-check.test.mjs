import assert from 'node:assert/strict'
import test from 'node:test'
import { checkReactSources } from './react-source-check.mjs'

function check(sources, options) {
  return checkReactSources(Object.entries({
    'package.json': JSON.stringify({ dependencies: { react: '19' },
      devDependencies: { vitest: '4', '@testing-library/jest-dom': '6', '@testing-library/react': '16' } }),
    ...sources,
  }).map(([path, content]) => ({ path, content })), options)
}

const app = 'export default function App() { return <div>0</div> }'
const appTest = "import App from './App'; import { render } from '@testing-library/react'; import { test } from 'vitest'; test('zero', () => render(<App />));"
const v2 = sources => check(sources, { missionReactV2: true })

test('React v2 accepts tests of the real imported component, including import aliases', () => {
  assert.deepEqual(v2({ 'src/App.tsx': app, 'src/App.test.tsx': appTest }), [])
  assert.deepEqual(v2({ 'src/App.tsx': app, 'src/App.test.tsx':
    "import Counter from './App'; import { render as mount } from '@testing-library/react'; mount(<Counter />);" }), [])
  assert.deepEqual(v2({ 'src/App.tsx': app, 'src/App.test.tsx':
    "import Counter from './App'; import * as rtl from '@testing-library/react'; import React from 'react'; rtl.render(React.createElement(Counter));" }), [])
  assert.deepEqual(v2({ 'src/App.tsx': app, 'src/App.test.tsx':
    "import Counter from './App'; import { render } from '@testing-library/react'; import { createElement as element } from 'react'; render(element(Counter));" }), [])
})

test('React v2 rejects production test imports, embedded tests and duplicated application contents', () => {
  const content = "import { test } from 'vitest'; import { render } from '@testing-library/react'; " +
    app + " test('zero', () => render(<App />));"
  const issues = v2({ 'src/App.tsx': content, 'src/App.test.tsx': content })
  for (const code of ['REACT_APP_TEST_IMPORT', 'REACT_APP_TEST_DECLARATION', 'REACT_DUPLICATE_APPLICATION', 'REACT_TEST_APP_IMPORT', 'REACT_TEST_APP_TARGET'])
    assert.ok(issues.some(issue => issue.code === code), JSON.stringify(issues))
  assert.ok(v2({ 'src/App.tsx': app + "; test.only('embedded', () => {});" })
    .some(issue => issue.code === 'REACT_APP_TEST_DECLARATION'))
})

test('React v2 rejects unused App imports, local copies and type-only component imports', () => {
  for (const content of [
    "import { render } from '@testing-library/react'; const App = () => <div>0</div>; render(<App />);",
    "import RealApp from './App'; import { render } from '@testing-library/react'; const App = () => <div>0</div>; render(<App />);",
    "import type App from './App'; import { render } from '@testing-library/react'; render(<App />);",
    "import App from './App'; const text = 'render(<App />)';",
    "import App from './App'; import { render } from '@testing-library/react'; const fake = { createElement: () => null }; render(fake.createElement(App));",
    "import App from './App'; import { render } from '@testing-library/react'; const Copy = () => <div>0</div>; render(<Copy />, { unused: <App /> });",
  ]) assert.ok(v2({ 'src/App.tsx': app, 'src/App.test.tsx': content })
    .some(issue => issue.code === 'REACT_TEST_APP_TARGET'), content)
})

test('reports independent syntax, local export, dependency and Vitest errors with locations', () => {
  const issues = check({
    'src/Item.tsx': 'export default function Item() { return null }',
    'src/App.tsx': "import { Item } from './Item'\nimport './missing.css'\nimport 'not-in-project'\nexport default Item",
    'src/App.test.tsx': "import '@testing-library/jest-dom/extend-expect'\njest.fn()\nconst broken = 'abc",
  })
  for (const [file, line, code] of [
    ['src/App.tsx', 1, 'TS2614'],
    ['src/App.tsx', 2, 'REACT_LOCAL_IMPORT'],
    ['src/App.tsx', 3, 'REACT_DEPENDENCY'],
    ['src/App.test.tsx', 1, 'REACT_JEST_DOM'],
    ['src/App.test.tsx', 2, 'REACT_JEST'],
    ['src/App.test.tsx', 3, 'TS1002'],
  ]) assert.ok(issues.some(issue => issue.file === file && issue.line === line &&
    issue.column > 0 && issue.code === code), JSON.stringify(issues))
})

test('reads valid source without running it or mistaking comments and local names for Jest', () => {
  assert.deepEqual(check({
    'src/Item.tsx': 'export type Props = { title: string }; export default function Item() { return null }',
    'src/App.tsx': "import Item, { type Props } from './Item'\nimport './styles.css'\nexport default Item",
    'src/styles.css': '',
    'src/App.test.tsx': `import { vi } from 'vitest'
import '@testing-library/jest-dom/vitest'
// import 'undeclared-package'
const text = 'jest.fn()'
const jest = { fn: vi.fn }; jest.fn()
throw new Error('source was executed')`,
  }), [])
})
