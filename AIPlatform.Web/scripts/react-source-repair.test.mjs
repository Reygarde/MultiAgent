import test from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { checkReactSources } from './react-source-check.mjs'
import { repairReactSources } from './react-source-repair.mjs'

const setup = "import '@testing-library/jest-dom/vitest';"
const files = sources => [
  { path: 'package.json', content: JSON.stringify({ dependencies: { react: '19.3.1' },
    devDependencies: { vitest: '3', '@testing-library/jest-dom': '6' } }) },
  { path: 'test-setup.ts', content: setup },
  ...Object.entries(sources).map(([path, content]) => ({ path: 'src/' + path, content })),
]
const repair = (sources, allowedPaths = sources.filter(file => file.path.startsWith('src/')).map(file => file.path)) =>
  repairReactSources({ files: sources, allowedPaths, trustedSetup: setup })

test('stdio repair invokes only its own entrypoint, not the imported checker CLI', () => {
  const input = files({ 'View.test.tsx': 'const handler = jest.fn();' })
  const child = spawnSync(process.execPath, [fileURLToPath(new URL('./react-source-repair.mjs', import.meta.url)), '--stdio'],
    { encoding: 'utf8', input: JSON.stringify({ files: input, allowedPaths: ['src/View.test.tsx'], trustedSetup: setup }) })
  assert.equal(child.status, 0, child.stderr)
  assert.equal(JSON.parse(child.stdout).issues.length, 0)
})

test('diagnostic-bound edits are scoped, traced, idempotent and preserve module effects', () => {
  const original = files({
    'Entity.ts': 'export interface Entity { id: string }',
    'View.tsx': "import { Entity } from './Entity'; export const value: Entity = { id: 'a' };",
    'View.test.tsx': "import '@testing-library/jest-dom/extend-expect'; import { Entity } from '../Entity'; const a: Entity = {id:'x'}; const fn = jest.fn();",
  })
  const before = structuredClone(original)
  const result = repair(original)
  assert.equal(result.issues.length, 0)
  assert.equal(result.before.length, 4)
  assert.match(result.files.find(file => file.path === 'src/View.tsx').content, /import \{ type Entity \}/)
  assert.match(result.files.find(file => file.path === 'src/View.test.tsx').content, /vi.fn\(\)/)
  assert.ok(result.edits.every(edit => /^[A-F0-9]{64}$/.test(edit.originalSha256) &&
    edit.before === original.find(file => file.path === edit.file).content.slice(edit.start, edit.end)))
  assert.ok(result.edits.every(edit => result.before.some(issue => issue.file === edit.file && issue.code === edit.code)))
  assert.deepEqual(original, before)
  assert.equal(repair(result.files).edits.length, 0)
  assert.deepEqual(repair(original, ['src/View.tsx']).files.find(file => file.path === 'src/View.test.tsx'), original.at(-1))
})

test('syntax recovery and ambiguous or effectful module resolution are refused', () => {
  for (const extra of [
    { 'nested/Entity.ts': 'export interface Entity { id: string }' },
    { 'Entity.ts': "console.log('effect'); export interface Entity { id: string }" },
  ]) {
    const input = files({ 'Entity.ts': 'export interface Entity { id: string }', ...extra,
      'View.tsx': "import { Entity } from '../Entity'; export const value: Entity = {id:'a'};" })
    assert.ok(repair(input).issues.some(issue => issue.code === 'REACT_LOCAL_IMPORT'))
  }
  const result = repair(files({ 'View.tsx': "import { X } from '../X'; const value = fn(key: value);" }))
  assert.equal(result.edits.length, 0)
  assert.equal(result.refusals.length, 1)
  assert.ok(result.issues.some(issue => issue.code === 'TS1005'))
})

test('Jest conversion rejects arguments, other APIs, shadowing and vi collisions', () => {
  for (const content of [
    'jest.fn(() => 1)', 'jest.fn<string>()', 'jest.spyOn(obj,"method"); jest.fn()',
    'const vi = {}; jest.fn()', 'function f(vi: object) { return jest.fn() }',
  ]) {
    const input = files({ 'View.test.tsx': content })
    assert.equal(repair(input).files.at(-1).content, content)
  }
})

test('obsolete jest-dom import requires exact trusted Vitest setup and no bindings', () => {
  const input = files({ 'View.test.tsx': "import '@testing-library/jest-dom/extend-expect';" })
  input[1].content = 'export {}'
  assert.equal(repair(input).edits.length, 0)
  const binding = files({ 'View.test.tsx': "import value from '@testing-library/jest-dom/extend-expect';" })
  assert.equal(repair(binding).edits.length, 0)
})

test('type-only repairs never remove value imports or change value queries', () => {
  const input = files({
    'Entity.ts': 'export interface Entity { id: string }; export const make = () => 1;',
    'View.tsx': "import { Entity, make } from './Entity'; const x: Entity = {id:'a'}; make();",
  })
  const result = repair(input)
  assert.match(result.files.at(-1).content, /import \{ type Entity, make \}/)
  assert.equal(checkReactSources(result.files).length, 0)
  assert.throws(() => repair(input, ['src/main.tsx']), /Unauthorized/)
  assert.throws(() => repair(input, ['src/../View.tsx']), /Unauthorized/)
})
