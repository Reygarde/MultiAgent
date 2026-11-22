import { readFileSync } from 'node:fs'
import path from 'node:path'
import { pathToFileURL } from 'node:url'
import ts from 'typescript'

// Project text never becomes a script, plugin, tsconfig or filesystem path on the host.
export function checkReactSources(files, { missionReactV2 = false } = {}) {
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
  function importedCall(expression, moduleName, member) {
    const namespace = ts.isPropertyAccessExpression(expression) && expression.name.text === member
    const binding = namespace ? expression.expression : expression
    if (!ts.isIdentifier(binding)) return false
    return (checker.getSymbolAtLocation(binding)?.declarations ?? []).some(declaration => {
      if (namespace ? !(ts.isNamespaceImport(declaration) || moduleName === 'react' && ts.isImportClause(declaration))
          : !ts.isImportSpecifier(declaration) || declaration.isTypeOnly || (declaration.propertyName ?? declaration.name).text !== member)
        return false
      let statement = declaration
      while (statement && !ts.isImportDeclaration(statement)) statement = statement.parent
      return statement && !statement.importClause?.isTypeOnly &&
        ts.isStringLiteral(statement.moduleSpecifier) && statement.moduleSpecifier.text === moduleName
    })
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
      if (missionReactV2 && root === '/src/App.tsx' &&
          /^(?:vitest(?:\/|$)|@vitest\/|@testing-library\/)/.test(name))
        add(file, at, 'REACT_APP_TEST_IMPORT', 'App.tsx is application code: Vitest and Testing Library imports belong only in tests.')
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
      if (missionReactV2 && root === '/src/App.tsx' && ts.isCallExpression(node)) {
        let callee = node.expression
        while (ts.isPropertyAccessExpression(callee)) callee = callee.expression
        if (ts.isIdentifier(callee)) {
          const declarations = checker.getSymbolAtLocation(callee)?.declarations ?? []
          const importedTest = declarations.some(d => ts.isImportSpecifier(d) &&
            d.parent.parent.parent.moduleSpecifier.text === 'vitest')
          if (importedTest || !declarations.length && /^(?:test|it|describe|suite|beforeEach|afterEach|beforeAll|afterAll)$/.test(callee.text))
            add(file, node.getStart(file), 'REACT_APP_TEST_DECLARATION', 'Embedded tests are forbidden in App.tsx; move them to App.test.tsx.')
        }
      }
      ts.forEachChild(node, visit)
    }
    visit(file)
    if (missionReactV2 && root === '/src/App.test.tsx') {
      if (contents.get('/src/App.tsx') === contents.get(root))
        add(file, 0, 'REACT_DUPLICATE_APPLICATION', 'App.test.tsx duplicates App.tsx instead of testing its imported component.')
      const imports = file.statements.filter(ts.isImportDeclaration)
      const appBindings = imports.filter(statement =>
        !statement.importClause?.isTypeOnly &&
        ts.isStringLiteral(statement.moduleSpecifier) &&
        ts.resolveModuleName(statement.moduleSpecifier.text, root, options, virtual).resolvedModule?.resolvedFileName === '/src/App.tsx')
        .flatMap(statement => [
          ...(statement.importClause?.name ? [statement.importClause.name] : []),
          ...(statement.importClause?.namedBindings && ts.isNamedImports(statement.importClause.namedBindings)
            ? statement.importClause.namedBindings.elements.filter(binding => !binding.isTypeOnly && binding.propertyName?.text === 'default').map(binding => binding.name) : []),
        ]).map(binding => checker.getSymbolAtLocation(binding)).filter(Boolean)
      if (!appBindings.length)
        add(file, 0, 'REACT_TEST_APP_IMPORT', "App.test.tsx must import the real default App from './App'; do not declare a local copy.")
      let targeted = false
      function inspectRender(node) {
        if (ts.isCallExpression(node)) {
          const render = importedCall(node.expression, '@testing-library/react', 'render')
          if (render) {
            let rendersApp = false
            function inspectTarget(child) {
              if ((ts.isJsxOpeningElement(child) || ts.isJsxSelfClosingElement(child)) &&
                  appBindings.includes(checker.getSymbolAtLocation(child.tagName)))
                rendersApp = true
              if (ts.isCallExpression(child) && importedCall(child.expression, 'react', 'createElement') && child.arguments[0] &&
                  appBindings.includes(checker.getSymbolAtLocation(child.arguments[0])))
                rendersApp = true
              ts.forEachChild(child, inspectTarget)
            }
            if (node.arguments[0]) inspectTarget(node.arguments[0])
            if (rendersApp) targeted = true
            else add(file, node.getStart(file), 'REACT_TEST_APP_TARGET', 'Tests must render the imported App, not a local component copy.')
          }
        }
        ts.forEachChild(node, inspectRender)
      }
      inspectRender(file)
      if (!targeted)
        add(file, 0, 'REACT_TEST_APP_TARGET', 'No render of the imported App was found in App.test.tsx.')
    }
  }
  return results
}

if (process.argv[2] === '--stdio' && process.argv[1] &&
    import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const input = JSON.parse(readFileSync(0, 'utf8'))
  process.stdout.write(JSON.stringify(Array.isArray(input) ? checkReactSources(input) :
    checkReactSources(input.files, { missionReactV2: input.templateId === 'react-typescript-vite-v2' })))
}
