use serde::Serialize;

/// Why an engine call failed. Serializes as `{ kind, code?, message }` for the frontend.
#[derive(Debug, Clone, thiserror::Error, Serialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum BridgeError {
    /// The sidecar could not be started (not built, not bundled, `dotnet` missing).
    #[error("could not start the engine: {message}")]
    Spawn { message: String },

    /// The sidecar exited, or closed its output, before answering.
    #[error("the engine stopped")]
    Disconnected { message: String },

    /// The sidecar did not announce it was ready in time.
    #[error("the engine did not start in time")]
    Timeout { message: String },

    /// The engine answered with a JSON-RPC error.
    #[error("{message}")]
    Rpc { code: i64, message: String },
}

impl BridgeError {
    pub(crate) fn disconnected() -> Self {
        Self::Disconnected {
            message: "the engine stopped".into(),
        }
    }

    /// JSON-RPC code the bridge uses for a cancelled request.
    pub const CANCELLED: i64 = -32800;

    pub fn is_cancelled(&self) -> bool {
        matches!(self, Self::Rpc { code, .. } if *code == Self::CANCELLED)
    }
}
