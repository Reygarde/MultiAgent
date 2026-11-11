import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import DesktopApp from './desktop/DesktopApp'
import { isTauri } from '@tauri-apps/api/core'
import './styles.css'

const Root = isTauri() ? DesktopApp : App
createRoot(document.getElementById('root')!).render(<StrictMode><Root /></StrictMode>)
