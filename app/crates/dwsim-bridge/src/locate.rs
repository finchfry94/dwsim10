use std::path::{Path, PathBuf};

/// How to start the sidecar: a program, its arguments and the folder to run it in.
#[derive(Debug, Clone)]
pub struct BridgeCommand {
    pub program: PathBuf,
    pub args: Vec<String>,
}

const NAME: &str = "dwsim-bridge";

impl BridgeCommand {
    /// Runs a published executable directly, or a framework-dependent `.dll` through `dotnet`.
    pub fn for_path(path: impl Into<PathBuf>) -> Self {
        let path = path.into();
        if path.extension().is_some_and(|e| e.eq_ignore_ascii_case("dll")) {
            Self {
                program: "dotnet".into(),
                args: vec![path.to_string_lossy().into_owned()],
            }
        } else {
            Self {
                program: path,
                args: Vec::new(),
            }
        }
    }

    /// Finds the sidecar, in order:
    /// 1. `DWSIM_BRIDGE`, a path to the executable or the `.dll`;
    /// 2. next to the running executable, where a Tauri bundle puts its `externalBin`;
    /// 3. a local build, `tools/DWSIM.Bridge/bin/{Release,Debug}/net10.0/dwsim-bridge.dll`, in
    ///    the current folder or any folder above it, for development.
    pub fn locate() -> Option<Self> {
        Self::locate_in(&[])
    }

    /// [`BridgeCommand::locate`], also looking in `bundled`, folders holding a published sidecar
    /// (the app's resource folder), after the environment variable and before a development build.
    pub fn locate_in(bundled: &[PathBuf]) -> Option<Self> {
        if let Some(path) = std::env::var_os("DWSIM_BRIDGE") {
            return Some(Self::for_path(path));
        }
        let exe_dir = std::env::current_exe()
            .ok()
            .and_then(|exe| exe.parent().map(Path::to_path_buf));
        let exe_name = format!("{NAME}{}", std::env::consts::EXE_SUFFIX);
        if let Some(found) = exe_dir
            .iter()
            .chain(bundled)
            .map(|dir| dir.join(&exe_name))
            .find(|p| p.is_file())
        {
            return Some(Self::for_path(found));
        }
        let start = std::env::current_dir().ok()?;
        start.ancestors().find_map(dev_build).map(Self::for_path)
    }
}

/// The reference flowsheets in `tests/flowsheets`, found from the current folder upward, sorted
/// by name. Only a development checkout has them; an installed app returns none.
pub fn reference_flowsheets() -> Vec<(String, PathBuf)> {
    let Some(folder) = std::env::current_dir()
        .ok()
        .and_then(|dir| dir.ancestors().map(|d| d.join("tests/flowsheets")).find(|d| d.is_dir()))
    else {
        return Vec::new();
    };
    let mut files: Vec<(String, PathBuf)> = std::fs::read_dir(folder)
        .into_iter()
        .flatten()
        .filter_map(Result::ok)
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|e| e == "dwxml" || e == "dwxmz"))
        .map(|p| (p.file_stem().unwrap_or_default().to_string_lossy().into_owned(), p))
        .collect();
    files.sort();
    files
}

fn dev_build(root: &Path) -> Option<PathBuf> {
    ["Release", "Debug"].iter().find_map(|config| {
        let dll = root
            .join("tools/DWSIM.Bridge/bin")
            .join(config)
            .join("net10.0")
            .join(format!("{NAME}.dll"));
        dll.is_file().then_some(dll)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dll_runs_through_dotnet() {
        let cmd = BridgeCommand::for_path("/x/dwsim-bridge.dll");
        assert_eq!(cmd.program, PathBuf::from("dotnet"));
        assert_eq!(cmd.args, vec!["/x/dwsim-bridge.dll".to_string()]);
    }

    #[test]
    fn executable_runs_directly() {
        let cmd = BridgeCommand::for_path("/x/dwsim-bridge");
        assert_eq!(cmd.program, PathBuf::from("/x/dwsim-bridge"));
        assert!(cmd.args.is_empty());
    }
}
