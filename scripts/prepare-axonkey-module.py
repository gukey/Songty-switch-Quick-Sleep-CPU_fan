"""Adapt a pinned AxonKey checkout to the DisplaySwitcher native-module ABI."""

import json
from pathlib import Path
import re
import subprocess
import sys


PIN = "0b5e8d7fb5bc2cf41567785c64046c60fd9cebfb"
source = Path(sys.argv[1]).resolve()
if subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip() != PIN:
    raise SystemExit(f"Expected gukey/axonkey commit {PIN}")


def original(path):
    return subprocess.check_output(["git", "show", f"HEAD:{path}"], cwd=source).decode("utf-8")


def replace(text, old, new):
    if text.count(old) != 1:
        raise ValueError(f"Unexpected AxonKey source: {old!r}")
    return text.replace(old, new, 1)


def redirect_resources(text):
    return re.sub(r"\b(app|app_for_task)\s*\.path\(\)\s*\.resource_dir\(\)",
                  r"crate::embedded_resource_dir(&\1)", text)


path = "src-tauri/src/lib.rs"
rust = original(path)
start = rust.index("pub fn run() {")
builder = rust.index("    tauri::Builder::default()", start)
rust = rust[:start] + "fn run_embedded() -> i32 {\n    tauri::Builder::default().any_thread()" + rust[builder + len("    tauri::Builder::default()") :]
start = rust.index("        .plugin(tauri_plugin_single_instance::init(", start)
end = rust.index("        .plugin(tauri_plugin_dialog::init())", start)
rust = rust[:start] + rust[end:]
start = rust.index("        .plugin(\n            tauri_plugin_updater::Builder::new()", start)
end = rust.index("        .setup(|app| {", start)
rust = rust[:start] + rust[end:]
start = rust.index("            if let Err(error) = initialize_autostart(app.handle())", start)
end = rust.index("            if let Err(error) = cli_ipc::initialize(app.handle())", start)
rust = rust[:start] + rust[end:]
start = rust.index("            if let Err(error) = install_tray(app)", start)
end = rust.index('            #[cfg(target_os = "windows")]\n            legacy_cleanup::initialize', start)
rust = rust[:start] + rust[end:]
rust = replace(rust, "            restart_app,\n", "")
rust = replace(rust, "            release_update::check_release_update,\n", "")
for command in (
    "launch_driver_action", "probe_legacy_audio_driver", "uninstall_legacy_audio_driver",
    "dismiss_microphone_driver_warning", "probe_audio_available", "probe_audio_state",
    "probe_windows_audio_driver", "restart_audio_service", "get_audio_test_state",
    "get_audio_level", "set_audio_gain", "set_audio_stretch_compensation",
    "open_signal_monitor", "get_signal_monitor_state", "export_signal_monitor", "toggle_device_recording",
):
    rust = replace(rust, f"            {command},\n", "")
rust = replace(rust, '            #[cfg(any(target_os = "windows", target_os = "macos"))]\n            recording_overlay::initialize(app.handle().clone());\n', "")
rust = replace(rust, '            #[cfg(target_os = "windows")]\n            windows_vmic::start();\n', "")
rust = replace(rust, "    recording_overlay::set_enabled(settings.show_recording_overlay)?;\n", "")
start_audio = rust.index("pub(crate) fn audio_targets(")
end_audio = rust.index("\n}\n", start_audio) + len("\n}\n")
rust = rust[:start_audio] + "pub(crate) fn audio_targets(_: &DeviceSnapshot) -> Vec<AudioTarget> { Vec::new() }\n" + rust[end_audio:]
rust = replace(rust, "app.manage(AudioServiceManager::start());", "app.manage(AudioServiceManager::buttons_only());")
rust = replace(rust, "        let microphone_driver = windows_vmic::snapshot();\n", "")
rust = replace(rust, "            microphone_driver_abnormal: microphone_driver.microphone_driver_abnormal,\n"
               "            microphone_driver_warning_suppressed: microphone_driver\n"
               "                .microphone_driver_warning_suppressed,",
               "            microphone_driver_abnormal: false,\n            microphone_driver_warning_suppressed: true,")
