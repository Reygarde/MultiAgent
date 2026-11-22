import { readFileSync } from 'node:fs'
import path from 'node:path'
import { pathToFileURL } from 'node:url'
import { createHash } from 'node:crypto'
import ts from 'typescript'
import { checkReactSources } from './react-source-check.mjs'

const hash = value => createHash('sha256').update(value).digest('hex').toUpperCase()
const signature = issue => JSON.stringify([issue.file, issue.code, issue.message])
function walk(node, visit) {
  visit(node)
  ts.forEachChild(node, child => walk(child, visit))
}

export function repairReactSources({ files, allowedPaths, trustedSetup, templateId }) {
  const allowed = new Set(allowedPaths)
  for (const name of allowed) {
    if (!name.startsWith('src/') || !/\.tsx?$/.test(name) || name.includes('\\') ||
        name.split('/').some(part => !part || part === '..' || part === '.') ||
        ['src/main.tsx', 'src/technical.test.tsx'].includes(name)) throw Error('Unauthorized repair path')
  }
  const contents = new Map(files.map(file => ['/' + file.path, file.content]))
  const options = { noLib: true, noEmit: true, types: [], module: ts.ModuleKind.ESNext,
    moduleResolution: ts.ModuleResolutionKind.Bundler, target: ts.ScriptTarget.ES2022,
    jsx: ts.JsxEmit.Preserve, verbatimModuleSyntax: true }
  const host = { fileExists: file => contents.has(file), readFile: file => contents.get(file),
    getCurrentDirectory: () => '/', getCanonicalFileName: file => file, useCaseSensitiveFileNames: () => true,
    getDefaultLibFileName: () => '/absent.d.ts', getNewLine: () => '\n',
    writeFile: () => { throw Error('Emit is disabled') },
    getSourceFile: (file, version) => contents.has(file) ? ts.createSourceFile(file, contents.get(file), version, true) : undefined }
  const program = ts.createProgram([...contents.keys()].filter(file => /\.tsx?$/.test(file)), options, host)
  const checker = program.getTypeChecker()
  const checkOptions = { missionReactV2: templateId === 'react-typescript-vite-v2' }
  const before = checkReactSources(files, checkOptions), edits = [], refusals = []
  function typeOnly(file, specifier) {
    const symbol = checker.getSymbolAtLocation(specifier.name)
    if (!symbol) return false
    let safe = true
    walk(file, node => {
      if (!ts.isIdentifier(node) || node === specifier.name || checker.getSymbolAtLocation(node) !== symbol) return
      for (let parent = node.parent; parent; parent = parent.parent) {
        if (ts.isImportSpecifier(parent)) return
        if (ts.isTypeQueryNode(parent) || ts.isHeritageClause(parent) && parent.token === ts.SyntaxKind.ExtendsKeyword) {
          safe = false; return
        }
        if (ts.isTypeNode(parent) && !ts.isExpressionWithTypeArguments(parent)) return
      }
      safe = false
    })
    return safe
  }
  const candidate = files.map(source => {
    if (!allowed.has(source.path)) return { ...source }
    const issues = before.filter(issue => issue.file === source.path)
    if (!issues.length) return { ...source }
    if (issues.some(issue => /^(?:REACT_APP_TEST_|REACT_TEST_APP_|REACT_DUPLICATE_APPLICATION)/.test(issue.code))) {
      refusals.push({ file: source.path, reason: 'Application/test role violations require a Developer correction; no AST transformation applied.' })
      return { ...source }
    }
    const file = program.getSourceFile('/' + source.path)
    if (program.getSyntacticDiagnostics(file).length) {
      refusals.push({ file: source.path, reason: 'Syntax recovery is ambiguous; no AST transformation applied.' })
      return { ...source }
    }
    const replacements = new Map()
    function replace(node, text, code) {
      const start = node.getStart(file), end = node.end
      replacements.set(`${start}:${end}`, { start, end, before: source.content.slice(start, end), after: text, code })
    }
    function inlineType(specifier, code = 'TS1484') {
      if (specifier.isTypeOnly) return
      const start = specifier.getStart(file)
      replacements.set(`${start}:${start}`, { start, end: start, before: '', after: 'type ', code })
    }
    const matches = (node, code) => issues.some(issue => issue.code === code &&
      file.getPositionOfLineAndCharacter(issue.line - 1, issue.column - 1) >= node.getStart(file) &&
      file.getPositionOfLineAndCharacter(issue.line - 1, issue.column - 1) < node.end)
    const imports = file.statements.filter(ts.isImportDeclaration)
    for (const statement of imports) {
      const specifier = statement.moduleSpecifier, bindings = statement.importClause?.namedBindings
      if (bindings && ts.isNamedImports(bindings))
        for (const binding of bindings.elements)
          if (matches(binding, 'TS1484') && typeOnly(file, binding)) inlineType(binding)
      if (matches(specifier, 'REACT_JEST_DOM') && !statement.importClause && trustedSetup &&
          files.find(item => item.path === 'test-setup.ts')?.content === trustedSetup) {
        const setup = ts.createSourceFile('test-setup.ts', trustedSetup, ts.ScriptTarget.ES2022, true)
        if (setup.statements.some(node => ts.isImportDeclaration(node) && !node.importClause &&
            node.moduleSpecifier.text === '@testing-library/jest-dom/vitest'))
          replace(statement, '', 'REACT_JEST_DOM')
      }
      if (matches(specifier, 'REACT_LOCAL_IMPORT') && !statement.importClause?.name &&
          bindings && ts.isNamedImports(bindings) && bindings.elements.length &&
          bindings.elements.every(binding => typeOnly(file, binding))) {
        const targets = files.filter(target => target.path.startsWith('src/') &&
          path.posix.basename(target.path, path.posix.extname(target.path)) === path.posix.basename(specifier.text))
        if (targets.length !== 1) continue
        const target = program.getSourceFile('/' + targets[0].path)
        if (!target || !target.statements.every(node => ts.isInterfaceDeclaration(node) || ts.isTypeAliasDeclaration(node))) continue
        const module = checker.getSymbolAtLocation(target)
        if (!module) continue
        const exports = checker.getExportsOfModule(module)
        if (!bindings.elements.every(binding => exports.some(symbol =>
          symbol.name === (binding.propertyName ?? binding.name).text && !(symbol.flags & ts.SymbolFlags.Value)))) continue
        let relative = path.posix.relative(path.posix.dirname(source.path), targets[0].path).replace(/\.tsx?$/, '')
        if (!relative.startsWith('.')) relative = './' + relative
        replace(specifier, JSON.stringify(relative), 'REACT_LOCAL_IMPORT')
        for (const binding of bindings.elements) inlineType(binding, 'REACT_LOCAL_IMPORT')
      }
    }
    const jest = []
    let safeJest = true
    walk(file, node => {
      if (!ts.isIdentifier(node) || node.text !== 'jest' || !matches(node, 'REACT_JEST')) return
      const access = node.parent, call = access.parent
      if (checker.getSymbolAtLocation(node)?.declarations?.length || !ts.isPropertyAccessExpression(access) ||
          access.expression !== node || access.name.text !== 'fn' || !ts.isCallExpression(call) ||
          call.expression !== access || call.arguments.length || call.typeArguments?.length) { safeJest = false; return }
      const vi = checker.resolveName('vi', node, ts.SymbolFlags.Value, false)
      if (vi && !vi.declarations?.every(declaration => ts.isImportSpecifier(declaration) &&
          !declaration.isTypeOnly && !declaration.parent.parent.isTypeOnly && !declaration.propertyName &&
          declaration.name.text === 'vi' && declaration.parent.parent.parent.moduleSpecifier.text === 'vitest')) safeJest = false
      jest.push(node)
    })
    if (safeJest && jest.length && !source.content.startsWith('#!')) {
      for (const node of jest) replace(node, 'vi', 'REACT_JEST')
      const imported = imports.some(statement => statement.moduleSpecifier.text === 'vitest' &&
        !statement.importClause?.isTypeOnly && statement.importClause?.namedBindings &&
        ts.isNamedImports(statement.importClause.namedBindings) &&
        statement.importClause.namedBindings.elements.some(binding => binding.name.text === 'vi' &&
          !binding.propertyName && !binding.isTypeOnly))
      if (!imported) replacements.set('0:0:vi', { start: 0, end: 0, before: '',
        after: 'import { vi } from "vitest";\n', code: 'REACT_JEST' })
    }
    let content = source.content, boundary = content.length + 1
    for (const edit of [...replacements.values()].sort((a, b) => b.start - a.start || b.end - a.end)) {
      if (edit.end > boundary || source.content.slice(edit.start, edit.end) !== edit.before) throw Error('Overlapping or stale AST edit')
      content = content.slice(0, edit.start) + edit.after + content.slice(edit.end)
      boundary = edit.start
      edits.push({ file: source.path, ...edit, originalSha256: hash(source.content), candidateSha256: hash(content) })
    }
    for (const edit of edits.filter(edit => edit.file === source.path)) edit.candidateSha256 = hash(content)
    return { ...source, content }
  })
  const issues = checkReactSources(candidate, checkOptions), known = new Set(before.map(signature))
  if (issues.some(issue => !known.has(signature(issue)))) throw Error('AST edits introduced a new diagnostic; candidate rejected')
  return { files: candidate, before, issues, edits, refusals }
}

if (process.argv[2] === '--stdio' && process.argv[1] &&
    import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href)
  process.stdout.write(JSON.stringify(repairReactSources(JSON.parse(readFileSync(0, 'utf8')))))
