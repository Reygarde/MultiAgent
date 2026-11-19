import { readFileSync } from 'node:fs'
import path from 'node:path'
import { pathToFileURL } from 'node:url'
import ts from 'typescript'

// Project text never becomes a script, plugin, tsconfig or filesystem path on the host.
export function checkReactSources(files) {
  const contents = new Map(files.map(file => [`/${file.path}`, file.content]))
  const manifest = JSON.parse(contents.get('/package.json'))
  const dependencies = new Set([...Object.keys(manifest.dependencies ?? {}), ...Object.keys(manifest.devDependencies ?? {})])
  const allowedSubpaths = new Set([
    'react-dom/client', 'react-dom/server', 'react/jsx-runtime', 'react/jsx-dev-runtime',
    'vitest/config', '@testing-library/jest-dom/vitest', '@testing-library/jest-dom/matchers',
  ])
  const options = { noLib: true, noResolve: false, noEmit: true, types: [], module: ts.ModuleKind.ESNext,
    moduleResolution: ts.ModuleResolutionKind.Bundler, target: ts.ScriptTarget.ES2022,
    jsx: ts.JsxEmit.Preserve, allowJs: true, checkJs: true, verbatimModuleSyntax: true }
  const virtual = {
    fileExists: file => contents.has(file),
    readFile: file => contents.get(file),
    getCurrentDirectory: () => '/',
  }
  const host = {
    ...virtual, getCanonicalFileName: file => file, useCaseSensitiveFileNames: () => true,
    getDefaultLibFileName: () => '/unused.d.ts', getNewLine: () => '\n', writeFile: () => { throw Error('Emit is disabled') },
    getSourceFile: (file, languageVersion) => contents.has(file)
      ? ts.createSourceFile(file, contents.get(file), languageVersion, true) : undefined,
  }
  const roots = [...contents.keys()].filter(file => file.startsWith('/src/') && /\.(?:tsx?|jsx?)$/.test(file))
  const program = ts.createProgram(roots, options, host)
  const checker = program.getTypeChecker()
  const results = []
  function add(file, start, code, message) {
    const { line, character } = file.getLineAndCharacterOfPosition(start ?? 0)
    if (results.length < 60) results.push({ file: file.fileName.slice(1), line: line + 1, column: character + 1, code, message })
  }
  for (const root of roots) {
    const file = program.getSourceFile(root)
    const syntax = program.getSyntacticDiagnostics(file)
    const semantic = program.getSemanticDiagnostics(file).filter(d =>
      [1192, 1484, 2305, 2306, 2459, 2613, 2614, 2724].includes(d.code))
    for (const d of [...syntax, ...semantic])
      add(file, d.start, `TS${d.code}`, ts.flattenDiagnosticMessageText(d.messageText, '\n'))
    function inspect(specifier) {
      if (!specifier || !ts.isStringLiteralLike(specifier)) return
      const name = specifier.text
      const at = specifier.getStart(file)
      if (name === '@testing-library/jest-dom/extend-expect')
        add(file, at, 'REACT_JEST_DOM', 'Use the scaffold test setup or @testing-library/jest-dom/vitest, not extend-expect.')
      else if (['jest', '@jest/globals'].includes(name))
        add(file, at, 'REACT_JEST', 'Use imports from vitest and vi instead of Jest.')
      else if (name.startsWith('./') || name.startsWith('../')) {
        const asset = path.posix.resolve(path.posix.dirname(root), name)
        if (!(asset.endsWith('.css') && contents.has(asset)) &&
            !ts.resolveModuleName(name, root, options, virtual).resolvedModule)
          add(file, at, 'REACT_LOCAL_IMPORT', `Local import ${JSON.stringify(name)} does not resolve in the candidate.`)
      } else {
        const dependency = name.split('/').slice(0, name.startsWith('@') ? 2 : 1).join('/')
        if (!dependencies.has(dependency) || (name !== dependency && !allowedSubpaths.has(name)))
          add(file, at, 'REACT_DEPENDENCY', `Import ${JSON.stringify(name)} is not available in the locked React scaffold.`)
      }
    }
    function visit(node) {
      if (ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) inspect(node.moduleSpecifier)
      if (ts.isExternalModuleReference(node)) inspect(node.expression)
      if (ts.isImportTypeNode(node) && ts.isLiteralTypeNode(node.argument)) inspect(node.argument.literal)
      if (ts.isCallExpression(node) && (node.expression.kind === ts.SyntaxKind.ImportKeyword ||
          (ts.isIdentifier(node.expression) && node.expression.text === 'require'))) inspect(node.arguments[0])
      if (ts.isIdentifier(node) && node.text === 'jest' && !checker.getSymbolAtLocation(node)?.declarations?.length &&
          ((ts.isPropertyAccessExpression(node.parent) || ts.isElementAccessExpression(node.parent)) && node.parent.expression === node ||
           ts.isQualifiedName(node.parent) && node.parent.left === node))
        add(file, node.getStart(file), 'REACT_JEST', 'Jest globals are unavailable; use vi from vitest.')
      ts.forEachChild(node, visit)
    }
    visit(file)
  }
  return results
}

if (process.argv[2] === '--stdio' && process.argv[1] &&
    import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  process.stdout.write(JSON.stringify(checkReactSources(JSON.parse(readFileSync(0, 'utf8')))))
}