rust = replace(rust, "        .run(|app, event| {\n", "        .run_return(|app, event| {\n            if matches!(event, tauri::RunEvent::Ready) {\n                *EMBEDDED_APP.lock().unwrap() = Some(app.clone());\n                if EMBEDDED_STOP.load(std::sync::atomic::Ordering::Acquire) { app.exit(0); }\n            }\n")
rust = replace(rust, "                shutdown_device_services(app);\n", "                shutdown_device_services(app);\n                *EMBEDDED_APP.lock().unwrap() = None;\n")
rust = replace(rust, "        });\n}\n\n#[cfg(test)]\nmod tests", "        })\n}\n\n#[cfg(test)]\nmod tests")
rust = redirect_resources(rust)
rust += '''

fn embedded_resource_dir(app: &tauri::AppHandle) -> tauri::Result<std::path::PathBuf> {
    use tauri::Manager;
    match std::env::var_os("DISPLAY_SWITCHER_AXONKEY_RESOURCES") {
        Some(root) => Ok(root.into()),
        None => app.path().resource_dir(),
    }
}

fn ensure_embedded_drivers(app: &tauri::AppHandle) -> Result<(), String> {
    use tauri::Manager;
    #[derive(serde::Deserialize)]
    struct Config { instances: Vec<Instance> }
    #[derive(serde::Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct Instance { enabled: bool, kind: String, quarbor_target: Option<String>, binding: Option<String> }

    let config_path = app.path().app_data_dir().map_err(|e| e.to_string())?.join("device-instances.json");
    let content = match std::fs::read(config_path) {
        Ok(content) => content,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(error.to_string()),
    };
    let config: Config = serde_json::from_slice(&content).map_err(|e| e.to_string())?;
    let resources = embedded_resource_dir(app).map_err(|e| e.to_string())?;
    let logs = app.path().app_log_dir().map_err(|e| e.to_string())?;
    std::fs::create_dir_all(&logs).map_err(|e| e.to_string())?;
    let targets = config.instances.iter().filter(|instance| instance.enabled && instance.binding.is_some());
    if targets.clone().any(|instance| instance.quarbor_target.is_some()) {
        let installer = quarbor_installer::find(&resources)?;
        let detected = quarbor_installer::invoke(&installer, "list", None, None)?;
        for device in detected.devices.iter().filter(|device| device.present && !device.bound && !device.recovery_active) {
            if targets.clone().any(|instance| instance.quarbor_target.as_ref().is_some_and(|id|
                id.eq_ignore_ascii_case(&device.instance_id)) && device.matches_kind(&instance.kind)) {
                let log_path = logs.join("display-switcher-quarbor-install.log");
                std::fs::OpenOptions::new().create(true).append(true).open(&log_path)
                    .map_err(|error| error.to_string())?;
                let reply = quarbor_installer::invoke(&installer, "install", Some(&device.instance_id),
                    Some(&log_path))?;
                if reply.reboot_required { log::warn!("Quarbor input installation requires Windows restart"); }
            }
        }
    }
    Ok(())
}

static EMBEDDED_APP: std::sync::Mutex<Option<tauri::AppHandle>> = std::sync::Mutex::new(None);
static EMBEDDED_STOP: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

#[no_mangle]
pub extern "C" fn display_switcher_axonkey_run() -> i32 {
    std::panic::catch_unwind(run_embedded).unwrap_or(-1)
}

#[no_mangle]
pub extern "C" fn display_switcher_axonkey_show() -> i32 {
    let Some(app) = EMBEDDED_APP.lock().ok().and_then(|handle| handle.clone()) else { return 1; };
    let window_app = app.clone();
    if app.run_on_main_thread(move || show_main_window(&window_app)).is_ok() { 0 } else { 1 }
}

#[no_mangle]
pub extern "C" fn display_switcher_axonkey_stop() -> i32 {
    EMBEDDED_STOP.store(true, std::sync::atomic::Ordering::Release);
    if let Some(app) = EMBEDDED_APP.lock().ok().and_then(|handle| handle.clone()) {
        let exit_app = app.clone();
        if app.run_on_main_thread(move || exit_app.exit(0)).is_err() { return 1; }
    }
    0
}
'''
(source / path).write_text(rust, encoding="utf-8")

path = "src-tauri/src/audio_service/mod.rs"
rust = replace(original(path), "    fn spawn(legacy: Arc<PlatformAudioService>) -> Self {",
               "    pub fn buttons_only() -> Self {\n"
               "        Self { instances: Arc::new(InstanceServices::default()),\n"
               "            legacy: Arc::new(PlatformAudioService::without_workers()), worker: None }\n"
               "    }\n\n    fn spawn(legacy: Arc<PlatformAudioService>) -> Self {")
(source / path).write_text(rust, encoding="utf-8")

path = "src-tauri/src/devices/xiaomi_remote/voice/windows.rs"
rust = replace(original(path), "    #[cfg(test)]\n    pub(crate) fn without_workers()", "    pub(crate) fn without_workers()")
(source / path).write_text(rust, encoding="utf-8")

path = "src-tauri/src/input_service/windows.rs"
rust = original(path)
for handler in ("recording_voice", "rc902_voice"):
    start = rust.index(f"fn {handler}(\n")
    end = rust.index("\n}\n", start) + len("\n}\n")
    rust = rust[:start] + (
        f"fn {handler}(_: &QuarborContext) -> Option<&'static crate::devices::device_descriptor::VoiceDescriptor> {{\n"
        "    None\n}\n"
    ) + rust[end:]
(source / path).write_text(rust, encoding="utf-8")

path = "src-tauri/src/legacy_cleanup.rs"
(source / path).write_text(redirect_resources(original(path)), encoding="utf-8")

path = "src-tauri/src/driver_migration.rs"
rust = replace(original(path), "            crate::start_device_services(app)?;",
               "            if let Err(error) = crate::ensure_embedded_drivers(app) {\n"
               "                log::warn!(\"Automatic Quarbor setup: {error}\");\n            }\n"
               "            crate::start_device_services(app)?;")
