import assert from 'node:assert/strict'
import test from 'node:test'
import { checkReactSources } from './react-source-check.mjs'

function check(sources) {
  return checkReactSources(Object.entries({
    'package.json': JSON.stringify({ dependencies: { react: '19' },
      devDependencies: { vitest: '4', '@testing-library/jest-dom': '6' } }),
    ...sources,
  }).map(([path, content]) => ({ path, content })))
}

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
