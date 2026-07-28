use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    fs,
    io::{BufRead, BufReader, Write},
    path::PathBuf,
    process::{Child, ChildStdin, Command, Stdio},
    sync::{mpsc, Arc, Mutex},
    thread,
    time::Duration,
};
use tauri::{AppHandle, Manager, State};

const DEFAULT_SERVER_BASE_URL: &str = "http://124.221.136.83:8000";
const CLIENT_CONFIG_FILE_NAME: &str = "client-config.json";

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ClientConfigFile {
    server_base_url: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ClientConfigResult {
    config: ClientConfigFile,
    file_path: String,
}

#[tauri::command]
fn load_client_config() -> Result<ClientConfigResult, String> {
    let path = resolve_client_config_path()?;
    let config = if path.exists() {
        let content = fs::read_to_string(&path)
            .map_err(|error| format!("Failed to read client config: {error}"))?;
        serde_json::from_str::<ClientConfigFile>(&content)
            .map_err(|error| format!("Invalid client config JSON: {error}"))?
    } else {
        let config = ClientConfigFile {
            server_base_url: DEFAULT_SERVER_BASE_URL.to_string(),
        };
        if let Err(error) = write_client_config(&path, &config) {
            eprintln!("Failed to create client config beside executable: {error}");
        }
        config
    };

    Ok(ClientConfigResult {
        config,
        file_path: path.to_string_lossy().into_owned(),
    })
}

#[tauri::command]
fn save_client_config(config: ClientConfigFile) -> Result<ClientConfigResult, String> {
    let server_base_url = config.server_base_url.trim().trim_end_matches('/');
    if !(server_base_url.starts_with("http://") || server_base_url.starts_with("https://")) {
        return Err("Server address must start with http:// or https://".to_string());
    }

    let normalized = ClientConfigFile {
        server_base_url: server_base_url.to_string(),
    };
    let path = resolve_client_config_path()?;
    write_client_config(&path, &normalized)?;

    Ok(ClientConfigResult {
        config: normalized,
        file_path: path.to_string_lossy().into_owned(),
    })
}

fn resolve_client_config_path() -> Result<PathBuf, String> {
    let executable = std::env::current_exe()
        .map_err(|error| format!("Failed to resolve client executable path: {error}"))?;
    let executable_dir = executable
        .parent()
        .ok_or_else(|| "Client executable directory is unavailable".to_string())?;
    Ok(executable_dir.join(CLIENT_CONFIG_FILE_NAME))
}

fn write_client_config(path: &PathBuf, config: &ClientConfigFile) -> Result<(), String> {
    let content = serde_json::to_string_pretty(config)
        .map_err(|error| format!("Failed to serialize client config: {error}"))?;
    fs::write(path, format!("{content}\n"))
        .map_err(|error| format!("Failed to write client config: {error}"))
}

#[derive(Default)]
struct SidecarState {
    child: Option<Child>,
    stdin: Option<ChildStdin>,
    receiver: Option<mpsc::Receiver<String>>,
}

#[tauri::command]
async fn codegen_execution(
    app: AppHandle,
    state: State<'_, Arc<Mutex<SidecarState>>>,
    request: Value,
) -> Result<Value, String> {
    let app = app.clone();
    let state = state.inner().clone();

    tauri::async_runtime::spawn_blocking(move || execute_sidecar_request(&app, &state, request))
        .await
        .map_err(|error| error.to_string())?
}

fn execute_sidecar_request(
    app: &AppHandle,
    state: &Arc<Mutex<SidecarState>>,
    mut request: Value,
) -> Result<Value, String> {
    let id = ensure_request_id(&mut request);
    let mut guard = state
        .lock()
        .map_err(|_| "Sidecar state lock failed".to_string())?;
    guard.ensure_started(app)?;

    let stdin = guard
        .stdin
        .as_mut()
        .ok_or_else(|| "Sidecar stdin is unavailable".to_string())?;

    let line = serde_json::to_string(&request).map_err(|error| error.to_string())?;
    stdin
        .write_all(line.as_bytes())
        .and_then(|_| stdin.write_all(b"\n"))
        .and_then(|_| stdin.flush())
        .map_err(|error| format!("Failed to write sidecar request: {error}"))?;

    let receiver = guard
        .receiver
        .as_ref()
        .ok_or_else(|| "Sidecar stdout receiver is unavailable".to_string())?;

    loop {
        let line = receiver
            .recv_timeout(Duration::from_secs(300))
            .map_err(|_| "Timed out waiting for sidecar response".to_string())?;

        let response: Value = match serde_json::from_str(&line) {
            Ok(value) => value,
            Err(_) => continue,
        };

        if response
            .get("id")
            .and_then(Value::as_str)
            .map(|response_id| response_id == id)
            .unwrap_or(false)
        {
            return Ok(response);
        }
    }
}

impl SidecarState {
    fn ensure_started(&mut self, app: &AppHandle) -> Result<(), String> {
        if let Some(child) = self.child.as_mut() {
            if child
                .try_wait()
                .map_err(|error| error.to_string())?
                .is_none()
            {
                return Ok(());
            }
        }

        let sidecar_path = resolve_sidecar_path(app)?;
        let mut child = Command::new(sidecar_path)
            .arg("--stdio")
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .map_err(|error| format!("Failed to start CodeMaster sidecar: {error}"))?;

        let stdin = child
            .stdin
            .take()
            .ok_or_else(|| "Failed to open sidecar stdin".to_string())?;
        let stdout = child
            .stdout
            .take()
            .ok_or_else(|| "Failed to open sidecar stdout".to_string())?;

        let (sender, receiver) = mpsc::channel();
        thread::spawn(move || {
            let reader = BufReader::new(stdout);
            for line in reader.lines().map_while(Result::ok) {
                if sender.send(line).is_err() {
                    break;
                }
            }
        });

        self.child = Some(child);
        self.stdin = Some(stdin);
        self.receiver = Some(receiver);

        Ok(())
    }
}

fn ensure_request_id(request: &mut Value) -> String {
    let existing_id = request
        .get("id")
        .and_then(Value::as_str)
        .filter(|id| !id.is_empty())
        .map(str::to_string);

    if let Some(id) = existing_id {
        return id;
    }

    let id = format!(
        "{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|duration| duration.as_nanos())
            .unwrap_or_default()
    );

    if let Some(object) = request.as_object_mut() {
        object.insert("id".to_string(), json!(id));
    }

    id
}

fn resolve_sidecar_path(app: &AppHandle) -> Result<PathBuf, String> {
    let names = [
        platform_binary_name("codemaster-local-agent"),
        format!(
            "codemaster-local-agent-{}{}",
            target_triple(),
            std::env::consts::EXE_SUFFIX
        ),
    ];

    if let Ok(resource_dir) = app.path().resource_dir() {
        for name in names.iter() {
            let path = resource_dir.join(name);
            if path.exists() {
                return Ok(path);
            }
        }
    }

    let binaries_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("binaries");
    for name in names.iter() {
        let path = binaries_dir.join(name);
        if path.exists() {
            return Ok(path);
        }
    }

    Err(format!(
        "CodeMaster sidecar was not found. Expected one of: {}",
        names.join(", ")
    ))
}

fn platform_binary_name(name: &str) -> String {
    format!("{name}{}", std::env::consts::EXE_SUFFIX)
}

fn target_triple() -> &'static str {
    match (std::env::consts::OS, std::env::consts::ARCH) {
        ("windows", "x86_64") => "x86_64-pc-windows-msvc",
        ("windows", "aarch64") => "aarch64-pc-windows-msvc",
        ("macos", "x86_64") => "x86_64-apple-darwin",
        ("macos", "aarch64") => "aarch64-apple-darwin",
        ("linux", "x86_64") => "x86_64-unknown-linux-gnu",
        ("linux", "aarch64") => "aarch64-unknown-linux-gnu",
        _ => "unknown",
    }
}

fn main() {
    tauri::Builder::default()
        .manage(Arc::new(Mutex::new(SidecarState::default())))
        .invoke_handler(tauri::generate_handler![
            codegen_execution,
            load_client_config,
            save_client_config
        ])
        .run(tauri::generate_context!())
        .expect("error while running CodeMaster desktop client");
}
