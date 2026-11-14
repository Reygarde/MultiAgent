using System.Text;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public static class ReactTemplate
{
    public const string Id = "react-typescript-vite";
    public const int Version = 1;
    public const string Image = "aiplatform-react-sandbox:1.0.0";
    public const int MaxLockfileBytes = 262144;
    public static bool IsEditable(string path) => path is
        "src/tasks.ts" or "src/components/TaskItem.tsx" or "src/App.tsx";
    public static bool IsTest(string path) => path.EndsWith(".test.ts", StringComparison.Ordinal) ||
        path.EndsWith(".test.tsx", StringComparison.Ordinal);
    public static bool IsPath(string path) => Files.Any(file => file.Path == path);
    public static bool IsTrustedLockfile(string path, string content) =>
        path == "package-lock.json" && content == Files.Single(file => file.Path == path).Content;

    private static string Resource(string name)
    {
        using var stream = typeof(ReactTemplate).Assembly.GetManifestResourceStream("React." + name)
            ?? throw new InvalidOperationException("Missing trusted React resource: " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<GeneratedFile> Files { get; } =
    [
        new("package.json", Resource("package.json")),
        new("package-lock.json", Resource("package-lock.json")),
        new("index.html", """
            <!doctype html>
            <html lang="fr"><head><meta charset="UTF-8" /><meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <title>Task Studio</title></head><body><div id="root"></div><script type="module" src="/src/main.tsx"></script></body></html>
            """),
        new("tsconfig.json", """
            {
              "compilerOptions": {
                "target": "ES2022", "lib": ["ES2022", "DOM", "DOM.Iterable"],
                "module": "ESNext", "moduleResolution": "bundler", "jsx": "react-jsx",
                "strict": true, "noEmit": true, "skipLibCheck": true,
                "esModuleInterop": true, "resolveJsonModule": true,
                "types": ["vitest/globals", "@testing-library/jest-dom", "node"]
              },
              "include": ["src", "vite.config.ts", "test-setup.ts"]
            }
            """),
        new("vite.config.ts", """
            import { defineConfig } from 'vitest/config'
            import react from '@vitejs/plugin-react'
            export default defineConfig({
              plugins: [react()],
              test: { environment: 'jsdom', globals: true, setupFiles: ['./test-setup.ts'],
                include: ['src/**/*.test.ts', 'src/**/*.test.tsx'], maxWorkers: 1, fileParallelism: false }
            })
            """),
        new("eslint.config.js", """
            import js from '@eslint/js'
            import parser from '@typescript-eslint/parser'
            export default [
              { ignores: ['dist/**', 'node_modules/**'] },
              { files: ['src/**/*.{ts,tsx}'], languageOptions: { parser,
                parserOptions: { ecmaVersion: 'latest', sourceType: 'module', ecmaFeatures: { jsx: true } } },
                rules: { ...js.configs.recommended.rules, 'no-undef': 'off', 'no-unused-vars': 'off' } }
            ]
            """),
        new("test-setup.ts", """
            import '@testing-library/jest-dom/vitest'
            import { cleanup } from '@testing-library/react'
            import { afterEach } from 'vitest'
            afterEach(cleanup)
            """),
        new("src/main.tsx", """
            import { StrictMode } from 'react'
            import { createRoot } from 'react-dom/client'
            import App from './App'
            import './styles/app.css'
            createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>)
            """),
        new("src/tasks.ts", """
            export interface Task { id: string; title: string; completed: boolean }
            export type Filter = 'all' | 'active' | 'completed'
            export function visibleTasks(tasks: Task[], filter: Filter): Task[] {
              return tasks.filter(task => filter === 'all' || (filter === 'completed' ? task.completed : !task.completed))
            }
            export function addTask(tasks: Task[], _title: string): Task[] { return tasks }
            export function deleteTask(tasks: Task[], _id: string): Task[] { return tasks }
            export function toggleTask(tasks: Task[], _id: string): Task[] { return tasks }
            """),
        new("src/components/TaskItem.tsx", """
            import type { Task } from '../tasks'
            interface Props { task: Task; onToggle: (id: string) => void; onDelete: (id: string) => void }
            export default function TaskItem({ task, onToggle, onDelete }: Props) {
              return <li><label><input type="checkbox" checked={task.completed} onChange={() => onToggle(task.id)} />
                <span>{task.title}</span></label><button onClick={() => onDelete(task.id)}>Supprimer {task.title}</button></li>
            }
            """),
        new("src/App.tsx", """
            import { useState } from 'react'
            import type { Task, Filter } from './tasks'
            import { visibleTasks } from './tasks'
            import TaskItem from './components/TaskItem'
            export default function App() {
              const [tasks] = useState<Task[]>([])
              const [filter] = useState<Filter>('all')
              return <main><h1>Task Studio</h1><ul>{visibleTasks(tasks, filter).map(task =>
                <TaskItem key={task.id} task={task} onToggle={() => {}} onDelete={() => {}} />)}</ul></main>
            }
            """),
        new("src/tasks.test.ts", """
            import { expect, test } from 'vitest'
            import { addTask, deleteTask, toggleTask, visibleTasks } from './tasks'
            import type { Task } from './tasks'
            const tasks: Task[] = [
              { id: 'a', title: 'Alpha', completed: false },
              { id: 'b', title: 'Beta', completed: true }
            ]
            test('adds a trimmed task without mutating input', () => {
              const added = addTask(tasks, '  Gamma  ')
              expect(added).toHaveLength(3)
              expect(added[2]).toEqual({ id: expect.any(String), title: 'Gamma', completed: false })
              expect(added[2].id).not.toBe('')
              expect(tasks).toHaveLength(2)
            })
            test('rejects whitespace-only titles', () => { expect(addTask(tasks, '   ')).toEqual(tasks) })
            test('deletes only the selected task', () => {
              expect(deleteTask(tasks, 'a')).toEqual([tasks[1]])
              expect(tasks).toHaveLength(2)
            })
            test('toggles completion without mutating input', () => {
              const changed = toggleTask(tasks, 'a')
              expect(changed[0].completed).toBe(true)
              expect(toggleTask(changed, 'a')[0].completed).toBe(false)
              expect(tasks[0].completed).toBe(false)
            })
            test('filters all tasks', () => { expect(visibleTasks(tasks, 'all')).toEqual(tasks) })
            test('filters active tasks', () => { expect(visibleTasks(tasks, 'active')).toEqual([tasks[0]]) })
            test('filters completed tasks', () => { expect(visibleTasks(tasks, 'completed')).toEqual([tasks[1]]) })
            """),
        new("src/App.test.tsx", """
            import { render, screen } from '@testing-library/react'
            import userEvent from '@testing-library/user-event'
            import { expect, test } from 'vitest'
            import App from './App'
            async function add(title: string) {
              await userEvent.type(screen.getByRole('textbox', { name: 'Nouvelle tâche' }), title)
              await userEvent.click(screen.getByRole('button', { name: 'Ajouter' }))
            }
            test('adds and deletes a real task', async () => {
              render(<App />)
              await add('Alpha')
              expect(screen.getByRole('checkbox', { name: 'Alpha' })).not.toBeChecked()
              await userEvent.click(screen.getByRole('button', { name: 'Supprimer Alpha' }))
              expect(screen.queryByText('Alpha')).not.toBeInTheDocument()
            })
            test('rejects blank input in the UI', async () => {
              render(<App />)
              await add('   ')
              expect(screen.queryAllByRole('checkbox')).toHaveLength(0)
            })
            test('completes tasks and switches all three filters', async () => {
              render(<App />)
              await add('Alpha')
              await add('Beta')
              await userEvent.click(screen.getByRole('checkbox', { name: 'Alpha' }))
              expect(screen.getByRole('checkbox', { name: 'Alpha' })).toBeChecked()
              const filter = screen.getByRole('combobox', { name: 'Filtrer les tâches' })
              await userEvent.selectOptions(filter, 'active')
              expect(screen.queryByText('Alpha')).not.toBeInTheDocument()
              expect(screen.getByRole('checkbox', { name: 'Beta' })).not.toBeChecked()
              await userEvent.selectOptions(filter, 'completed')
              expect(screen.getByRole('checkbox', { name: 'Alpha' })).toBeChecked()
              expect(screen.queryByText('Beta')).not.toBeInTheDocument()
              await userEvent.selectOptions(filter, 'all')
              expect(screen.getAllByRole('checkbox')).toHaveLength(2)
            })
            """),
        new("src/styles/app.css", """
            :root { font-family: system-ui, sans-serif; color: #172554; background: #eff6ff; }
            * { box-sizing: border-box; }
            body { margin: 0; padding: 1rem; }
            main { max-width: 48rem; margin: 2rem auto; padding: 1.5rem; border-radius: 1rem; background: white; }
            form, nav, li, label { display: flex; gap: .75rem; align-items: center; flex-wrap: wrap; }
            input[type=text] { flex: 1; min-width: 0; padding: .75rem; }
            button, select { padding: .65rem; cursor: pointer; }
            ul { padding: 0; list-style: none; }
            li { padding: .75rem 0; justify-content: space-between; border-bottom: 1px solid #dbeafe; overflow-wrap: anywhere; }
            input:checked + span { text-decoration: line-through; }
            @media (max-width: 480px) { main { padding: 1rem; margin: 0 auto; } form { align-items: stretch; flex-direction: column; } }
            """),
        new("README.md", """
            # React TypeScript Vite template v1

            Exact approved dependency versions and the original npm lockfile are supplied by AIPlatform.
            Source and tests are untrusted: never execute them on Windows automatically.
            Studio verifies TypeScript, Vite production build and nonempty Vitest tests offline in Docker,
            only after explicit approval of the exact source version and image.
            The prepared image provides dependencies; no project install or lifecycle script is run.
            Changes to dependency/configuration files are forbidden during agent generation.
            The template includes ten immutable task-manager contract tests. Agents implement the
            three application source files against these tests; assertions cannot be weakened.
            Scripts for a separately trusted development environment: npm run build, npm test, npm run lint.
            """)
    ];

    public static IReadOnlyList<PlannedFile> PlannedFiles => Files.Select(file => new PlannedFile(file.Path,
        IsEditable(file.Path) ? "Implement requested functionality and meaningful tests; preserve supplied public contracts." :
            "Trusted immutable React template configuration.",
        file.Path switch
        {
            "src/components/TaskItem.tsx" => ["src/tasks.ts"],
            "src/App.tsx" => ["src/tasks.ts", "src/components/TaskItem.tsx"],
            "src/tasks.test.ts" => ["src/tasks.ts"],
            "src/App.test.tsx" => ["src/tasks.ts", "src/components/TaskItem.tsx", "src/App.tsx"],
            _ => []
        })).ToArray();

    public static IReadOnlyList<GeneratedFile> ForReview(IEnumerable<GeneratedFile> files) =>
        files.Where(file => IsEditable(file.Path) || IsTest(file.Path)).ToArray();

    public static void Validate(IReadOnlyList<GeneratedFile> files)
    {
        if (files.Count != Files.Count || files.Any(file => !IsPath(file.Path)))
            throw new StudioValidationException("React files must match the versioned template paths.");
        foreach (var baseline in Files.Where(file => !IsEditable(file.Path)))
            if (files.SingleOrDefault(file => file.Path == baseline.Path)?.Content != baseline.Content)
                throw new StudioValidationException("React configuration and dependencies are immutable: " + baseline.Path);
    }
}
