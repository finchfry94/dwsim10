//! Client for the DWSIM engine sidecar.
//!
//! The engine is the .NET project `tools/DWSIM.Bridge`, run as a child process that speaks
//! line-delimited JSON-RPC 2.0 over stdio. [`Engine`] owns that process: it starts it on first
//! use, matches responses to requests, fans notifications out to subscribers, and starts a fresh
//! process when the old one has died.
//!
//! This is the seam the Rust port grows behind: a method served natively by a Rust crate is
//! answered in [`Engine::call`] before it would be forwarded, so callers never change.

mod client;
mod error;
mod locate;

pub use client::{BridgeClient, Notification};
pub use error::BridgeError;
pub use locate::{reference_flowsheets, BridgeCommand};

use serde_json::Value;
use std::sync::Arc;
use tokio::sync::{broadcast, Mutex};

/// The engine as the application sees it: one sidecar, restarted on demand.
pub struct Engine {
    command: BridgeCommand,
    client: Mutex<Option<Arc<BridgeClient>>>,
    notifications: broadcast::Sender<Notification>,
}

impl Engine {
    pub fn new(command: BridgeCommand) -> Self {
        let (notifications, _) = broadcast::channel(1024);
        Self {
            command,
            client: Mutex::new(None),
            notifications,
        }
    }

    /// Notifications from every sidecar this engine starts, including `engine/exited`.
    pub fn subscribe(&self) -> broadcast::Receiver<Notification> {
        self.notifications.subscribe()
    }

    /// The running sidecar, started now if there is none or the last one exited.
    pub async fn client(&self) -> Result<Arc<BridgeClient>, BridgeError> {
        let mut slot = self.client.lock().await;
        if let Some(client) = slot.as_ref() {
            if !client.is_closed() {
                return Ok(client.clone());
            }
        }
        let client = Arc::new(BridgeClient::spawn(&self.command, self.notifications.clone())?);
        *slot = Some(client.clone());
        Ok(client)
    }

    /// Calls an engine method. Flowsheets live in the sidecar, so after a restart the ids the
    /// caller holds are gone; it learns of the restart from the `engine/exited` notification.
    pub async fn call(&self, method: &str, params: Value) -> Result<Value, BridgeError> {
        self.client().await?.call(method, params).await
    }

    /// Like [`Engine::call`], but `cancel` resolving sends `$/cancelRequest` for it.
    pub async fn call_cancellable(
        &self,
        method: &str,
        params: Value,
        cancel: impl std::future::Future<Output = ()>,
    ) -> Result<Value, BridgeError> {
        self.client().await?.call_cancellable(method, params, cancel).await
    }
}
