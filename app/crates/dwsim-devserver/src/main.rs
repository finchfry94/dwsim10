//! The desktop app's engine, served over HTTP for development and end-to-end tests.
//!
//! The Tauri shell is the real host; this does the same job for a plain browser, so the frontend
//! can run under `vite` and Playwright without a webview:
//!
//! - `POST /rpc` with `{ method, params }` answers `{ result }` or `{ error }`, like `engine_call`;
//! - `GET /events` streams the engine's notifications as server-sent events;
//! - `GET /samples` lists the reference flowsheets in `tests/flowsheets`;
//! - anything else is served from `--static <dir>` when given (the built frontend).
//!
//! It binds to loopback only: whoever can reach it can read and write files through the engine.

use axum::extract::State;
use axum::response::sse::{Event, KeepAlive, Sse};
use axum::routing::{get, post};
use axum::{Json, Router};
use dwsim_bridge::{BridgeCommand, Engine};
use futures_util::Stream;
use serde::Deserialize;
use serde_json::{json, Value};
use std::convert::Infallible;
use std::path::PathBuf;
use std::sync::Arc;
use tokio_stream::wrappers::BroadcastStream;
use tokio_stream::StreamExt;

#[derive(Deserialize)]
struct Call {
    method: String,
    #[serde(default)]
    params: Value,
}

async fn rpc(State(engine): State<Arc<Engine>>, Json(call): Json<Call>) -> Json<Value> {
    let params = if call.params.is_null() { json!({}) } else { call.params };
    Json(match engine.call(&call.method, params).await {
        Ok(result) => json!({ "result": result }),
        Err(error) => json!({ "error": error }),
    })
}

async fn events(State(engine): State<Arc<Engine>>) -> Sse<impl Stream<Item = Result<Event, Infallible>>> {
    let stream = BroadcastStream::new(engine.subscribe())
        .filter_map(|n| n.ok())
        .map(|n| Ok(Event::default().json_data(n).unwrap_or_default()));
    Sse::new(stream).keep_alive(KeepAlive::default())
}

async fn samples() -> Json<Value> {
    let files = dwsim_bridge::reference_flowsheets()
        .into_iter()
        .map(|(name, path)| json!({ "name": name, "path": path }))
        .collect();
    Json(Value::Array(files))
}

#[tokio::main]
async fn main() {
    tracing_subscriber::fmt()
        .with_env_filter(tracing_subscriber::EnvFilter::from_default_env())
        .init();

    let mut port: u16 = 7878;
    let mut static_dir: Option<PathBuf> = None;
    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--port" => port = args.next().and_then(|p| p.parse().ok()).expect("--port <number>"),
            "--static" => static_dir = args.next().map(PathBuf::from),
            other => panic!("unknown argument {other}; use --port <n> and --static <dir>"),
        }
    }

    let command = BridgeCommand::locate().expect(
        "dwsim-bridge not found: build it with `dotnet build tools/DWSIM.Bridge -c Release` or set DWSIM_BRIDGE",
    );
    let engine = Arc::new(Engine::new(command));
    // Start the sidecar now, so the first request does not wait for the databases to load.
    if let Ok(client) = engine.client().await {
        tokio::spawn(async move { client.ready().await });
    }

    let mut app = Router::new()
        .route("/rpc", post(rpc))
        .route("/events", get(events))
        .route("/samples", get(samples))
        .with_state(engine);
    if let Some(dir) = static_dir {
        app = app.fallback_service(tower_http::services::ServeDir::new(dir));
    }

    let listener = tokio::net::TcpListener::bind(("127.0.0.1", port)).await.expect("bind");
    println!("dwsim-devserver on http://127.0.0.1:{port}");
    axum::serve(listener, app)
        .with_graceful_shutdown(async {
            let _ = tokio::signal::ctrl_c().await;
        })
        .await
        .expect("serve");
}
