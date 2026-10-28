//! The client against a scripted stand-in for the sidecar, so these run without .NET.
//! A last test drives the real bridge when a build of it can be found.

use dwsim_bridge::{BridgeCommand, BridgeError, Engine};
use serde_json::json;
use std::time::Duration;

fn fake() -> Option<BridgeCommand> {
    let python = ["python3", "python"]
        .into_iter()
        .find(|p| std::process::Command::new(p).arg("--version").output().is_ok())?;
    let script = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/fake_bridge.py");
    Some(BridgeCommand {
        program: python.into(),
        args: vec!["-u".into(), script.into()],
    })
}

macro_rules! fake_or_skip {
    () => {
        match fake() {
            Some(cmd) => Engine::new(cmd),
            None => {
                eprintln!("skipped: no python to run the fake bridge");
                return;
            }
        }
    };
}

#[tokio::test]
async fn matches_responses_to_concurrent_requests() {
    let engine = std::sync::Arc::new(fake_or_skip!());
    let tasks: Vec<_> = (0..20)
        .map(|i| {
            let engine = engine.clone();
            tokio::spawn(async move { engine.call("add", json!({ "a": i, "b": 100 })).await })
        })
        .collect();
    for (i, task) in tasks.into_iter().enumerate() {
        assert_eq!(task.await.unwrap().unwrap(), json!(i + 100));
    }
}

#[tokio::test]
async fn forwards_notifications_and_errors() {
    let engine = fake_or_skip!();
    let mut events = engine.subscribe();
    assert_eq!(engine.call("echo", json!({ "x": 1 })).await.unwrap(), json!({ "x": 1 }));
    let echoed = loop {
        let n = events.recv().await.unwrap();
        if n.method == "test/echoed" {
            break n;
        }
    };
    assert_eq!(echoed.params, json!({ "x": 1 }));

    match engine.call("missing", json!({})).await {
        Err(BridgeError::Rpc { code, .. }) => assert_eq!(code, -32601),
        other => panic!("expected an RPC error, got {other:?}"),
    }
}

#[tokio::test]
async fn cancellation_reaches_the_engine() {
    let engine = fake_or_skip!();
    let result = engine
        .call_cancellable("slow", json!({}), tokio::time::sleep(Duration::from_millis(100)))
        .await;
    assert!(result.as_ref().is_err_and(BridgeError::is_cancelled), "{result:?}");
}

#[tokio::test]
async fn restarts_after_the_sidecar_exits() {
    let engine = fake_or_skip!();
    let mut events = engine.subscribe();
    assert!(matches!(
        engine.call("exit", json!({})).await,
        Err(BridgeError::Disconnected { .. })
    ));
    loop {
        if events.recv().await.unwrap().method == "engine/exited" {
            break;
        }
    }
    assert_eq!(engine.call("add", json!({ "a": 1, "b": 2 })).await.unwrap(), json!(3));
}

/// Opens and solves a reference flowsheet on the real engine, when it has been built
/// (`dotnet build tools/DWSIM.Bridge -c Release`) and `dotnet` is on the path.
#[tokio::test]
async fn real_bridge_opens_and_solves_cavett() {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../..");
    let dll = root.join("tools/DWSIM.Bridge/bin/Release/net10.0/dwsim-bridge.dll");
    if !dll.is_file() {
        eprintln!("skipped: {} not built", dll.display());
        return;
    }
    let engine = Engine::new(BridgeCommand::for_path(dll));
    let fs = engine
        .call(
            "flowsheet.open",
            json!({ "path": root.join("tests/flowsheets/CavettProblem.dwxml") }),
        )
        .await
        .unwrap();
    let id = fs["id"].as_str().unwrap().to_owned();
    let graph = engine.call("graph.get", json!({ "flowsheetId": id })).await.unwrap();
    assert!(graph["nodes"].as_array().unwrap().len() > 30);
    let solved = engine.call("solve.run", json!({ "flowsheetId": id })).await.unwrap();
    assert_eq!(solved["ok"], json!(true), "{solved}");
}
