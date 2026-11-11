#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use serde::{Deserialize, Serialize};
use std::{
    fs::{self, File, OpenOptions},
    io::{BufRead, BufReader, Write},
    net::{IpAddr, Ipv4Addr, SocketAddr},
    path::PathBuf,
    process::{Child, Command, Stdio},
    sync::{atomic::{AtomicBool, Ordering}, mpsc, Arc, Mutex},
    thread,
    time::{Duration, Instant},
};
use tauri::Manager;
#[cfg(windows)]
use std::os::windows::process::CommandExt;

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct BackendStatus {
    phase: String,
    url: Option<String>,
    token: Option<String>,
    error: Option<String>,
    log_path: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ReadyMessage {
    url: String,
    process_id: u32,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Health {
    ready: bool,
    process_id: u32,
}

struct BackendManager {
    status: Mutex<BackendStatus>,
    child: Mutex<Option<Child>>,
    operation: Mutex<()>,
    closing: AtomicBool,
    executable: PathBuf,
    content_root: PathBuf,
    log: Arc<Mutex<File>>,
}

impl BackendManager {
    fn new(app: &tauri::AppHandle) -> Result<Self, Box<dyn std::error::Error>> {
        let data = app.path().local_data_dir()?.join("AIPlatformStudio");
        fs::create_dir_all(&data)?;
        let log_path = data.join("backend.log");
        if log_path.exists() && fs::metadata(&log_path)?.len() > 1_048_576 {
            let previous = data.join("backend.previous.log");
            if previous.exists() {
                fs::remove_file(&previous)?;
            }
            fs::rename(&log_path, previous)?;
        }
        let log = OpenOptions::new().create(true).append(true).open(&log_path)?;
        let (executable, content_root) = if cfg!(debug_assertions) {
            let project = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
            (project.join("binaries/aiplatform-api-x86_64-pc-windows-msvc.exe"),
             project.join("resources/backend"))
        } else {
            let executable = std::env::current_exe()?.parent().ok_or("Missing application directory")?.join("aiplatform-api.exe");
            (executable, app.path().resource_dir()?.join("backend"))
        };
        Ok(Self {
            status: Mutex::new(BackendStatus { phase: "Starting".into(), url: None, token: None,
                error: None, log_path: log_path.to_string_lossy().into_owned() }),
            child: Mutex::new(None), operation: Mutex::new(()), closing: AtomicBool::new(false),
            executable, content_root, log: Arc::new(Mutex::new(log)),
        })
    }

    fn set_status(&self, phase: &str, url: Option<String>, token: Option<String>, error: Option<String>) {
        let mut state = self.status.lock().expect("Desktop status lock poisoned");
        state.phase = phase.into();
        state.url = url;
        state.token = token;
        state.error = error;
    }

    fn restart(&self) -> Result<(), String> {
        let _operation = self.operation.lock().expect("Desktop operation lock poisoned");
        if self.closing.load(Ordering::Acquire) {
            return Err("Application is closing.".into());
        }
        self.set_status("Starting", None, None, None);
        let result = self.stop_owned().and_then(|_| self.start_owned());
        if let Err(ref error) = result {
            log_line(&self.log, error);
            self.set_status("Failed", None, None, Some(error.clone()));
        }
        result
    }

    fn start_owned(&self) -> Result<(), String> {
        if !self.executable.is_file() || !self.content_root.join("appsettings.Desktop.json").is_file() {
            return Err("Packaged backend or configuration missing. Reinstall AIPlatform Studio.".into());
        }
        let mut random = [0u8; 32];
        getrandom::fill(&mut random).map_err(|error| error.to_string())?;
        let token: String = random.iter().map(|byte| format!("{byte:02x}")).collect();
        let mut command = Command::new(&self.executable);
        command.current_dir(&self.content_root)
            .env("ASPNETCORE_ENVIRONMENT", "Desktop").env("DOTNET_ENVIRONMENT", "Desktop")
            .env("AIPLATFORM_DESKTOP_TOKEN", &token)
            .env_remove("ASPNETCORE_URLS").env_remove("DOTNET_URLS")
            .stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::piped());
        #[cfg(windows)]
        command.creation_flags(0x08000000);
        let mut child = command.spawn().map_err(|error| format!("Cannot start packaged backend: {error}"))?;
        let pid = child.id();
        let stdout = child.stdout.take().ok_or("Missing backend output pipe")?;
        let stderr = child.stderr.take().ok_or("Missing backend error pipe")?;
        let (tx, rx) = mpsc::channel();
        let log = self.log.clone();
        thread::spawn(move || {
            for line in BufReader::new(stdout).lines() {
                match line {
                    Ok(line) => {
                        if let Some(message) = line.strip_prefix("AIPLATFORM_READY ") {
                            if tx.send(serde_json::from_str::<ReadyMessage>(message).map_err(|error| error.to_string())).is_err() {
                                log_line(&log, "Readiness receiver has closed.");
                            }
                        } else {
                            log_line(&log, &line);
                        }
                    }
                    Err(error) => { log_line(&log, &format!("Backend output read failed: {error}")); break; }
                }
            }
        });
        let log = self.log.clone();
        thread::spawn(move || {
            for line in BufReader::new(stderr).lines() {
                match line {
                    Ok(line) => log_line(&log, &line),
                    Err(error) => { log_line(&log, &format!("Backend error read failed: {error}")); break; }
                }
            }
        });
        *self.child.lock().expect("Backend process lock poisoned") = Some(child);
        let ready = (|| {
            let deadline = Instant::now() + Duration::from_secs(40);
            loop {
                if self.closing.load(Ordering::Acquire) {
                    return Err("Startup cancelled because the application is closing.".into());
                }
                match rx.recv_timeout(Duration::from_millis(100)) {
                    Ok(result) => return result,
                    Err(mpsc::RecvTimeoutError::Disconnected) => return Err("Backend exited before readiness. Consult the technical log.".into()),
                    Err(mpsc::RecvTimeoutError::Timeout) if Instant::now() >= deadline =>
                        return Err("Backend readiness exceeded 40 seconds. Consult the technical log.".into()),
                    Err(mpsc::RecvTimeoutError::Timeout) => {}
                }
            }
        })();
        let checked = ready.and_then(|ready| {
            validate_ready(&ready, pid)?;
            let agent = ureq::AgentBuilder::new().timeout(Duration::from_secs(3)).redirects(0).build();
            let health: Health = agent.get(&format!("{}/api/health", ready.url))
                .set("Authorization", &format!("Bearer {token}")).call()
                .map_err(|error| format!("Backend health check failed: {error}"))?
                .into_json().map_err(|error| format!("Invalid backend health response: {error}"))?;
            if !health.ready || health.process_id != pid {
                return Err("Readiness did not belong to the owned backend process.".into());
            }
            Ok(ready)
        });
        match checked {
            Ok(ready) => {
                let mut process = self.child.lock().expect("Backend process lock poisoned");
                if process.as_mut().ok_or("Backend process disappeared")?.try_wait().map_err(|error| error.to_string())?.is_some() {
                    return Err("Backend exited after its health check.".into());
                }
                self.set_status("Ready", Some(ready.url), Some(token), None);
                Ok(())
            }
            Err(error) => {
                if let Err(stop_error) = self.stop_owned() {
                    return Err(format!("{error} Cleanup: {stop_error}"));
                }
                Err(error)
            }
        }
    }

    fn stop_owned(&self) -> Result<(), String> {
        let child = self.child.lock().expect("Backend process lock poisoned").take();
        if let Some(mut child) = child {
            let result = (|| {
                if child.try_wait().map_err(|error| error.to_string())?.is_some() { return Ok(()); }
                if let Some(mut stdin) = child.stdin.take() {
                    if let Err(error) = stdin.write_all(b"shutdown\n") {
                        log_line(&self.log, &format!("Shutdown pipe write failed: {error}; closing the owner pipe."));
                    }
                }
                let deadline = Instant::now() + Duration::from_secs(22);
                while Instant::now() < deadline {
                    if child.try_wait().map_err(|error| error.to_string())?.is_some() { return Ok(()); }
                    thread::sleep(Duration::from_millis(100));
                }
                child.kill().map_err(|error| format!("Cannot terminate the owned backend: {error}"))?;
                child.wait().map_err(|error| error.to_string())?;
                Err("Backend did not stop gracefully in 22 seconds and required forced termination.".into())
            })();
            if result.is_err() {
                *self.child.lock().expect("Backend process lock poisoned") = Some(child);
            }
            return result;
        }
        Ok(())
    }

    fn shutdown(&self) {
        self.closing.store(true, Ordering::Release);
        let _operation = self.operation.lock().expect("Desktop operation lock poisoned");
        if let Err(error) = self.stop_owned() {
            log_line(&self.log, &error);
        }
        self.set_status("Stopped", None, None, None);
    }

    fn monitor(&self) {
        let mut process = self.child.lock().expect("Backend process lock poisoned");
        if self.status.lock().expect("Desktop status lock poisoned").phase != "Ready" { return; }
        if let Some(child) = process.as_mut() {
            match child.try_wait() {
                Ok(None) => {}
                Ok(Some(status)) => {
                    let error = format!("Backend stopped unexpectedly ({status}).");
                    log_line(&self.log, &error);
                    self.set_status("Failed", None, None, Some(error));
                    *process = None;
                }
                Err(error) => {
                    let error = format!("Cannot inspect the owned backend: {error}");
                    log_line(&self.log, &error);
                    self.set_status("Failed", None, None, Some(error));
                }
            }
        }
    }
}

fn log_line(log: &Mutex<File>, line: &str) {
    if let Err(error) = writeln!(log.lock().expect("Backend log lock poisoned"), "{line}") {
        eprintln!("Cannot write backend technical log: {error}");
    }
}

fn validate_ready(ready: &ReadyMessage, pid: u32) -> Result<(), String> {
    let address: SocketAddr = ready.url.strip_prefix("http://").ok_or("Backend must use local HTTP")?
        .parse().map_err(|_| "Invalid backend listening address")?;
    if address.ip() != IpAddr::V4(Ipv4Addr::LOCALHOST) || address.port() == 0 || ready.process_id != pid {
        return Err("Refused a non-loopback or foreign backend readiness address.".into());
    }
    Ok(())
}

#[tauri::command]
fn backend_status(manager: tauri::State<'_, Arc<BackendManager>>) -> BackendStatus {
    manager.status.lock().expect("Desktop status lock poisoned").clone()
}

#[tauri::command]
async fn restart_backend(manager: tauri::State<'_, Arc<BackendManager>>) -> Result<(), String> {
    let manager = manager.inner().clone();
    tauri::async_runtime::spawn_blocking(move || manager.restart()).await.map_err(|error| error.to_string())?
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _, _| {
            if let Some(window) = app.get_webview_window("main") {
                if let Err(error) = window.set_focus() { eprintln!("Cannot focus Studio: {error}"); }
            }
        }))
        .setup(|app| {
            let manager = Arc::new(BackendManager::new(app.handle())?);
            app.manage(manager.clone());
            tauri::WebviewWindowBuilder::from_config(app, &app.config().app.windows[0])?
                .on_navigation(|url| {
                    url.scheme() == "tauri" && url.host_str() == Some("localhost") ||
                    url.scheme() == "http" && url.host_str() == Some("tauri.localhost") ||
                    cfg!(debug_assertions) && url.scheme() == "http" && url.host_str() == Some("127.0.0.1") && url.port() == Some(5173)
                })
                .build()?;
            let starting = manager.clone();
            thread::spawn(move || {
                if let Err(error) = starting.restart() { eprintln!("Backend startup failed: {error}"); }
            });
            thread::spawn(move || {
                while !manager.closing.load(Ordering::Acquire) {
                    thread::sleep(Duration::from_millis(500));
                    manager.monitor();
                }
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![backend_status, restart_backend])
        .build(tauri::generate_context!())
        .expect("Cannot initialize the native Studio application")
        .run(|app, event| {
            if let tauri::RunEvent::ExitRequested { .. } = event {
                app.state::<Arc<BackendManager>>().shutdown();
            }
        });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn readiness_must_match_local_address_and_owned_pid() {
        assert!(validate_ready(&ReadyMessage { url: "http://127.0.0.1:45000".into(), process_id: 42 }, 42).is_ok());
        for url in ["http://0.0.0.0:45000", "http://192.168.1.1:45000", "https://127.0.0.1:45000",
            "http://127.0.0.1:0", "http://127.0.0.1:45000/foreign", "http://example.com:45000"] {
            assert!(validate_ready(&ReadyMessage { url: url.into(), process_id: 42 }, 42).is_err());
        }
        assert!(validate_ready(&ReadyMessage { url: "http://127.0.0.1:45000".into(), process_id: 43 }, 42).is_err());
    }
}
