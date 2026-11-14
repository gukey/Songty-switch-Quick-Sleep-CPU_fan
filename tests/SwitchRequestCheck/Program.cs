using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;
using DisplaySwitcher;

// Exercise only the busy-request path: no window, worker, hotkey or display changes.
Environment.SetEnvironmentVariable("DISPLAY_SWITCHER_DATA_DIR",
    Path.Combine(Path.GetTempPath(), "DisplaySwitcherQueueTests", Guid.NewGuid().ToString("N")));
var type = typeof(MainWindow);
var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(type);
const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
void Set(string name, object? value) => type.GetField(name, flags)!.SetValue(window, value);
bool? Pending() => (bool?)type.GetField("pendingSwitchTv", flags)!.GetValue(window);
async Task Request(bool tv) => await (Task)type.GetMethod("Switch", flags)!.Invoke(window, [tv, false])!;
void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
Set("working", true);
Set("activeSwitchTv", true);
await Request(false);
Require(Pending() == false, "F4 during F3 is retained");
await Request(false);
Require(Pending() == false, "repeated F4 keeps a single request");
await Request(true);
Require(Pending() is null, "latest F3 cancels an obsolete queued F4");
Set("activeSwitchTv", false);
await Request(true);
Require(Pending() == true, "F3 during F4 is retained");
Set("exiting", true);
await Request(false);
Require(Pending() == true, "shutdown does not accept more requests");
