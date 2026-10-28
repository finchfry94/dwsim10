use crate::{BridgeCommand, BridgeError};
use serde::Serialize;
use serde_json::{json, Value};
use std::collections::HashMap;
use std::future::Future;
use std::process::Stdio;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::process::{Child, Command};
use tokio::sync::{broadcast, mpsc, oneshot, watch};

/// Loading the compound databases takes a few seconds; a cold start on a slow disk, longer.
const READY_TIMEOUT: Duration = Duration::from_secs(180);

/// A message the engine sends unprompted: solver progress, a log line, `engine/ready`.
/// The client adds `engine/stderr` for the sidecar's own log and `engine/exited` when it stops.
#[derive(Debug, Clone, Serialize)]
pub struct Notification {
    pub method: String,
    pub params: Value,
}

type Pending = Arc<Mutex<HashMap<u64, oneshot::Sender<Result<Value, BridgeError>>>>>;

/// One running sidecar process and the requests waiting on it.
pub struct BridgeClient {
    outgoing: mpsc::UnboundedSender<String>,
    pending: Pending,
    next_id: AtomicU64,
    ready: watch::Receiver<bool>,
    closed: Arc<AtomicBool>,
    // Held so the process is killed when the client is dropped.
    _child: Arc<tokio::sync::Mutex<Child>>,
}

impl BridgeClient {
    pub fn spawn(command: &BridgeCommand, notifications: broadcast::Sender<Notification>) -> Result<Self, BridgeError> {
        let mut child = Command::new(&command.program)
            .args(&command.args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .kill_on_drop(true)
            .spawn()
            .map_err(|e| BridgeError::Spawn {
                message: format!("{}: {e}", command.program.display()),
            })?;

        let mut stdin = child.stdin.take().expect("stdin is piped");
        let stdout = child.stdout.take().expect("stdout is piped");
        let stderr = child.stderr.take().expect("stderr is piped");

        let pending: Pending = Arc::default();
        let closed = Arc::new(AtomicBool::new(false));
        let (ready_tx, ready) = watch::channel(false);
        let (outgoing, mut queue) = mpsc::unbounded_channel::<String>();

        tokio::spawn(async move {
            while let Some(line) = queue.recv().await {
                if stdin.write_all(line.as_bytes()).await.is_err()
                    || stdin.write_all(b"\n").await.is_err()
                    || stdin.flush().await.is_err()
                {
                    break;
                }
            }
        });

        let log = notifications.clone();
        tokio::spawn(async move {
            let mut lines = BufReader::new(stderr).lines();
            while let Ok(Some(line)) = lines.next_line().await {
                tracing::debug!(target: "dwsim_bridge::engine", "{line}");
                let _ = log.send(Notification {
                    method: "engine/stderr".into(),
                    params: json!({ "text": line }),
                });
            }
        });

        {
            let pending = pending.clone();
            let closed = closed.clone();
            tokio::spawn(async move {
                let mut lines = BufReader::new(stdout).lines();
                while let Ok(Some(line)) = lines.next_line().await {
                    route(&line, &pending, &notifications, &ready_tx);
                }
                closed.store(true, Ordering::SeqCst);
                for (_, waiter) in pending.lock().unwrap().drain() {
                    let _ = waiter.send(Err(BridgeError::disconnected()));
                }
                let _ = notifications.send(Notification {
                    method: "engine/exited".into(),
                    params: json!({}),
                });
            });
        }

        Ok(Self {
            outgoing,
            pending,
            next_id: AtomicU64::new(1),
            ready,
            closed,
            _child: Arc::new(tokio::sync::Mutex::new(child)),
        })
    }

    pub fn is_closed(&self) -> bool {
        self.closed.load(Ordering::SeqCst)
    }

    /// Waits for `engine/ready`, which the sidecar sends once its databases are loaded.
    pub async fn ready(&self) -> Result<(), BridgeError> {
        let mut ready = self.ready.clone();
        let wait = async {
            while !*ready.borrow_and_update() {
                if self.is_closed() || ready.changed().await.is_err() {
                    return Err(BridgeError::disconnected());
                }
            }
            Ok(())
        };
        tokio::time::timeout(READY_TIMEOUT, wait)
            .await
            .map_err(|_| BridgeError::Timeout {
                message: format!("no engine/ready after {READY_TIMEOUT:?}"),
            })?
    }

    pub async fn call(&self, method: &str, params: Value) -> Result<Value, BridgeError> {
        self.call_cancellable(method, params, std::future::pending()).await
    }

    /// Sends a request and waits for its response. If `cancel` resolves first, the engine is
    /// asked to stop, and the call still returns the engine's answer (normally a cancelled error).
    pub async fn call_cancellable(
        &self,
        method: &str,
        params: Value,
        cancel: impl Future<Output = ()>,
    ) -> Result<Value, BridgeError> {
        self.ready().await?;
        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let (tx, mut rx) = oneshot::channel();
        self.pending.lock().unwrap().insert(id, tx);

        let request = json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params });
        if self.outgoing.send(request.to_string()).is_err() || self.is_closed() {
            self.pending.lock().unwrap().remove(&id);
            return Err(BridgeError::disconnected());
        }

        tokio::pin!(cancel);
        let answer = tokio::select! {
            answer = &mut rx => answer,
            () = &mut cancel => {
                let note = json!({ "jsonrpc": "2.0", "method": "$/cancelRequest", "params": { "id": id } });
                let _ = self.outgoing.send(note.to_string());
                rx.await
            }
        };
        answer.unwrap_or_else(|_| Err(BridgeError::disconnected()))
    }
}

fn route(line: &str, pending: &Pending, notifications: &broadcast::Sender<Notification>, ready: &watch::Sender<bool>) {
    let Ok(message) = serde_json::from_str::<Value>(line) else {
        tracing::warn!("engine sent a line that is not JSON: {line}");
        return;
    };

    if let Some(method) = message.get("method").and_then(Value::as_str) {
        if method == "engine/ready" {
            let _ = ready.send(true);
        }
        let params = message.get("params").cloned().unwrap_or(Value::Null);
        let _ = notifications.send(Notification {
            method: method.to_owned(),
            params,
        });
        return;
    }

    let Some(id) = message.get("id").and_then(Value::as_u64) else {
        tracing::warn!("engine response without a usable id: {line}");
        return;
    };
    let Some(waiter) = pending.lock().unwrap().remove(&id) else {
        return;
    };
    let outcome = match message.get("error") {
        Some(error) => Err(BridgeError::Rpc {
            code: error.get("code").and_then(Value::as_i64).unwrap_or(-32603),
            message: error
                .get("message")
                .and_then(Value::as_str)
                .unwrap_or("engine error")
                .to_owned(),
        }),
        None => Ok(message.get("result").cloned().unwrap_or(Value::Null)),
    };
    let _ = waiter.send(outcome);
}
