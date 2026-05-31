global using NUnit.Framework;
using OpenAstroAra.Core.Utility;
using System.IO;

[SetUpFixture]
public class Init {
    [OneTimeSetUp]
    public void LoadAllNativeDlls() {
        // Phase 0.5p2 net10.0 conversion: native SOFA + NOVAS .dlls are
        // Windows-only binaries from the legacy NINA bundle. On Linux/macOS
        // hosts the equivalent .so / .dylib lookup is handled by .NET's
        // ALC + the per-arch runtime/ folders. Skip the eager pre-load
        // path off-Windows; tests requiring those natives are skipped via
        // [Platform] attributes per playbook line 1533.
        if (!System.OperatingSystem.IsWindows()) {
            return;
        }
        Logger.Info($"Preloading Native DLLs. The environment is x64: {Environment.Is64BitProcess}");
        Logger.Info(Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "External", "x64", Path.Combine("SOFA", "SOFAlib.dll")));
        DllLoader.LoadDll(Path.Combine("SOFA", "SOFAlib.dll"));
        Logger.Info(Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "External", "x64", Path.Combine("NOVAS", "NOVAS31lib.dll")));
        DllLoader.LoadDll(Path.Combine("NOVAS", "NOVAS31lib.dll"));
        Logger.Info("Native DLLs preloaded.");
    }
}