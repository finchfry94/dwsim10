//! The Tauri shell: owns the engine sidecar and relays between it and the webview.
//!
//! The frontend never talks to the sidecar directly. It calls `engine_call`, and receives every
//! engine notification as the `engine://notification` event. Native Rust implementations of
//! engine methods will be routed in `dwsim_bridge::Engine::call`, behind the same command.

use dwsim_bridge::{BridgeCommand, BridgeError, Engine};
use serde::Serialize;
use serde_json::Value;
use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use tauri::{Emitter, Manager, State};
use tokio::sync::oneshot;

struct AppEngine {
    engine: Arc<Engine>,
    /// Cancellation handles of the calls in flight that the frontend tagged with a request id.
    cancels: Mutex<HashMap<String, oneshot::Sender<()>>>,
}

/// Calls an engine method. A `requestId` makes the call cancellable through `engine_cancel`.
#[tauri::command]
async fn engine_call(
    state: State<'_, AppEngine>,
    method: String,
    params: Option<Value>,
    request_id: Option<String>,
) -> Result<Value, BridgeError> {
    let params = params
        .filter(|p| !p.is_null())
        .unwrap_or_else(|| Value::Object(Default::default()));
    let Some(request_id) = request_id else {
        return state.engine.call(&method, params).await;
    };
    let (tx, rx) = oneshot::channel();
    state.cancels.lock().unwrap().insert(request_id.clone(), tx);
    let result = state
        .engine
        .call_cancellable(&method, params, async {
            // A dropped sender (the call finished) must not count as a cancellation.
            if rx.await.is_err() {
                std::future::pending::<()>().await;
            }
        })
        .await;
    state.cancels.lock().unwrap().remove(&request_id);
    result
}

#[tauri::command]
fn engine_cancel(state: State<'_, AppEngine>, request_id: String) {
    if let Some(cancel) = state.cancels.lock().unwrap().remove(&request_id) {
        let _ = cancel.send(());
    }
}

#[derive(Serialize)]
struct SampleFile {
    name: String,
    path: std::path::PathBuf,
}

/// The reference flowsheets of a development checkout; none in an installed app.
#[tauri::command]
fn reference_flowsheets() -> Vec<SampleFile> {
    dwsim_bridge::reference_flowsheets()
        .into_iter()
        .map(|(name, path)| SampleFile { name, path })
        .collect()
}

pub fn run() {
    tracing_subscriber::fmt()
        .with_env_filter(tracing_subscriber::EnvFilter::try_from_default_env().unwrap_or_else(|_| "info".into()))
        .init();

    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            // A bundle carries the published sidecar in its resources, under engine/.
            let bundled: Vec<_> = app.path().resource_dir().map(|d| vec![d.join("engine")]).unwrap_or_default();
            let command = BridgeCommand::locate_in(&bundled).ok_or(
                "dwsim-bridge not found: build it with `dotnet build tools/DWSIM.Bridge -c Release` or set DWSIM_BRIDGE",
            )?;
            tracing::info!("engine: {} {:?}", command.program.display(), command.args);
            let engine = Arc::new(Engine::new(command));

            let mut notifications = engine.subscribe();
            let handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                use tokio::sync::broadcast::error::RecvError;
                loop {
                    match notifications.recv().await {
                        Ok(n) => {
                            let _ = handle.emit("engine://notification", n);
                        }
                        Err(RecvError::Lagged(missed)) => tracing::warn!("dropped {missed} engine notifications"),
                        Err(RecvError::Closed) => break,
                    }
                }
            });

            // Start the sidecar now, so the databases load while the window opens.
            let warm = engine.clone();
            tauri::async_runtime::spawn(async move {
                if let Ok(client) = warm.client().await {
                    let _ = client.ready().await;
                }
            });

            app.manage(AppEngine { engine, cancels: Mutex::default() });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![engine_call, engine_cancel, reference_flowsheets])
        .run(tauri::generate_context!())
        .expect("error while running the DWSIM app");
}