(source / path).write_text(redirect_resources(rust), encoding="utf-8")

path = "src-tauri/capabilities/main.json"
capabilities = json.loads(original(path))
capabilities["permissions"] = [permission for permission in capabilities["permissions"]
                               if not permission.startswith(("autostart:", "updater:", "process:"))]
(source / path).write_text(json.dumps(capabilities, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

for path in ("src-tauri/src/quarbor_installer.rs",):
    rust = replace(original(path), "    let mut roots = vec![resource_dir.to_path_buf()];",
                   "    let mut roots = vec![resource_dir.to_path_buf()];\n"
                   "    if let Some(root) = std::env::var_os(\"DISPLAY_SWITCHER_AXONKEY_RESOURCES\") {\n"
                   "        roots.insert(0, PathBuf::from(root));\n    }")
    (source / path).write_text(rust, encoding="utf-8")

path = "src-tauri/src/cli_ipc.rs"
rust = replace(original(path), "pub fn cli_executable_path() -> Result<String, String> {\n",
               "pub fn cli_executable_path() -> Result<String, String> {\n"
               "    if let Some(root) = std::env::var_os(\"DISPLAY_SWITCHER_AXONKEY_RESOURCES\") {\n"
               "        return Ok(PathBuf::from(root).join(\"axonkey-cli.exe\").to_string_lossy().into_owned());\n    }\n")
(source / path).write_text(rust, encoding="utf-8")

path = "src-tauri/src/bin/axonkey-cli.rs"
rust = replace(original(path), "        .map(PathBuf::from)\n        .or_else(|| {\n            let current",
               "        .map(PathBuf::from)\n        .or_else(|| {\n"
               "            let pointer = app_config_dir().ok()?.join(\"cli\").join(\"display-switcher-host.txt\");\n"
               "            let host = PathBuf::from(fs::read_to_string(pointer).ok()?.trim());\n"
               "            (host.is_absolute() && host.is_file()).then_some(host)\n        })\n"
               "        .or_else(|| {\n            let current")
(source / path).write_text(rust, encoding="utf-8")

path = "src/components/SettingsPage.tsx"
page = replace(original(path), "import { AutostartControl } from './AutostartControl'\n", "")
page = replace(page, "<AutostartControl supported={nativeRuntime && (platform === 'macos' || platform === 'windows')} />",
               "<p>开机启动由 DisplaySwitcher 管理。</p>")
start = page.index('                  <section className="settings-form-row" aria-labelledby="settings-windows-audio-label">')
end = page.index('                </div>\n              </>\n            ) : (', start)
page = page[:start] + page[end:]
for name in ("onOpenWindowsAudioDriver", "onOpenVirtualAudioUpgrade"):
    page = replace(page, f"  {name},\n", "")
start = page.index('              <div className="settings-form-row">\n                <span className="settings-form-label">录音悬浮窗：')
end = page.index('              <div className="settings-form-row">', start + 25)
page = page[:start] + page[end:]
for name in ("showRecordingOverlay", "onShowRecordingOverlayChange"):
    page = replace(page, f"  {name},\n", "")
(source / path).write_text(page, encoding="utf-8")

path = "src/devices/integration/InstanceWorkspace.tsx"
page = replace(original(path), "  const voice = pkg.remoteView?.voice", "  const voiceFeaturesEnabled: boolean = Boolean(false)\n  const voice = voiceFeaturesEnabled ? pkg.remoteView?.voice : undefined")
start = page.index("                      {selectedInput.icon === 'mic' && (")
end = page.index("                      )}", start) + len("                      )}\n")
page = page[:start] + page[end:]
(source / path).write_text(page, encoding="utf-8")

path = "src/app/AppController.tsx"
page = replace(original(path), "useWindowsAudioDriver(nativeRuntime && platform === 'windows')", "useWindowsAudioDriver(false)")
timer = page.index("    const initialTimer = window.setTimeout(() => void probeAudioState(), 1_500)")
start = page.rindex("  useEffect(() => {", 0, timer)
end = page.index("  }, [platform])", timer) + len("  }, [platform])\n\n")
page = page[:start] + page[end:]
(source / path).write_text(page, encoding="utf-8")

path = "src/hooks/useReleaseUpdate.ts"
page = replace(original(path), "    check()\n    return () => {", "    return () => {")
page = replace(page, "    void checker.current?.check(force)", "    void force")
(source / path).write_text(page, encoding="utf-8")

path = "src/components/AboutPage.tsx"
page = original(path)
page = re.sub(r"^import .* from '\.\./releaseUpdate'\n", "", page, flags=re.MULTILINE)
page = replace(page, "import { openGitHub, openReleases }", "import { openGitHub }")
start = page.index("export function AboutPage(")
end = page.index("  return (", start)
page = page[:start] + "export function AboutPage(_: { update: ReturnType<typeof useReleaseUpdate> }) {\n" + page[end:]
start = page.index('          <section className={`about-update')
end = page.index("          <AgentConfigurationCard />", start)
page = page[:start] + '          <p>组件随 DisplaySwitcher 一起更新。</p>\n' + page[end:]
(source / path).write_text(page, encoding="utf-8")
