global using NUnit.Framework;

namespace OpenAstroAra.Test;

// SetUpFixture in the root test namespace -> applies to every test under
// OpenAstroAra.Test.* (CA1050: types must live in a namespace).
[SetUpFixture]
public class Init {
    [OneTimeSetUp]
    public void NativesAreStagedByTheBuild() {
        // The ERFA + SuperNOVAS natives are built by OpenAstroAra.Astrometry.csproj and copied
        // next to this assembly (§14e); the runtime dlopens them on the first P/Invoke. Nothing
        // to preload — tests that need them check AstrometryNatives.Probe() and self-Ignore.
    }
}